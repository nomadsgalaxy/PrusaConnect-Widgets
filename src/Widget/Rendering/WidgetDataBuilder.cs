using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PrusaConnect.Core.Models;
using PrusaConnect.Core.PrusaConnect;
using PrusaConnect.Core.PrusaLink;

namespace PrusaConnect.Widget.Rendering;

/// <summary>
/// Builds the data JSON the Adaptive Card templates bind to. Keeps formatting
/// (state labels, time strings, temps) out of the provider's lifecycle code.
/// </summary>
internal static class WidgetDataBuilder
{
    public static string ForStatus(PrinterInfo printer, PrinterStatus status,
        string? cameraImageUri = null, string? cameraAge = null)
    {
        bool hasJob = status.Job is not null && status.State == PrinterState.Printing
                                            || status.State == PrinterState.Paused;
        bool hasCamera = !string.IsNullOrEmpty(cameraImageUri);

        var data = new Dictionary<string, object?>
        {
            ["printerName"] = printer.Name,
            ["modelLabel"] = FormatModel(printer),
            ["stateLabel"] = FormatState(status.State),
            ["stateColor"] = StateAdaptiveColor(status.State),
            ["hasJob"] = hasJob,
            ["fileLabel"] = status.Job?.BestLabel ?? string.Empty,
            ["progressLabel"] = hasJob
                ? status.Job!.ProgressPercent.ToString("0", CultureInfo.InvariantCulture) + "%"
                : string.Empty,
            ["timeLabel"] = FormatTimeLabel(status.Job),
            ["idleLabel"] = FormatIdleLabel(status.State),
            ["nozzleTemp"] = FormatTempPair(status.Temperatures.NozzleCurrent, status.Temperatures.NozzleTarget),
            ["bedTemp"] = FormatTempPair(status.Temperatures.BedCurrent, status.Temperatures.BedTarget),
            ["updatedLabel"] = FormatRelativeTime(status.Timestamp),
            ["accentBar"] = BrandImages.OrangeBar,
            // Large-tile camera image (data URI or http URL) + staleness label,
            // resolved by WidgetSession (Connect snapshot or manual URL).
            ["hasCamera"] = hasCamera,
            ["cameraUrl"] = cameraImageUri ?? string.Empty,
            ["cameraAge"] = cameraAge ?? string.Empty,
            ["hasCameraAge"] = hasCamera && !string.IsNullOrEmpty(cameraAge),
        };

        return JsonSerializer.Serialize(data);
    }

    public static string ForError(PrinterInfo printer, Exception ex)
    {
        var (title, hint) = ex switch
        {
            PrusaLinkUnauthorizedException => ("API key rejected", "Check the API key in the Prusa Connect Widget settings."),
            PrusaLinkUnreachableException => ($"Cannot reach {printer.Host}", "Offline on LAN and no cloud status available. Open PrusaSlicer to refresh the Connect session."),
            PrusaConnectAuthRequiredException => ("Cloud sign-in needed", "Open PrusaSlicer and sign in to Prusa Connect so the widget can read cloud status."),
            PrusaConnectException => ("Prusa Connect error", ex.Message),
            _ => ("Status unavailable", ex.Message),
        };

        var data = new Dictionary<string, object?>
        {
            ["printerName"] = printer.Name,
            ["errorTitle"] = title,
            ["errorDetail"] = ex.Message,
            ["hint"] = hint,
            ["accentBar"] = BrandImages.OrangeBar,
        };
        return JsonSerializer.Serialize(data);
    }

    public static string ForCamera(PrinterInfo printer, PrinterStatus? status)
    {
        string? url = printer.CameraStreamUrl;
        bool hasHttpImage = !string.IsNullOrWhiteSpace(url)
            && (url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        string note = string.IsNullOrWhiteSpace(url)
            ? "No camera URL set - add one in the settings app."
            : hasHttpImage
                ? string.Empty
                : "Only http(s) snapshot URLs can be shown on a tile.";

        var data = new Dictionary<string, object?>
        {
            ["printerName"] = printer.Name,
            ["stateLabel"] = status is null ? string.Empty : FormatState(status.State),
            ["stateColor"] = status is null ? "Default" : StateAdaptiveColor(status.State),
            ["hasImage"] = hasHttpImage,
            ["cameraUrl"] = hasHttpImage ? url : string.Empty,
            ["note"] = note,
            ["hasNote"] = !string.IsNullOrEmpty(note),
            ["accentBar"] = BrandImages.OrangeBar,
        };
        return JsonSerializer.Serialize(data);
    }

    public static string ForFarmSummary(PrusaConnect.Core.Aggregator.FarmSummary s, string headerTitle = "Farm Status")
    {
        string printers = $"{s.Total} printer{(s.Total == 1 ? "" : "s")}";
        // the host's templating only binds strings into a TextBlock - bare ints
        // render blank - so stringify the counts
        var data = new Dictionary<string, object?>
        {
            ["accentBar"] = BrandImages.ProGreenBar,
            ["headerTitle"] = headerTitle,
            ["totalLabel"] = string.IsNullOrEmpty(s.OrgName) ? printers : $"{s.OrgName} · {printers}",
            ["printing"] = s.Printing.ToString(CultureInfo.InvariantCulture),
            ["idle"] = s.Idle.ToString(CultureInfo.InvariantCulture),
            ["attention"] = s.Attention.ToString(CultureInfo.InvariantCulture),
            ["offline"] = s.Offline.ToString(CultureInfo.InvariantCulture),
            ["updatedLabel"] = "just now",
        };
        return JsonSerializer.Serialize(data);
    }

    public static string ForFarmOrders(FarmOrders f)
    {
        var rows = f.Orders
            .OrderByDescending(o => o.IsActive)    // running orders first
            .ThenBy(o => o.IsComplete)             // then in-progress, finished last
            .ThenByDescending(o => o.TotalJobs)
            .Take(6)
            .Select(o =>
            {
                string eta = FormatEta(o);
                string prints = FormatCurrentPrints(o);
                return new
                {
                    name = string.IsNullOrEmpty(o.Number) ? o.Name : $"#{o.Number} · {o.Name}",
                    status = o.StatusSummary,
                    statusColor = FarmStatusColor(o),
                    progress = o.TotalJobs == 0
                        ? string.Empty
                        : $"{o.CompletedJobs}/{o.TotalJobs} · {o.ProgressPercent:0}%",
                    eta,
                    hasEta = eta.Length > 0,
                    prints,
                    hasPrints = prints.Length > 0,
                };
            })
            .ToArray();

        var data = new Dictionary<string, object?>
        {
            ["accentBar"] = BrandImages.ProGreenBar,
            ["orgName"] = f.OrganizationName,
            ["countLabel"] = $"{f.Orders.Count} order{(f.Orders.Count == 1 ? "" : "s")}",
            ["hasOrders"] = f.Orders.Count > 0,
            ["orders"] = rows,
        };
        return JsonSerializer.Serialize(data);
    }

    public static string ForFarmStub(string title, string subtitle)
    {
        // Farm/fleet views use Prusa Pro Green per the brand manual (Pro Green
        // is reserved for the professional line).
        var data = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["subtitle"] = subtitle,
            ["accentBar"] = BrandImages.ProGreenBar,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string FormatModel(PrinterInfo printer)
    {
        // brand naming: new products drop "Original", legacy keep it. without a
        // model registry we can't tell which, so just upper-case as-is. empty
        // model is fine - the line collapses.
        return string.IsNullOrWhiteSpace(printer.Model)
            ? string.Empty
            : printer.Model.ToUpperInvariant();
    }

    private static string FormatState(PrinterState state) => state switch
    {
        PrinterState.Printing => "PRINTING",
        PrinterState.Paused => "PAUSED",
        PrinterState.Finished => "FINISHED",
        PrinterState.Idle => "IDLE",
        PrinterState.Ready => "READY",
        PrinterState.Busy => "BUSY",
        PrinterState.Stopped => "STOPPED",
        PrinterState.Error => "ERROR",
        PrinterState.Attention => "ATTENTION",
        PrinterState.Offline => "OFFLINE",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// PrinterState -> one of Adaptive Cards' six semantic color tokens. The host
    /// themes these (not exact Prusa orange) since AC text won't take hex;
    /// "Warning" is the closest to brand orange.
    /// </summary>
    private static string StateAdaptiveColor(PrinterState state) => state switch
    {
        PrinterState.Printing => "Warning",
        PrinterState.Paused => "Warning",
        PrinterState.Attention => "Attention",
        PrinterState.Error => "Attention",
        PrinterState.Finished => "Good",
        PrinterState.Offline => "Default",
        _ => "Default",
    };

    /// <summary>
    /// Status-line color for a farm order: red-ish if anything was cancelled,
    /// orange while actively printing, green when complete, neutral otherwise.
    /// </summary>
    private static string FarmStatusColor(FarmOrder o)
    {
        if (o.Cancelled > 0) return "Attention";
        if (o.IsActive) return "Warning";
        if (o.IsComplete) return "Good";
        return "Default";
    }

    /// <summary>
    /// Compact one-line list of an order's current prints, e.g.
    /// "Mini-Fermi ~1h · Artemis ~9h". Capped to 3 printers with "+N more".
    /// </summary>
    private static string FormatCurrentPrints(FarmOrder o)
    {
        if (o.CurrentPrints.Count == 0) return string.Empty;

        var parts = o.CurrentPrints
            .Take(3)
            .Select(p =>
            {
                string t = FormatSecondsShort(p.TimeRemainingSeconds);
                return t.Length > 0 ? $"{p.PrinterName} {t}" : p.PrinterName;
            })
            .ToList();

        int extra = o.CurrentPrints.Count - parts.Count;
        if (extra > 0) parts.Add($"+{extra} more");
        return string.Join(" · ", parts);
    }

    private static string FormatSecondsShort(int? seconds)
    {
        if (seconds is not { } s || s <= 0) return string.Empty;
        var ts = TimeSpan.FromSeconds(s);
        if (ts.TotalDays >= 1) return $"~{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1) return $"~{(int)ts.TotalHours}h";
        return $"~{System.Math.Max(1, (int)ts.TotalMinutes)}m";
    }

    /// <summary>
    /// Coarse "time left" from the server's estimate - the real progress signal
    /// for a long farm print (e.g. "~10h left"). Empty if there's no estimate or
    /// it's past due / complete.
    /// </summary>
    private static string FormatEta(FarmOrder o)
    {
        if (o.IsComplete || o.EstimatedCompletion is not { } eta) return string.Empty;
        var remaining = eta - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return string.Empty;
        if (remaining.TotalDays >= 1) return $"~{(int)remaining.TotalDays}d left";
        if (remaining.TotalHours >= 1) return $"~{(int)remaining.TotalHours}h left";
        return $"~{System.Math.Max(1, (int)remaining.TotalMinutes)}m left";
    }

    private static string FormatTimeLabel(JobInfo? job)
    {
        if (job?.TimeRemaining is not { } tr)
        {
            return string.Empty;
        }
        return $"{FormatDuration(tr)} left";
    }

    private static string FormatIdleLabel(PrinterState state) => state switch
    {
        PrinterState.Idle => "Idle - ready to print",
        PrinterState.Ready => "Ready",
        PrinterState.Finished => "Print finished",
        PrinterState.Error => "Error",
        PrinterState.Attention => "Attention required",
        PrinterState.Offline => "Offline",
        _ => string.Empty,
    };

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
        {
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        }
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }
        return $"{(int)ts.TotalMinutes}m";
    }

    private static string FormatTempPair(double? current, double? target)
    {
        if (current is null)
        {
            return "-";
        }
        if (target is null or 0)
        {
            return $"{current.Value:0}°";
        }
        return $"{current.Value:0}° / {target.Value:0}°";
    }

    private static string FormatRelativeTime(DateTimeOffset ts)
    {
        var age = DateTimeOffset.UtcNow - ts;
        if (age.TotalSeconds < 60)
        {
            return "just now";
        }
        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }
        return $"{(int)age.TotalHours}h ago";
    }
}
