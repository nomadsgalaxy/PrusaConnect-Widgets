using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace PrusaConnect.Widget.Rendering;

/// <summary>
/// Turns a raw Connect camera JPEG into a downscaled base64 data URI small
/// enough for an Adaptive Card, and builds the "X old" label from the capture
/// time.
/// </summary>
internal static class CameraImage
{
    private const int MaxWidth = 640;
    private const long JpegQuality = 72;

    /// <summary>
    /// Normalize a webcam URL to a single still. A tile can't render an MJPEG
    /// stream, so swap the common stream endpoints to their snapshot form:
    /// mjpg-streamer's <c>?action=stream</c> -> <c>?action=snapshot</c>, and a
    /// crowsnest/ustreamer trailing <c>/stream</c> -> <c>/snapshot</c>. Anything
    /// else (including an already-correct snapshot URL) passes through unchanged.
    /// </summary>
    public static string ToSnapshotUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        string s = url.Replace("action=stream", "action=snapshot", StringComparison.OrdinalIgnoreCase);
        if (s.EndsWith("/stream", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - "/stream".Length) + "/snapshot";
        }
        return s;
    }

    public static string ToDownscaledDataUri(byte[] jpeg)
    {
        using var inStream = new MemoryStream(jpeg);
        using var src = Image.FromStream(inStream);

        int w = src.Width, h = src.Height;
        if (w > MaxWidth)
        {
            double scale = (double)MaxWidth / w;
            w = MaxWidth;
            h = Math.Max(1, (int)Math.Round(src.Height * scale));
        }

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }

        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);

        using var outStream = new MemoryStream();
        bmp.Save(outStream, encoder, ep);
        return "data:image/jpeg;base64," + Convert.ToBase64String(outStream.ToArray());
    }

    /// <summary>Mimics Connect's staleness phrasing.</summary>
    public static string AgeLabel(DateTimeOffset? capturedAt)
    {
        if (capturedAt is not { } ts)
        {
            return "Camera";
        }
        var age = DateTimeOffset.UtcNow - ts;
        if (age < TimeSpan.FromSeconds(90)) return "Camera • live";
        if (age < TimeSpan.FromMinutes(60)) return $"Camera • {(int)age.TotalMinutes}m old";
        if (age < TimeSpan.FromHours(24))
        {
            int h = (int)age.TotalHours;
            return $"Camera • more than {h} hour{(h == 1 ? "" : "s")} old";
        }
        int d = (int)age.TotalDays;
        return $"Camera • more than {d} day{(d == 1 ? "" : "s")} old";
    }
}
