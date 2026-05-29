using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PrusaConnect.Core.Models;
using PrusaConnect.Core.PrusaConnect;
using PrusaConnect.Core.PrusaLink;
using PrusaConnect.Core.Storage;

namespace PrusaConnect.Core.Aggregator;

public enum StatusSource
{
    Unknown,
    PrusaLink,
    PrusaConnect,
}

public sealed record PrinterStatusResult(PrinterStatus Status, StatusSource Source);

/// <summary>
/// Gets a printer's status from whichever backend answers: PrusaLink on the LAN
/// (fast, detailed, no token) or Prusa Connect cloud (for printers off the
/// current network, via the borrowed PrusaSlicer token). Callers pass the
/// last-good source so a remote printer doesn't eat the LAN timeout every poll.
/// </summary>
public sealed class PrinterStatusService
{
    private readonly ISecretsStore _secrets;
    private readonly ConnectTokenCache _connectTokens;
    private readonly ConnectAccount _cloudAccount;

    public PrinterStatusService(ISecretsStore secrets, ConnectTokenCache connectTokens, ConnectAccount cloudAccount)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _connectTokens = connectTokens ?? throw new ArgumentNullException(nameof(connectTokens));
        _cloudAccount = cloudAccount ?? throw new ArgumentNullException(nameof(cloudAccount));
    }

    public async Task<PrinterStatusResult> GetStatusAsync(
        PrinterInfo printer, StatusSource preferred, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        Exception? lastError = null;

        foreach (var source in OrderSources(printer, preferred))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (source)
                {
                    case StatusSource.PrusaLink:
                    {
                        string? apiKey = _secrets.Get(printer.Id);
                        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrWhiteSpace(printer.Host))
                        {
                            continue;
                        }
                        using var client = new PrusaLinkClient(printer.Host, apiKey, printer.Port);
                        var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
                        return new PrinterStatusResult(status, StatusSource.PrusaLink);
                    }
                    case StatusSource.PrusaConnect:
                    {
                        string? uuid = EffectiveConnectUuid(printer);
                        if (uuid is null)
                        {
                            continue;
                        }
                        using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
                        var status = await client.GetStatusAsync(uuid, ct).ConfigureAwait(false);
                        return new PrinterStatusResult(status, StatusSource.PrusaConnect);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        // nothing was even tried (no LAN creds, no UUID)
        throw new PrusaLinkUnauthorizedException();
    }

    /// <summary>
    /// Which Connect UUID to poll. Prefers ConnectUuid, else the printer Id when
    /// it's a dashed GUID - imported printers use the Connect UUID as their Id,
    /// so this still works for ones imported before ConnectUuid existed. Manual
    /// printers get a dash-less GUID, so the dash check keeps them LAN-only.
    /// </summary>
    private static string? EffectiveConnectUuid(PrinterInfo printer)
    {
        if (!string.IsNullOrEmpty(printer.ConnectUuid))
        {
            return printer.ConnectUuid;
        }
        if (!string.IsNullOrEmpty(printer.Id) && printer.Id.Contains('-') && Guid.TryParse(printer.Id, out _))
        {
            return printer.Id;
        }
        return null;
    }

    /// <summary>
    /// Latest Connect camera still for a printer; null if there's no Connect UUID
    /// / camera / stored still, or cloud's down. The large tile uses it.
    /// </summary>
    public async Task<CameraSnapshot?> GetCameraSnapshotAsync(PrinterInfo printer, CancellationToken ct)
    {
        string? uuid = EffectiveConnectUuid(printer);
        if (uuid is null) return null;
        try
        {
            using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
            return await client.GetPrimaryCameraSnapshotAsync(uuid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrusaConnectException) { return null; }
    }

    /// <summary>
    /// Farm teams (orgs) the account can monitor - fills the team picker in
    /// settings. Empty if cloud's down.
    /// </summary>
    public async Task<List<(string Id, string Name)>> GetFarmOrganizationsAsync(CancellationToken ct)
    {
        try
        {
            using var gql = new FarmGraphQlClient(_cloudAccount, _connectTokens);
            return await gql.GetOrganizationsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrusaConnectException) { return new List<(string, string)>(); }
    }

    /// <summary>
    /// Fleet health for one farm team. Pass the org id; null/empty falls back to
    /// the first team, and with no farm org at all, to the account-wide
    /// /slicer/status poll. null if cloud's down / not signed in. Feeds Farm Status.
    /// </summary>
    public async Task<FarmSummary?> GetFarmSummaryAsync(string? organizationId, CancellationToken ct)
    {
        try
        {
            using var gql = new FarmGraphQlClient(_cloudAccount, _connectTokens);
            var (orgId, orgName) = await ResolveOrgAsync(gql, organizationId, ct).ConfigureAwait(false);

            List<string> states;
            if (orgId is not null)
            {
                states = await gql.GetOrgPrinterStatesAsync(orgId, ct).ConfigureAwait(false);
            }
            else
            {
                // no farm org - fall back to the account-wide poll
                using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
                var items = await client.GetAllStatusAsync(ct).ConfigureAwait(false);
                states = items.ConvertAll(i => i.PrinterState ?? string.Empty);
            }

            int printing = 0, idle = 0, attention = 0, offline = 0;
            foreach (var st in states)
            {
                switch ((st ?? string.Empty).ToUpperInvariant())
                {
                    case "PRINTING":
                        printing++; break;
                    case "IDLE":
                    case "READY":
                    case "FINISHED":
                    case "STOPPED":
                        idle++; break;
                    case "ATTENTION":
                    case "ERROR":
                        attention++; break;
                    case "OFFLINE":
                        offline++; break;
                }
            }
            return new FarmSummary(states.Count, printing, idle, attention, offline, orgName);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrusaConnectException) { return null; }
    }

    /// <summary>
    /// Connect Farm orders (with progress) for one team. Pass the org id;
    /// null/empty falls back to the first team. null if cloud's down / not signed
    /// in / no farm org.
    /// </summary>
    public async Task<FarmOrders?> GetFarmOrdersAsync(string? organizationId, CancellationToken ct)
    {
        try
        {
            using var gql = new FarmGraphQlClient(_cloudAccount, _connectTokens);
            var (orgId, orgName) = await ResolveOrgAsync(gql, organizationId, ct).ConfigureAwait(false);
            if (orgId is null) return null;
            var orders = await gql.GetOrdersAsync(orgId, ct).ConfigureAwait(false);
            return new FarmOrders(orgName ?? "Farm", orders);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrusaConnectException) { return null; }
    }

    /// <summary>
    /// Pick the org to use: the requested team if it still exists, else the first.
    /// (null, null) if the account has no farm org.
    /// </summary>
    private static async Task<(string? Id, string? Name)> ResolveOrgAsync(
        FarmGraphQlClient gql, string? requestedId, CancellationToken ct)
    {
        var orgs = await gql.GetOrganizationsAsync(ct).ConfigureAwait(false);
        if (orgs.Count == 0) return (null, null);

        if (!string.IsNullOrEmpty(requestedId))
        {
            var match = orgs.FirstOrDefault(o => string.Equals(o.Id, requestedId, StringComparison.OrdinalIgnoreCase));
            if (match.Id is not null) return (match.Id, match.Name);
        }
        return (orgs[0].Id, orgs[0].Name);
    }

    /// <summary>
    /// Regular Connect teams the account can monitor (from /app/printers, grouped
    /// by team name) - fills the Team Status picker. No Connect Farm needed. Empty
    /// if cloud's down.
    /// </summary>
    public async Task<List<(string Id, string Name)>> GetConnectTeamsAsync(CancellationToken ct)
    {
        try
        {
            using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
            var printers = await client.ListAppPrintersAsync(ct).ConfigureAwait(false);
            return printers
                .GroupBy(p => TeamKey(p), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => string.Equals(g.Key, PersonalTeam, StringComparison.Ordinal))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Id: g.Key, Name: g.Key))
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (PrusaConnectException) { return new List<(string, string)>(); }
    }

    /// <summary>
    /// Fleet health for one regular Connect team, from /app/printers (membership
    /// + live state in one call - no Farm needed). Pass the team name; null/empty
    /// = whole account. null if cloud's down. Feeds Team Status.
    /// </summary>
    public async Task<FarmSummary?> GetTeamSummaryAsync(string? teamKey, CancellationToken ct)
    {
        try
        {
            using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
            var printers = await client.ListAppPrintersAsync(ct).ConfigureAwait(false);
            if (printers.Count == 0) return null;

            IEnumerable<AppPrinterDto> scoped = printers;
            string teamName = "All printers";
            if (!string.IsNullOrEmpty(teamKey))
            {
                var match = printers.Where(p => string.Equals(TeamKey(p), teamKey, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count > 0) { scoped = match; teamName = teamKey!; }
            }

            var (total, printing, idle, attention, offline) =
                CountStates(scoped.Select(p => p.State ?? p.PrinterState ?? p.ConnectState));
            return new FarmSummary(total, printing, idle, attention, offline, teamName);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrusaConnectException) { return null; }
    }

    private const string PersonalTeam = "(Personal)";
    private static string TeamKey(AppPrinterDto p) =>
        string.IsNullOrWhiteSpace(p.TeamName) ? PersonalTeam : p.TeamName!;

    /// <summary>Bucket a set of Connect printer-state strings into fleet counts.</summary>
    private static (int Total, int Printing, int Idle, int Attention, int Offline) CountStates(IEnumerable<string?> states)
    {
        int total = 0, printing = 0, idle = 0, attention = 0, offline = 0;
        foreach (var st in states)
        {
            total++;
            switch ((st ?? string.Empty).ToUpperInvariant())
            {
                case "PRINTING": printing++; break;
                case "IDLE":
                case "READY":
                case "FINISHED":
                case "STOPPED": idle++; break;
                case "ATTENTION":
                case "ERROR": attention++; break;
                case "OFFLINE": offline++; break;
            }
        }
        return (total, printing, idle, attention, offline);
    }

    private static IEnumerable<StatusSource> OrderSources(PrinterInfo printer, StatusSource preferred)
    {
        bool hasLan = !string.IsNullOrWhiteSpace(printer.Host);
        bool hasConnect = EffectiveConnectUuid(printer) is not null;

        var ordered = new List<StatusSource>(2);

        // cloud worked last time? try it first, skip the LAN timeout
        if (preferred == StatusSource.PrusaConnect && hasConnect)
        {
            ordered.Add(StatusSource.PrusaConnect);
        }

        if (hasLan)
        {
            ordered.Add(StatusSource.PrusaLink);
        }

        if (hasConnect && !ordered.Contains(StatusSource.PrusaConnect))
        {
            ordered.Add(StatusSource.PrusaConnect);
        }

        return ordered;
    }
}
