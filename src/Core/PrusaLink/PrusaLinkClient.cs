using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PrusaConnect.Core.Models;

namespace PrusaConnect.Core.PrusaLink;

/// <summary>
/// Talks to a single PrusaLink printer on the LAN using the documented
/// /api/v1 endpoints. Authenticates via the X-Api-Key header.
/// </summary>
public sealed class PrusaLinkClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _host;

    public PrusaLinkClient(string host, string apiKey, int port = 80, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _host = host;

        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(5) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
        _ownsHttpClient = true;

        _http.BaseAddress = new Uri($"http://{host}:{port}/");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrusaConnectWidget/0.1 (+windows)");
    }

    /// <summary>
    /// Current status. If a job's active, also hits /api/v1/job for the file name
    /// (status only carries progress, not the name). Errors map to
    /// PrusaLinkException subtypes.
    /// </summary>
    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        StatusResponseDto status = await GetJsonAsync<StatusResponseDto>("api/v1/status", ct)
            .ConfigureAwait(false);

        var state = MapState(status.Printer.State);
        var temps = new Temperatures
        {
            NozzleCurrent = status.Printer.TempNozzle,
            NozzleTarget = status.Printer.TargetNozzle,
            BedCurrent = status.Printer.TempBed,
            BedTarget = status.Printer.TargetBed,
        };

        JobInfo? job = null;
        if (status.Job is { } sj)
        {
            string? fileName = null;
            string? displayName = null;
            try
            {
                var jobDetails = await GetJsonAsync<JobResponseDto>("api/v1/job", ct)
                    .ConfigureAwait(false);
                fileName = jobDetails.File?.Name;
                displayName = jobDetails.File?.DisplayName;
            }
            catch (PrusaLinkException)
            {
                // /job can briefly 404 right at print start/end. we still have
                // progress from /status, just no name.
            }

            job = new JobInfo
            {
                FileName = fileName,
                DisplayName = displayName,
                ProgressPercent = sj.Progress,
                TimeRemaining = sj.TimeRemaining is { } tr ? TimeSpan.FromSeconds(tr) : null,
                TimePrinting = sj.TimePrinting is { } tp ? TimeSpan.FromSeconds(tp) : null,
            };
        }

        return new PrinterStatus
        {
            State = state,
            Temperatures = temps,
            Job = job,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    public Task<InfoResponseDto> GetInfoAsync(CancellationToken ct = default)
        => GetJsonAsync<InfoResponseDto>("api/v1/info", ct);

    /// <summary>
    /// Quick reachability + auth check: true on 200 from /status, false on 401 or
    /// network failure. Backs the settings "Test" button.
    /// </summary>
    public async Task<TestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("api/v1/status", ct).ConfigureAwait(false);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => TestResult.Ok,
                HttpStatusCode.Unauthorized => TestResult.Unauthorized,
                _ => TestResult.HttpError(resp.StatusCode),
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TestResult.Timeout;
        }
        catch (HttpRequestException ex)
        {
            return TestResult.Unreachable(ex.Message);
        }
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PrusaLinkUnreachableException(_host, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PrusaLinkUnreachableException(_host, ex);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new PrusaLinkUnauthorizedException();
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new PrusaLinkHttpException(resp.StatusCode,
                    $"PrusaLink {path} returned HTTP {(int)resp.StatusCode}");
            }

            T? result = await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            return result ?? throw new PrusaLinkException(
                $"PrusaLink {path} returned no body or an unparseable response.");
        }
    }

    internal static PrinterState MapState(string wire) => wire?.ToUpperInvariant() switch
    {
        "IDLE" => PrinterState.Idle,
        "BUSY" => PrinterState.Busy,
        "READY" => PrinterState.Ready,
        "PRINTING" => PrinterState.Printing,
        "PAUSED" => PrinterState.Paused,
        "FINISHED" => PrinterState.Finished,
        "STOPPED" => PrinterState.Stopped,
        "ERROR" => PrinterState.Error,
        "ATTENTION" => PrinterState.Attention,
        _ => PrinterState.Unknown,
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

public readonly record struct TestResult(TestResultKind Kind, string? Detail = null, HttpStatusCode? StatusCode = null)
{
    public static TestResult Ok => new(TestResultKind.Ok);
    public static TestResult Unauthorized => new(TestResultKind.Unauthorized);
    public static TestResult Timeout => new(TestResultKind.Timeout);
    public static TestResult Unreachable(string detail) => new(TestResultKind.Unreachable, detail);
    public static TestResult HttpError(HttpStatusCode code) => new(TestResultKind.HttpError, null, code);
}

public enum TestResultKind
{
    Ok,
    Unauthorized,
    Timeout,
    Unreachable,
    HttpError,
}
