using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PrusaConnect.Core.Models;
using PrusaConnect.Core.PrusaLink;

namespace PrusaConnect.Core.Moonraker;

/// <summary>
/// Talks to a Klipper printer through its Moonraker API (the server behind
/// Mainsail / Fluidd, and what Prusa's HT90 runs). One objects/query gives state,
/// temps, and job progress. Moonraker on a LAN is usually open; an optional API
/// key rides along as X-Api-Key for locked-down setups.
/// </summary>
public sealed class MoonrakerClient : IDisposable
{
    private const string Query =
        "printer/objects/query?print_stats&heater_bed&extruder&display_status&virtual_sdcard";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _host;

    public MoonrakerClient(string host, int port = 80, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(5) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
        _ownsHttpClient = true;
        _http.BaseAddress = new Uri($"http://{host}:{port}/");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrusaConnectWidget/0.1 (+windows)");
    }

    /// <summary>
    /// Current status, mapped onto the same <see cref="PrinterStatus"/> the rest
    /// of the app uses. Time-left is estimated from elapsed print time and
    /// progress (Klipper doesn't always report a slicer ETA).
    /// </summary>
    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync(Query, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("status", out var status))
        {
            throw new MoonrakerException("Moonraker returned no status object.");
        }

        string stateWire = GetStr(status, "print_stats", "state") ?? string.Empty;
        var temps = new Temperatures
        {
            NozzleCurrent = GetNum(status, "extruder", "temperature"),
            NozzleTarget = GetNum(status, "extruder", "target"),
            BedCurrent = GetNum(status, "heater_bed", "temperature"),
            BedTarget = GetNum(status, "heater_bed", "target"),
        };

        JobInfo? job = null;
        string? filename = GetStr(status, "print_stats", "filename");
        bool active = stateWire.Equals("printing", StringComparison.OrdinalIgnoreCase)
                   || stateWire.Equals("paused", StringComparison.OrdinalIgnoreCase);
        if (active && !string.IsNullOrEmpty(filename))
        {
            // progress: prefer the slicer/display value, fall back to file position.
            double progress = GetNum(status, "display_status", "progress")
                              ?? GetNum(status, "virtual_sdcard", "progress") ?? 0;
            double printed = GetNum(status, "print_stats", "print_duration") ?? 0;

            TimeSpan? remaining = null;
            if (progress > 0.001 && progress < 1.0 && printed > 0)
            {
                double total = printed / progress;
                remaining = TimeSpan.FromSeconds(Math.Max(0, total - printed));
            }

            job = new JobInfo
            {
                FileName = TrimFilename(filename!),
                DisplayName = TrimFilename(filename!),
                ProgressPercent = Math.Round(progress * 100, 0),
                TimeRemaining = remaining,
                TimePrinting = printed > 0 ? TimeSpan.FromSeconds(printed) : null,
            };
        }

        return new PrinterStatus
        {
            State = MapState(stateWire),
            Temperatures = temps,
            Job = job,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Reachability + auth check for the settings "Test" button.</summary>
    public async Task<TestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("server/info", ct).ConfigureAwait(false);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => TestResult.Ok,
                HttpStatusCode.Unauthorized => TestResult.Unauthorized,
                _ => TestResult.HttpError(resp.StatusCode),
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return TestResult.Timeout; }
        catch (HttpRequestException ex) { return TestResult.Unreachable(ex.Message); }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(path, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new MoonrakerUnreachableException(_host, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new MoonrakerUnreachableException(_host, ex); }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new MoonrakerException("Moonraker rejected the request (401) - it needs an API key.");
            if (!resp.IsSuccessStatusCode)
                throw new MoonrakerException($"Moonraker {path} returned HTTP {(int)resp.StatusCode}.");
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(body);
        }
    }

    internal static PrinterState MapState(string wire) => wire?.ToLowerInvariant() switch
    {
        "printing" => PrinterState.Printing,
        "paused" => PrinterState.Paused,
        "complete" => PrinterState.Finished,
        "cancelled" => PrinterState.Stopped,
        "standby" => PrinterState.Idle,
        "ready" => PrinterState.Idle,
        "error" => PrinterState.Error,
        _ => PrinterState.Unknown,
    };

    private static string TrimFilename(string f)
    {
        int slash = f.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? f.Substring(slash + 1) : f;
    }

    private static string? GetStr(JsonElement status, string obj, string prop)
        => status.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
           && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? GetNum(JsonElement status, string obj, string prop)
        => status.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
           && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}

public class MoonrakerException : Exception
{
    public MoonrakerException(string message) : base(message) { }
    public MoonrakerException(string message, Exception inner) : base(message, inner) { }
}

public sealed class MoonrakerUnreachableException : MoonrakerException
{
    public MoonrakerUnreachableException(string host, Exception inner)
        : base($"Couldn't reach Moonraker at {host}.", inner) { }
}
