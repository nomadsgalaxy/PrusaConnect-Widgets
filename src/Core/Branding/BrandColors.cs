using PrusaConnect.Core.Models;

namespace PrusaConnect.Core.Branding;

/// <summary>
/// Prusa brand color tokens, from the official Brand Manual v1.0 (Part I,
/// mandatory). Don't add ad-hoc colors - pick from here, and cite the brand
/// manual for anything new.
/// </summary>
public static class BrandColors
{
    // PRIMARY - mandated for new applications.
    public const string PrusaOrange = "#FD5000";

    // Legacy orange - use ONLY when matching existing PrusaSlicer UI.
    public const string PrusaOrangeLegacy = "#ED6B21";

    // Pro/HT90 line accent. NEVER use as a generic success color.
    public const string PrusaProGreen = "#00C48D";

    // Neutrals from the brand manual.
    public const string BrandGray = "#808285";
    public const string Black = "#000000";
    public const string White = "#FFFFFF";

    // Semantic colors (from PrusaSlicer application palette - not the
    // brand-mandated palette, but the conventional UI semantics).
    public const string SuccessGreen = "#65C900";
    public const string ErrorRed = "#ED0000";
    public const string WarningRedWarm = "#E74840";
    public const string LinkBlue = "#346EF4";

    // Dark mode tokens (preserved across both themes, orange is the anchor).
    public const string DarkBackground = "#121212";
    public const string DarkText = "#E0E0E0";
    public const string LightBackground = "#FFFFFF";
    public const string LightText = "#000000";
}

/// <summary>
/// Maps Prusa printer state to the brand-mandated accent color for that state.
/// Printing = orange (active). Idle = gray. Error = red. Finished = success green.
/// </summary>
public static class StateColors
{
    public static string ForState(PrinterState state) => state switch
    {
        PrinterState.Printing => BrandColors.PrusaOrange,
        PrinterState.Paused => BrandColors.WarningRedWarm,
        PrinterState.Finished => BrandColors.SuccessGreen,
        PrinterState.Error => BrandColors.ErrorRed,
        PrinterState.Attention => BrandColors.WarningRedWarm,
        PrinterState.Offline => BrandColors.BrandGray,
        _ => BrandColors.BrandGray,
    };
}
