using System;
using System.Collections.Generic;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// One Connect Farm order. The counts come from the real per-job states (see
/// FarmGraphQlClient) plus the server's <c>estimatedCompletionDate</c> - the
/// same numbers the Farm web UI shows, so a live print reads as "1 printing ·
/// ~10h left" instead of a flat 0%.
/// </summary>
public sealed record FarmOrder(
    string Id,
    string Name,
    string? Number,
    int TotalJobs,
    int CompletedJobs,
    int Printing = 0,
    int Cancelled = 0,
    DateTimeOffset? EstimatedCompletion = null,
    string? State = null)
{
    public double ProgressPercent => TotalJobs > 0
        ? System.Math.Round(100.0 * CompletedJobs / TotalJobs, 0)
        : 0;

    /// <summary>
    /// Pieces not printing, done, or cancelled. Big orders only materialize a few
    /// jobs at a time, so we derive the rest of the backlog from the planned total.
    /// </summary>
    public int Queued => System.Math.Max(0, TotalJobs - Printing - CompletedJobs - Cancelled);

    public bool IsComplete =>
        string.Equals(State, "DONE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "COMPLETED", StringComparison.OrdinalIgnoreCase)
        || (TotalJobs > 0 && CompletedJobs >= TotalJobs);

    /// <summary>True while at least one job is actively printing.</summary>
    public bool IsActive => Printing > 0;

    /// <summary>
    /// The order's printing jobs, each resolved to its printer + live time-left
    /// (from fleet telemetry). Empty unless something's on a printer right now.
    /// </summary>
    public IReadOnlyList<FarmCurrentPrint> CurrentPrints { get; init; } = System.Array.Empty<FarmCurrentPrint>();

    /// <summary>Short job-state line for the tile, e.g. "1 printing · 2 queued" or "Done".</summary>
    public string StatusSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (Printing > 0) parts.Add($"{Printing} printing");
            if (Queued > 0) parts.Add($"{Queued} queued");
            if (Cancelled > 0) parts.Add($"{Cancelled} cancelled");
            if (parts.Count > 0) return string.Join(" · ", parts);
            if (IsComplete) return "Done";
            return CompletedJobs > 0 ? $"{CompletedJobs} done" : "Queued";
        }
    }
}

/// <summary>One currently-printing job of an order, on a named printer.</summary>
public sealed record FarmCurrentPrint(string PrinterName, int? TimeRemainingSeconds);

/// <summary>The user's Connect Farm orders, freshest first.</summary>
public sealed record FarmOrders(string OrganizationName, IReadOnlyList<FarmOrder> Orders);
