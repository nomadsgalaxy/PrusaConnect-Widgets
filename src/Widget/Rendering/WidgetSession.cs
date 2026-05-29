using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.Widgets.Providers;
using PrusaConnect.Core.Aggregator;
using PrusaConnect.Core.Models;
using PrusaConnect.Core.PrusaConnect;
using PrusaConnect.Core.PrusaLink;
using PrusaConnect.Core.Storage;
using PrusaConnect.Widget.Diagnostics;

namespace PrusaConnect.Widget.Rendering;

/// <summary>
/// Per-pinned-widget state: which printer + TileKind this tile shows, an
/// adaptive poll loop, and pushing updates to the host. Status comes from
/// <see cref="PrinterStatusService"/> (PrusaLink LAN, then Connect cloud).
/// </summary>
internal sealed class WidgetSession : IDisposable
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrintingPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly PrinterStore _printers;
    private readonly ISecretsStore _secrets;
    private readonly PinnedWidgetStore _pinnedStore;
    private readonly RebindRequestStore _rebindStore;
    private readonly PrinterStatusService _statusService;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private PrinterState _lastState = PrinterState.Unknown;
    private string? _boundPrinterId;
    private TileKind _kind = TileKind.PrinterStatus;
    private StatusSource _lastGoodSource = StatusSource.Unknown;
    private string _size = "medium";

    // Camera snapshot cache (large tiles only) - snapshots barely change, so
    // we refetch at most every 30s to avoid re-pulling ~280KB each poll.
    private static readonly TimeSpan CameraCacheTtl = TimeSpan.FromSeconds(30);
    private string? _camUri;
    private DateTimeOffset? _camCapturedAt;
    private DateTimeOffset _camFetchedAt = DateTimeOffset.MinValue;

    public WidgetSession(
        string id, string definitionId,
        PrinterStore printers, ISecretsStore secrets,
        PinnedWidgetStore pinnedStore, RebindRequestStore rebindStore,
        PrinterStatusService statusService)
    {
        Id = id;
        DefinitionId = definitionId;
        _printers = printers;
        _secrets = secrets;
        _pinnedStore = pinnedStore;
        _rebindStore = rebindStore;
        _statusService = statusService;
        // tiles are generic slots ("Prusa Connect - N"); kind defaults to
        // PrinterStatus and is set per-tile in settings (saved in our snapshot +
        // customState), not fixed by the definition.
        //
        // restore the saved kind + binding so a COM restart keeps the tile's
        // config. we do NOT write a snapshot here - that happens on first render
        // (SendCard), so tiles the host creates-then-never-places don't leave
        // ghost rows in settings.
        var saved = _pinnedStore.GetAll().FirstOrDefault(s => s.WidgetId == id);
        if (saved is not null)
        {
            if (!string.IsNullOrEmpty(saved.Kind)) _kind = TileKindWire.FromWire(saved.Kind);
            if (!string.IsNullOrEmpty(saved.BoundPrinterId)) _boundPrinterId = saved.BoundPrinterId;

            // heal snapshots from the old ctor-clobber bug: a farm render wrote a
            // farm name marker, but the kind got reset to "printer-status".
            // recover the real kind from that marker.
            if (_kind == TileKind.PrinterStatus)
            {
                if (saved.BoundPrinterName == "(farm)") _kind = TileKind.FarmStatus;
                else if (saved.BoundPrinterName == "(farm orders)") _kind = TileKind.FarmOrders;
                else if (saved.BoundPrinterName == "(team)") _kind = TileKind.TeamStatus;
            }
        }
    }

    public string Id { get; }
    public string DefinitionId { get; }

    public string? BoundPrinterId => _boundPrinterId;

    public void SetSize(string? size)
    {
        if (!string.IsNullOrWhiteSpace(size))
        {
            _size = size.Trim().ToLowerInvariant();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;

            // record the tile in the snapshot the moment it starts - restored
            // kind + binding (ApplyCustomState ran before Start), no network - so
            // settings sees every pinned tile even if the board closes before the
            // first render.
            WriteSnapshot(boundPrinterId: null, boundPrinterName: null, lastState: null, lastSource: null);

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
            Log.Write($"Session {Id} started (kind={_kind}, size={_size}).");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        cts.Dispose();
        Log.Write($"Session {Id} stopped.");
    }

    /// <summary>
    /// Apply a customState JSON blob - reads both the printer binding and the
    /// tile kind.
    /// </summary>
    public void ApplyCustomState(string customState)
    {
        if (string.IsNullOrWhiteSpace(customState)) return;
        try
        {
            using var doc = JsonDocument.Parse(customState);
            if (doc.RootElement.TryGetProperty("printerId", out var idEl))
            {
                string? id = idEl.GetString();
                if (!string.IsNullOrEmpty(id)) { _boundPrinterId = id; }
            }
            if (doc.RootElement.TryGetProperty("kind", out var kindEl))
            {
                _kind = TileKindWire.FromWire(kindEl.GetString());
            }
            Log.Write($"Session {Id} state: printer={_boundPrinterId} kind={_kind}");
        }
        catch (JsonException) { /* leave as-is */ }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        await TickOnceAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay = _lastState switch
            {
                PrinterState.Printing => PrintingPollInterval,
                PrinterState.Unknown or PrinterState.Offline => ErrorBackoff,
                _ => IdlePollInterval,
            };
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickOnceAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        try
        {
            _printers.RefreshIfStale();
            ApplyPendingRebind();

            // farm/team tiles are account-wide (not bound to one printer), so
            // they render before the per-printer resolution / no-printer guard.
            if (_kind == TileKind.FarmStatus)
            {
                await RenderFarmStatusAsync(ct).ConfigureAwait(false);
                return;
            }
            if (_kind == TileKind.FarmOrders)
            {
                await RenderFarmOrdersAsync(ct).ConfigureAwait(false);
                return;
            }
            if (_kind == TileKind.TeamStatus)
            {
                await RenderTeamStatusAsync(ct).ConfigureAwait(false);
                return;
            }

            PrinterInfo? printer = ResolvePrinter();
            if (printer is null)
            {
                SendCard("no-printer", "{}");
                _lastState = PrinterState.Unknown;
                WriteSnapshot(null, null, null, null);
                return;
            }

            if (_kind == TileKind.Camera)
            {
                await RenderCameraAsync(printer, ct).ConfigureAwait(false);
            }
            else
            {
                await RenderPrinterStatusAsync(printer, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Error($"Session {Id} tick unexpected", ex);
        }
    }

    private async Task RenderPrinterStatusAsync(PrinterInfo printer, CancellationToken ct)
    {
        try
        {
            var result = await _statusService.GetStatusAsync(printer, _lastGoodSource, ct)
                .ConfigureAwait(false);
            _lastGoodSource = result.Source;
            _lastState = result.Status.State;

            string template = _size switch
            {
                "small" => "status-small",
                "large" => "status-large",
                _ => "status-medium",
            };

            // large tiles pull the camera (manual http URL wins, else the Connect
            // snapshot); other sizes skip it.
            string? camUri = null, camAge = null;
            if (_size == "large")
            {
                (camUri, camAge) = await ResolveCameraAsync(printer, ct).ConfigureAwait(false);
            }

            SendCard(template, WidgetDataBuilder.ForStatus(printer, result.Status, camUri, camAge));
            WriteSnapshot(printer.Id, printer.Name, result.Status.State.ToString(), result.Source.ToString());
            Log.Write($"Session {Id} ticked: state={result.Status.State} via {result.Source}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"Session {Id} status fetch failed", ex);
            SendCard("error", WidgetDataBuilder.ForError(printer, ex));
            _lastState = PrinterState.Offline;
            WriteSnapshot(printer.Id, printer.Name, "Offline", null);
        }
    }

    private async Task RenderFarmStatusAsync(CancellationToken ct)
    {
        try
        {
            // for farm tiles, _boundPrinterId is the selected team's org id
            // (empty -> first team)
            var summary = await _statusService.GetFarmSummaryAsync(_boundPrinterId, ct).ConfigureAwait(false);
            if (summary is null)
            {
                SendCard("farm-stub", WidgetDataBuilder.ForFarmStub(
                    "Farm Status", "Sign in to Prusa Connect in PrusaSlicer to see your fleet."));
                _lastState = PrinterState.Offline;
                WriteSnapshot(null, "(farm)", "-", null);
                return;
            }
            SendCard("farm-status", WidgetDataBuilder.ForFarmSummary(summary));
            _lastState = PrinterState.Idle; // farm tile polls on the idle cadence
            WriteSnapshot(_boundPrinterId, "(farm)", $"{summary.Total} printers", "PrusaConnect");
            Log.Write($"Session {Id} farm status: {summary.Total} total, {summary.Printing} printing, {summary.Attention} attention");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"Session {Id} farm status failed", ex);
            SendCard("farm-stub", WidgetDataBuilder.ForFarmStub("Farm Status", "Couldn't load fleet status."));
            _lastState = PrinterState.Offline;
        }
    }

    private async Task RenderTeamStatusAsync(CancellationToken ct)
    {
        try
        {
            // _boundPrinterId is the selected team name (empty -> all printers)
            var summary = await _statusService.GetTeamSummaryAsync(_boundPrinterId, ct).ConfigureAwait(false);
            if (summary is null)
            {
                SendCard("farm-stub", WidgetDataBuilder.ForFarmStub(
                    "Team Status", "Sign in to Prusa Connect in PrusaSlicer to see your team."));
                _lastState = PrinterState.Offline;
                WriteSnapshot(_boundPrinterId, "(team)", "-", null);
                return;
            }
            SendCard("farm-status", WidgetDataBuilder.ForFarmSummary(summary, "Team Status"));
            _lastState = PrinterState.Idle;
            WriteSnapshot(_boundPrinterId, "(team)", $"{summary.Total} printers", "PrusaConnect");
            Log.Write($"Session {Id} team status: {summary.Total} total in {summary.OrgName}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"Session {Id} team status failed", ex);
            SendCard("farm-stub", WidgetDataBuilder.ForFarmStub("Team Status", "Couldn't load team status."));
            _lastState = PrinterState.Offline;
        }
    }

    private async Task RenderFarmOrdersAsync(CancellationToken ct)
    {
        try
        {
            // for farm tiles, _boundPrinterId is the selected team's org id
            // (empty -> first team)
            var orders = await _statusService.GetFarmOrdersAsync(_boundPrinterId, ct).ConfigureAwait(false);
            if (orders is null)
            {
                SendCard("farm-stub", WidgetDataBuilder.ForFarmStub(
                    "Farm Orders", "Sign in to Prusa Connect in PrusaSlicer to see your farm orders."));
                _lastState = PrinterState.Offline;
                WriteSnapshot(null, "(farm orders)", "-", null);
                return;
            }
            SendCard("farm-orders", WidgetDataBuilder.ForFarmOrders(orders));
            _lastState = PrinterState.Idle;
            WriteSnapshot(_boundPrinterId, "(farm orders)", $"{orders.Orders.Count} orders", "PrusaConnect");
            Log.Write($"Session {Id} farm orders: {orders.Orders.Count} in {orders.OrganizationName}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"Session {Id} farm orders failed", ex);
            SendCard("farm-stub", WidgetDataBuilder.ForFarmStub("Farm Orders", "Couldn't load farm orders."));
            _lastState = PrinterState.Offline;
        }
    }

    private async Task RenderCameraAsync(PrinterInfo printer, CancellationToken ct)
    {
        // grab status too so the camera tile still shows state
        PrinterStatus? status = null;
        try
        {
            var result = await _statusService.GetStatusAsync(printer, _lastGoodSource, ct)
                .ConfigureAwait(false);
            _lastGoodSource = result.Source;
            status = result.Status;
            _lastState = status.State;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Session {Id} camera tile: status fetch failed ({ex.GetType().Name}); rendering camera only");
        }

        SendCard("camera", WidgetDataBuilder.ForCamera(printer, status));
        WriteSnapshot(printer.Id, printer.Name, status?.State.ToString(), _lastGoodSource.ToString());
    }

    /// <summary>
    /// The large-tile camera image + age label. A manual http(s) URL wins (host
    /// loads it). Else pull the Connect snapshot, downscale + base64 it, cache 30s.
    /// </summary>
    private async Task<(string? uri, string? age)> ResolveCameraAsync(PrinterInfo printer, CancellationToken ct)
    {
        string? manual = printer.CameraStreamUrl;
        if (!string.IsNullOrWhiteSpace(manual)
            && (manual!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || manual.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return (manual, null);
        }

        // serve the cached snapshot if fresh. once it's stale (camera not
        // uploading), drop the cache and refetch every poll - best-effort "get
        // the latest": the moment the camera resumes its ~30s uploads we grab the
        // new frame instead of sitting on a stale one.
        bool haveFreshSnapshot = _camCapturedAt is { } cap
            && DateTimeOffset.UtcNow - cap < TimeSpan.FromMinutes(2);
        TimeSpan effectiveTtl = haveFreshSnapshot ? CameraCacheTtl : TimeSpan.Zero;
        if (_camUri is not null && DateTimeOffset.UtcNow - _camFetchedAt < effectiveTtl)
        {
            return (_camUri, CameraImage.AgeLabel(_camCapturedAt));
        }

        try
        {
            var snap = await _statusService.GetCameraSnapshotAsync(printer, ct).ConfigureAwait(false);
            if (snap is null || snap.Jpeg.Length == 0)
            {
                _camUri = null;
                return (null, null);
            }
            _camUri = CameraImage.ToDownscaledDataUri(snap.Jpeg);
            _camCapturedAt = snap.CapturedAt;
            _camFetchedAt = DateTimeOffset.UtcNow;
            Log.Write($"Session {Id} camera snapshot pulled ({snap.Jpeg.Length} bytes, captured {snap.CapturedAt:O})");
            return (_camUri, CameraImage.AgeLabel(_camCapturedAt));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Session {Id} camera fetch failed: {ex.GetType().Name} {ex.Message}");
            return (_camUri, _camUri is not null ? CameraImage.AgeLabel(_camCapturedAt) : null);
        }
    }

    private void ApplyPendingRebind()
    {
        var pending = _rebindStore.TryTake(Id);
        if (pending is null) return;

        // kind first - it decides whether the binding is a printer id or a farm
        // team's org id (the org id isn't in the printer store)
        if (!string.IsNullOrEmpty(pending.NewKind))
        {
            _kind = TileKindWire.FromWire(pending.NewKind);
            Log.Write($"Session {Id} rebind -> kind {_kind}");
        }

        if (_kind is TileKind.FarmStatus or TileKind.FarmOrders)
        {
            // binding is the team's org id (empty -> first team)
            _boundPrinterId = string.IsNullOrEmpty(pending.NewPrinterId) ? null : pending.NewPrinterId;
            Log.Write($"Session {Id} rebind -> farm team {_boundPrinterId ?? "(default)"}");
        }
        else if (!string.IsNullOrEmpty(pending.NewPrinterId))
        {
            var requested = _printers.TryGet(pending.NewPrinterId);
            if (requested is not null)
            {
                _boundPrinterId = requested.Id;
                _lastGoodSource = StatusSource.Unknown; // fresh source decision
                Log.Write($"Session {Id} rebind -> printer {requested.Name} ({requested.Id})");
            }
            else
            {
                Log.Write($"Session {Id} rebind printer {pending.NewPrinterId} not in store");
            }
        }
        else
        {
            _boundPrinterId = null; // printer tile cleared
        }
    }

    private PrinterInfo? ResolvePrinter()
    {
        // the user configures every tile in settings. we never auto-pick: an
        // unbound tile (or one whose printer is gone) returns null and shows
        // "tile not set" instead of grabbing whatever's first.
        if (string.IsNullOrEmpty(_boundPrinterId)) return null;

        var p = _printers.TryGet(_boundPrinterId);
        if (p is not null) return p;

        Log.Write($"Session {Id} bound printer {_boundPrinterId} gone; tile shows not-set");
        return null;
    }

    private void WriteSnapshot(string? boundPrinterId, string? boundPrinterName, string? lastState, string? lastSource)
    {
        try
        {
            var existing = _pinnedStore.GetAll().FirstOrDefault(s => s.WidgetId == Id);
            _pinnedStore.Upsert(new PinnedWidgetSnapshot
            {
                WidgetId = Id,
                DefinitionId = DefinitionId,
                BoundPrinterId = boundPrinterId ?? existing?.BoundPrinterId,
                BoundPrinterName = boundPrinterName ?? existing?.BoundPrinterName,
                Kind = TileKindWire.ToWire(_kind),
                LastState = lastState ?? existing?.LastState,
                LastSource = lastSource ?? existing?.LastSource,
                LastSeenAt = DateTimeOffset.UtcNow,
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Session {Id} WriteSnapshot threw", ex);
        }
    }

    private void SendCard(string templateName, string data)
    {
        var options = new WidgetUpdateRequestOptions(Id)
        {
            Template = LoadTemplate(templateName),
            Data = data,
            CustomState = CurrentCustomState(),
        };
        WidgetManager.GetDefault().UpdateWidget(options);
    }

    /// <summary>
    /// Serialize the current binding + kind for the host's CustomState. Every
    /// UpdateWidget sends it, so we never silently drop the binding.
    /// </summary>
    private string CurrentCustomState()
    {
        return JsonSerializer.Serialize(new
        {
            kind = TileKindWire.ToWire(_kind),
            printerId = _boundPrinterId ?? string.Empty,
        });
    }

    private static string LoadTemplate(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Templates", name + ".json");
        return File.ReadAllText(path);
    }
}
