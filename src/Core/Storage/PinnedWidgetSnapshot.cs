using System;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// One row in pinned-widgets.json - written by the COM server's WidgetSession,
/// read by the settings UI to show which tiles are pinned and what they show.
/// </summary>
public sealed class PinnedWidgetSnapshot
{
    public string WidgetId { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public string? BoundPrinterId { get; set; }
    public string? BoundPrinterName { get; set; }
    /// <summary>Wire value of the tile's TileKind (e.g. "printer-status").</summary>
    public string? Kind { get; set; }
    public string? LastState { get; set; }
    public string? LastSource { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
