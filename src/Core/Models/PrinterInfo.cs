namespace PrusaConnect.Core.Models;

/// <summary>
/// Saved config for one printer the user added - an entry in the catalogue
/// (printers.json).
/// </summary>
public sealed record PrinterInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; } = 80;

    /// <summary>Printer model as advertised by /api/v1/info (e.g. "MK4S", "CORE ONE").</summary>
    public string Model { get; init; } = string.Empty;

    public string? Hostname { get; init; }

    public string? Serial { get; init; }

    /// <summary>
    /// The printer's Prusa Connect UUID, if it came from (or matched) a cloud
    /// account. Lets the aggregator fall back to cloud polling
    /// (<c>/app/printers/&lt;uuid&gt;</c>) when the printer's off the LAN. Null
    /// for purely-manual PrusaLink entries.
    /// </summary>
    public string? ConnectUuid { get; init; }

    public PrinterSource Source { get; init; } = PrinterSource.PrusaLink;

    /// <summary>
    /// Optional manual camera URL. An http(s) one is shown on the large tile
    /// directly; otherwise the tile pulls the printer's Connect snapshot.
    /// </summary>
    public string? CameraStreamUrl { get; init; }
}

public enum PrinterSource
{
    PrusaLink,
    PrusaConnect,
}
