namespace PrusaConnect.Core.Models;

/// <summary>
/// What a pinned tile shows. Stored per-instance in the host's customState.
/// PrinterStatus is the default; the rest are set in the settings "Pinned tiles"
/// panel.
/// </summary>
public enum TileKind
{
    PrinterStatus,
    Camera,
    FarmStatus,
    FarmOrders,
    // Fleet summary for a regular Prusa Connect TEAM (no Connect Farm needed).
    TeamStatus,
}

public static class TileKindWire
{
    public const string PrinterStatus = "printer-status";
    public const string Camera = "camera";
    public const string FarmStatus = "farm-status";
    public const string FarmOrders = "farm-orders";
    public const string TeamStatus = "team-status";

    public static string ToWire(TileKind kind) => kind switch
    {
        TileKind.Camera => Camera,
        TileKind.FarmStatus => FarmStatus,
        TileKind.FarmOrders => FarmOrders,
        TileKind.TeamStatus => TeamStatus,
        _ => PrinterStatus,
    };

    public static TileKind FromWire(string? wire) => wire switch
    {
        Camera => TileKind.Camera,
        FarmStatus => TileKind.FarmStatus,
        FarmOrders => TileKind.FarmOrders,
        TeamStatus => TileKind.TeamStatus,
        _ => TileKind.PrinterStatus,
    };

    public static string DisplayName(TileKind kind) => kind switch
    {
        TileKind.Camera => "Camera",
        TileKind.FarmStatus => "Farm status",
        TileKind.FarmOrders => "Farm orders",
        TileKind.TeamStatus => "Team status",
        _ => "Printer status",
    };
}
