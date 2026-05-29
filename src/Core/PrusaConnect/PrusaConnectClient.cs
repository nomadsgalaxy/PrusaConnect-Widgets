using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PrusaConnect.Core.Models;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// REST client for one Prusa Connect instance. Auth is the cached bearer from
/// <see cref="ConnectTokenCache"/>; retries once after a refresh on a 401.
/// </summary>
public sealed class PrusaConnectClient : IDisposable
{
    // loose parsing - Connect's shapes drift between states/firmware and numbers
    // sometimes arrive as strings. don't let a field we don't even use break it.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly ConnectAccount _account;
    private readonly ConnectTokenCache _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public PrusaConnectClient(ConnectAccount account, ConnectTokenCache tokens, HttpClient? http = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttpClient = http is null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrusaConnectWidget/0.1 (+windows)");
    }

    public ConnectAccount Account => _account;

    /// <summary>One cheap call: live state of every printer. Fine to poll often.</summary>
    public Task<List<SlicerStatusItemDto>> GetAllStatusAsync(CancellationToken ct = default)
        => GetJsonAsync<List<SlicerStatusItemDto>>("/slicer/status", ct);

    /// <summary>The user's printer fleet (uuid + model + tools metadata).</summary>
    public async Task<List<SlicerPrinterDto>> ListPrintersAsync(CancellationToken ct = default)
    {
        var resp = await GetJsonAsync<SlicerPrintersResponseDto>("/slicer/v1/printers", ct)
            .ConfigureAwait(false);
        return resp.Printers;
    }

    /// <summary>
    /// Fleet list from <c>/app/printers</c> - like <see cref="ListPrintersAsync"/>
    /// but carries team_name/team_id so we can group by team. Walks every page
    /// (5000-printer safety stop).
    /// </summary>
    public async Task<List<AppPrinterDto>> ListAppPrintersAsync(CancellationToken ct = default)
    {
        const int pageSize = 100;
        const int safetyBound = 5000;

        var all = new List<AppPrinterDto>();
        int offset = 0;

        while (offset < safetyBound)
        {
            var page = await GetJsonAsync<AppPrintersResponseDto>(
                $"/app/printers?limit={pageSize}&offset={offset}", ct).ConfigureAwait(false);

            if (page.Printers.Count == 0)
            {
                break;
            }

            all.AddRange(page.Printers);

            // got everything the server says exists
            if (page.Pager?.Total is { } total && all.Count >= total)
            {
                break;
            }

            offset += pageSize;
        }

        return all;
    }

    /// <summary>
    /// Everything about one printer - state, temps, job - plus the PrusaLink
    /// API key + LAN IP, which is how we auto-import LAN creds.
    /// </summary>
    public Task<ConnectPrinterDetailDto> GetPrinterDetailAsync(string uuid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        return GetJsonAsync<ConnectPrinterDetailDto>($"/app/printers/{uuid}", ct);
    }

    /// <summary>
    /// Detail blob -> the <see cref="PrinterStatus"/> shape the rest of the app
    /// uses, so rendering doesn't care which backend it came from.
    /// </summary>
    public async Task<PrinterStatus> GetStatusAsync(string uuid, CancellationToken ct = default)
    {
        var detail = await GetPrinterDetailAsync(uuid, ct).ConfigureAwait(false);
        return DetailMapper.ToPrinterStatus(detail);
    }

    /// <summary>Cameras registered for a printer. Empty if none / 404.</summary>
    public async Task<IReadOnlyList<ConnectCameraDto>> GetCamerasAsync(string uuid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        string url = _account.ConnectBaseUrl.TrimEnd('/') + "/app/printers/" + uuid + "/cameras";
        using var resp = await SendWithRetryAsync(url, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<ConnectCameraDto>();
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new PrusaConnectAuthRequiredException();
        if (!resp.IsSuccessStatusCode)
            throw new PrusaConnectHttpException(resp.StatusCode, $"Prusa Connect cameras returned HTTP {(int)resp.StatusCode}.");

        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<ConnectCameraDto>>(text, JsonOptions)
                ?? (IReadOnlyList<ConnectCameraDto>)Array.Empty<ConnectCameraDto>();
        }
        var wrap = JsonSerializer.Deserialize<ConnectCamerasResponseDto>(text, JsonOptions);
        return wrap?.Cameras ?? (IReadOnlyList<ConnectCameraDto>)Array.Empty<ConnectCameraDto>();
    }

    /// <summary>
    /// Latest stored still for a camera. null on 404 (offline, or a WebRTC-only
    /// camera that never pushed a frame). CapturedAt is the Last-Modified header.
    /// </summary>
    public async Task<CameraSnapshot?> GetLastSnapshotAsync(string uuid, long cameraId, CancellationToken ct = default)
    {
        string url = _account.ConnectBaseUrl.TrimEnd('/')
            + "/app/cameras/" + cameraId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/snapshots/last?printer_uuid=" + Uri.EscapeDataString(uuid);
        using var resp = await SendWithRetryAsync(url, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new PrusaConnectAuthRequiredException();
        if (!resp.IsSuccessStatusCode)
            throw new PrusaConnectHttpException(resp.StatusCode, $"Prusa Connect snapshot returned HTTP {(int)resp.StatusCode}.");

        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new CameraSnapshot(bytes, resp.Content.Headers.LastModified);
    }

    /// <summary>
    /// Latest still from the printer's main camera (first registered, else
    /// lowest sort_order). null if there's no camera or nothing stored.
    /// </summary>
    public async Task<CameraSnapshot?> GetPrimaryCameraSnapshotAsync(string uuid, CancellationToken ct = default)
    {
        var cameras = await GetCamerasAsync(uuid, ct).ConfigureAwait(false);
        if (cameras.Count == 0) return null;

        ConnectCameraDto cam = null!;
        foreach (var c in cameras) { if (c.Registered == true) { cam = c; break; } }
        if (cam is null)
        {
            cam = cameras[0];
            foreach (var c in cameras)
            {
                if ((c.SortOrder ?? int.MaxValue) < (cam.SortOrder ?? int.MaxValue)) cam = c;
            }
        }
        return await GetLastSnapshotAsync(uuid, cam.Id, ct).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        string url = _account.ConnectBaseUrl.TrimEnd('/') + path;
        using var resp = await SendWithRetryAsync(url, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PrusaConnectAuthRequiredException();
        }
        if (!resp.IsSuccessStatusCode)
        {
            throw new PrusaConnectHttpException(resp.StatusCode,
                $"Prusa Connect {path} returned HTTP {(int)resp.StatusCode}.");
        }

        T? value = await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            .ConfigureAwait(false);
        return value ?? throw new PrusaConnectException(
            $"Prusa Connect {path} returned an unparseable or empty response.");
    }

    /// <summary>GET with the cached bearer; one transparent refresh+retry on 401.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        var resp = await SendWithTokenAsync(url, refresh: false, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            resp = await SendWithTokenAsync(url, refresh: true, ct).ConfigureAwait(false);
        }
        return resp;
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(string url, bool refresh, CancellationToken ct)
    {
        // refresh==true -> drop the cached token so the cache re-fetches it
        string token;
        if (refresh)
        {
            ConnectTokenCacheBackdoor.ForceExpire(_account.Id);
        }
        token = await _tokens.GetAccessTokenAsync(_account, ct).ConfigureAwait(false);

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PrusaConnectUnreachableException(_account.ConnectBaseUrl, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PrusaConnectUnreachableException(_account.ConnectBaseUrl, ex);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>
/// Escape hatch to force a refresh without making the token-cache dictionary
/// public.
/// </summary>
internal static class ConnectTokenCacheBackdoor
{
    public static void ForceExpire(string accountId)
    {
        // can't touch the readonly cache directly - just evict the token so the
        // next GetAccessTokenAsync misses and takes the refresh path
        var field = typeof(ConnectTokenCache)
            .GetField("_live", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is System.Collections.Concurrent.ConcurrentDictionary<string, TokenSet> dict)
        {
            dict.TryRemove(accountId, out _);
        }
    }
}
