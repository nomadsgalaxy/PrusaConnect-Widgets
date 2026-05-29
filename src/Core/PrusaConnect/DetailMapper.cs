using System;
using PrusaConnect.Core.Models;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// Maps a <see cref="ConnectPrinterDetailDto"/> (raw Connect wire shape) onto
/// the <see cref="PrinterStatus"/> the renderer already uses, so rendering
/// doesn't fork by backend.
/// </summary>
internal static class DetailMapper
{
    public static PrinterStatus ToPrinterStatus(ConnectPrinterDetailDto d)
    {
        return new PrinterStatus
        {
            State = MapState(d.State ?? d.PrinterState ?? ""),
            Temperatures = new Temperatures
            {
                NozzleCurrent = d.Temp?.TempNozzle,
                NozzleTarget = d.Temp?.TargetNozzle,
                BedCurrent = d.Temp?.TempBed,
                BedTarget = d.Temp?.TargetBed,
            },
            Job = BuildJob(d),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static JobInfo? BuildJob(ConnectPrinterDetailDto d)
    {
        var ji = d.JobInfo;
        if (ji is null)
        {
            return null;
        }

        return new JobInfo
        {
            FileName = ji.Path,
            DisplayName = ji.DisplayName,
            ProgressPercent = ji.Progress ?? 0,
            TimeRemaining = ji.TimeRemaining is { } tr ? TimeSpan.FromSeconds(tr) : null,
            TimePrinting = ji.TimePrinting is { } tp ? TimeSpan.FromSeconds(tp) : null,
        };
    }

    // Connect's states match PrusaLink's for the most part, plus OFFLINE (not
    // currently reporting to the cloud).
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
        "OFFLINE" => PrinterState.Offline,
        _ => PrinterState.Unknown,
    };
}
