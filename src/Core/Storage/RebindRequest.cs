using System;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// A pending rebind: the settings UI drops one into rebind-requests.json keyed by
/// widgetId; the WidgetSession picks it up on its next tick, applies it via
/// WidgetManager.UpdateWidget, then deletes the entry.
/// </summary>
public sealed class RebindRequest
{
    public string WidgetId { get; set; } = string.Empty;
    public string NewPrinterId { get; set; } = string.Empty;

    /// <summary>Wire value of the desired TileKind (e.g. "printer-status",
    /// "camera"). Null/empty leaves the tile's current kind unchanged.</summary>
    public string? NewKind { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
}
