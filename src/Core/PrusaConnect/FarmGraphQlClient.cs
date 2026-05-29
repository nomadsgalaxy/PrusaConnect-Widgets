using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// Small GraphQL client for the Prusa Connect Farm API. Same bearer as
/// <see cref="PrusaConnectClient"/> (refresh + retry once on a 401). Server-side,
/// so no CORS. Queries are the ones the Farm web app sends (captured 2026-05-28).
/// </summary>
public sealed class FarmGraphQlClient : IDisposable
{
    public const string DefaultGraphQlUrl = "https://connect-api.prusa3d.com/graphql";

    private const string OrganizationsQuery =
        "query Organizations { authorization { organizations(first: 50) { edges { node { id name } } } } }";

    private const string OrdersQuery =
        "query Queue($organizationId: UUID!) { order { orders(organizationId: $organizationId) { " +
        "edges { node { id name number state estimatedCompletionDate " +
        "jobCounts { created printing done cancelled } " +
        "items { quantity jobsCountCompleted jobs { state printerId } } } } } } }";

    // one poll of the org's printers -> each one's current job (name + time
    // left). currentJob is a union; PrinterJob is the variant with timeRemaining.
    private const string PrintersQuery =
        "query FleetCurrentJobs { printer { printers { ... on PrinterConnection { " +
        "edges { node { ... on Printer { id name state " +
        "currentJob { ... on PrinterJob { id timeRemaining } } } } } } } } }";

    // org-scoped printer states, for the org-filtered Farm Status counts
    private const string OrgPrinterStatesQuery =
        "query OrgPrinterStates($organizationId: UUID!) { printer { printers(organizationId: $organizationId) { " +
        "... on PrinterConnection { edges { node { ... on Printer { state } } } } } } }";

    private readonly ConnectAccount _account;
    private readonly ConnectTokenCache _tokens;
    private readonly string _url;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public FarmGraphQlClient(ConnectAccount account, ConnectTokenCache tokens, string? graphQlUrl = null, HttpClient? http = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _url = string.IsNullOrWhiteSpace(graphQlUrl) ? DefaultGraphQlUrl : graphQlUrl!;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttpClient = http is null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrusaConnectWidget/0.1 (+windows)");
    }

    /// <summary>Returns (id, name) of the user's first organization, or null.</summary>
    public async Task<(string Id, string Name)?> GetDefaultOrganizationAsync(CancellationToken ct)
    {
        using var data = await PostAsync(OrganizationsQuery, null, ct).ConfigureAwait(false);
        if (TryNavigate(data.RootElement, out var root,
                "data", "authorization", "organizations", "edges")
            && root.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in root.EnumerateArray())
            {
                if (edge.TryGetProperty("node", out var node)
                    && node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    string name = node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString()! : "Farm";
                    return (id.GetString()!, name);
                }
            }
        }
        return null;
    }

    /// <summary>All organizations (farm teams) the account can access.</summary>
    public async Task<List<(string Id, string Name)>> GetOrganizationsAsync(CancellationToken ct)
    {
        var result = new List<(string Id, string Name)>();
        using var data = await PostAsync(OrganizationsQuery, null, ct).ConfigureAwait(false);
        if (TryNavigate(data.RootElement, out var edges,
                "data", "authorization", "organizations", "edges")
            && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("node", out var node)
                    && node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    string name = node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(n.GetString())
                        ? n.GetString()! : "Farm";
                    result.Add((id.GetString()!, name));
                }
            }
        }
        return result;
    }

    /// <summary>Printer states (PRINTING / IDLE / …) for one organization.</summary>
    public async Task<List<string>> GetOrgPrinterStatesAsync(string organizationId, CancellationToken ct)
    {
        var states = new List<string>();
        var vars = new Dictionary<string, object?> { ["organizationId"] = organizationId };
        using var data = await PostAsync(OrgPrinterStatesQuery, vars, ct).ConfigureAwait(false);
        if (TryNavigate(data.RootElement, out var edges, "data", "printer", "printers", "edges")
            && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("node", out var node))
                {
                    string? s = GetStr(node, "state");
                    if (!string.IsNullOrEmpty(s)) states.Add(s!);
                }
            }
        }
        return states;
    }

    public async Task<List<FarmOrder>> GetOrdersAsync(string organizationId, CancellationToken ct)
    {
        var vars = new Dictionary<string, object?> { ["organizationId"] = organizationId };
        using var data = await PostAsync(OrdersQuery, vars, ct).ConfigureAwait(false);

        // pass 1: parse orders, noting which printerIds are printing so we can
        // resolve them to live printers in one bulk query after.
        var parsed = new List<(FarmOrder Order, List<string> PrintingPrinterIds)>();
        if (TryNavigate(data.RootElement, out var edges, "data", "order", "orders", "edges")
            && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out var node)) continue;
                string id = GetStr(node, "id") ?? string.Empty;
                if (id.Length == 0) continue;
                string name = GetStr(node, "name") ?? "(unnamed order)";
                string? number = GetScalar(node, "number"); // JSON int or string
                string? state = GetStr(node, "state");

                // total = sum of item quantities. the live breakdown comes from
                // the real per-job states in items[].jobs, NOT jobCounts /
                // jobsCountCompleted - those come back all-zero on the live API
                // (checked 2026-05-29: a COMPLETED 1-pc order and a 4/30 order
                // both had jobCounts {0,0,0,0} while items[].jobs had the states).
                int total = 0, done = 0, printing = 0, cancelled = 0;
                var printingPrinterIds = new List<string>();
                if (node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        total += GetInt(item, "quantity");

                        if (item.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var job in jobs.EnumerateArray())
                            {
                                switch ((GetStr(job, "state") ?? string.Empty).ToUpperInvariant())
                                {
                                    case "DONE":
                                        done++;
                                        break;
                                    case "PRINTING":
                                        printing++;
                                        string? pid = GetStr(job, "printerId");
                                        if (!string.IsNullOrEmpty(pid)) printingPrinterIds.Add(pid!);
                                        break;
                                    case "CANCELLED":
                                    case "CANCELED":
                                        cancelled++;
                                        break;
                                }
                            }
                        }
                    }
                }

                // no materialized jobs yet? fall back to order-level jobCounts
                // (some farms may fill it even when items[].jobs is empty)
                if (done == 0 && printing == 0 && cancelled == 0
                    && node.TryGetProperty("jobCounts", out var jc) && jc.ValueKind == JsonValueKind.Object)
                {
                    done = GetInt(jc, "done");
                    printing = GetInt(jc, "printing");
                    cancelled = GetInt(jc, "cancelled");
                }

                // a finished order keeps its terminal state after the farm stops
                // materializing jobs - call it fully done, not a stale partial
                if (total > 0
                    && (string.Equals(state, "COMPLETED", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase)))
                {
                    done = total;
                }

                DateTimeOffset? eta = GetDate(node, "estimatedCompletionDate");

                parsed.Add((
                    new FarmOrder(id, name, number, total, done, printing, cancelled, eta, state),
                    printingPrinterIds));
            }
        }

        // pass 2: one bulk fleet query gets each printing printer's name + time
        // left; hang the current prints off each order.
        bool anyPrinting = parsed.Exists(p => p.PrintingPrinterIds.Count > 0);
        var fleet = anyPrinting
            ? await GetFleetCurrentJobsAsync(ct).ConfigureAwait(false)
            : new Dictionary<string, FarmCurrentPrint>(0);

        var orders = new List<FarmOrder>(parsed.Count);
        foreach (var (order, ids) in parsed)
        {
            if (ids.Count == 0 || fleet.Count == 0) { orders.Add(order); continue; }

            var prints = new List<FarmCurrentPrint>(ids.Count);
            foreach (var pid in ids)
                if (fleet.TryGetValue(pid, out var cp)) prints.Add(cp);

            orders.Add(prints.Count > 0 ? order with { CurrentPrints = prints } : order);
        }
        return orders;
    }

    /// <summary>
    /// One poll of the org's printers -> map of printerId -> current print (name
    /// + time left) for whatever's printing. Best-effort: on failure returns an
    /// empty map instead of sinking the whole orders fetch.
    /// </summary>
    private async Task<Dictionary<string, FarmCurrentPrint>> GetFleetCurrentJobsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, FarmCurrentPrint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var data = await PostAsync(PrintersQuery, null, ct).ConfigureAwait(false);
            if (TryNavigate(data.RootElement, out var edges, "data", "printer", "printers", "edges")
                && edges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    if (!edge.TryGetProperty("node", out var node)) continue;
                    string? pid = GetStr(node, "id");
                    if (string.IsNullOrEmpty(pid)) continue;

                    string pname = GetStr(node, "name") ?? "Printer";
                    int? remaining = null;
                    if (node.TryGetProperty("currentJob", out var cj) && cj.ValueKind == JsonValueKind.Object
                        && cj.TryGetProperty("timeRemaining", out var tr) && tr.TryGetInt32(out int s))
                    {
                        remaining = s;
                    }
                    map[pid!] = new FarmCurrentPrint(pname, remaining);
                }
            }
        }
        catch (PrusaConnectException) { /* enrichment is best-effort */ }
        return map;
    }

    private async Task<JsonDocument> PostAsync(string query, object? variables, CancellationToken ct)
    {
        var resp = await SendAsync(query, variables, refresh: false, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            resp = await SendAsync(query, variables, refresh: true, ct).ConfigureAwait(false);
        }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new PrusaConnectAuthRequiredException();
            if (!resp.IsSuccessStatusCode)
                throw new PrusaConnectHttpException(resp.StatusCode, $"Farm GraphQL returned HTTP {(int)resp.StatusCode}.");
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // the web app batches (array in -> array out); a single object
            // request gets a single object back. if it's an array, unwrap [0].
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var first = doc.RootElement.EnumerateArray().GetEnumerator();
                if (first.MoveNext())
                {
                    var inner = JsonDocument.Parse(first.Current.GetRawText());
                    doc.Dispose();
                    return inner;
                }
            }
            return doc;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string query, object? variables, bool refresh, CancellationToken ct)
    {
        if (refresh) ConnectTokenCacheBackdoor.ForceExpire(_account.Id);
        string token = await _tokens.GetAccessTokenAsync(_account, ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object?> { ["query"] = query };
        if (variables is not null) payload["variables"] = variables;

        var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new PrusaConnectUnreachableException(_url, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new PrusaConnectUnreachableException(_url, ex); }
    }

    private static bool TryNavigate(JsonElement root, out JsonElement result, params string[] path)
    {
        result = root;
        foreach (var key in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(key, out result))
            {
                result = default;
                return false;
            }
        }
        return true;
    }

    private static string? GetStr(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>String form of a scalar that may arrive as a JSON number or string.</summary>
    private static string? GetScalar(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static int GetInt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.TryGetInt32(out int i) ? i : 0;

    private static DateTimeOffset? GetDate(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt)
            ? dt : null;

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
