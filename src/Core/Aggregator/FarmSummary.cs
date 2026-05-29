namespace PrusaConnect.Core.Aggregator;

/// <summary>
/// Fleet health counts for a Connect team/org (or the whole account). Feeds the
/// Farm Status and Team Status tiles.
/// </summary>
public sealed record FarmSummary(
    int Total,
    int Printing,
    int Idle,
    int Attention,
    int Offline,
    string? OrgName = null)
{
    /// <summary>Anything not printing/idle/attention/offline (paused, busy, unknown).</summary>
    public int Other => System.Math.Max(0, Total - Printing - Idle - Attention - Offline);
}
