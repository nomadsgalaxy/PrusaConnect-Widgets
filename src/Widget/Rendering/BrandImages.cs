namespace PrusaConnect.Widget.Rendering;

/// <summary>
/// Brand-colored solid PNGs as base64 data URIs. Adaptive Cards in the Widgets
/// host can't load ms-appx:// images or set hex text colors, but they CAN render
/// a data: image - so we stretch a 1-color swatch into a top accent bar to keep
/// tiles on-brand in either theme.
///
/// Orange #FD5000 = standard tiles. Pro Green #00C48D = farm/fleet tiles (the
/// brand manual reserves Pro Green for the pro / HT90 line).
/// </summary>
internal static class BrandImages
{
    public const string OrangeBar =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAdSURBVDhPY/gbwPCfEsyALkAqHjVg1IBRAwaLAQDPBkwfUPl+BAAAAABJRU5ErkJggg==";

    public const string ProGreenBar =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAA2SURBVDhPpcgxDQAgEACx95/gCl+PgJsahi6duWe/JFRCJVRCJVRCJVRCJVRCJVRCJVRCJdADZ29QH2N2myEAAAAASUVORK5CYII=";
}
