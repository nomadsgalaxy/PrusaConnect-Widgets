using System;

namespace PrusaConnect.Core.Models;

/// <summary>
/// A snapshot of a printer's runtime state - a domain model, not a wire format.
/// Live values from PrusaLink or Prusa Connect land here.
/// </summary>
public sealed record PrinterStatus
{
    public required PrinterState State { get; init; }

    public Temperatures Temperatures { get; init; } = new();

    public JobInfo? Job { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record Temperatures
{
    public double? NozzleCurrent { get; init; }
    public double? NozzleTarget { get; init; }
    public double? BedCurrent { get; init; }
    public double? BedTarget { get; init; }
}

public sealed record JobInfo
{
    public string? FileName { get; init; }
    public string? DisplayName { get; init; }
    public double ProgressPercent { get; init; }
    public TimeSpan? TimeRemaining { get; init; }
    public TimeSpan? TimePrinting { get; init; }

    /// <summary>Best-effort label for the currently printing file.</summary>
    public string? BestLabel => DisplayName ?? FileName;
}
