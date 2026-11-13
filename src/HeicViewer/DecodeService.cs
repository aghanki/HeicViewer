using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace HeicViewer;

public sealed record DecodeResult(
    BitmapSource? Image,
    int PixelWidth,
    int PixelHeight,
    int FrameCount,
    string Decoder,
    IReadOnlyList<KeyValuePair<string, string>> Exif,
    string? Error)
{
    public bool Success => Image is not null;
}

public static class DecodeService
{
    /// <summary>Every format the viewer accepts, HEIF family first.</summary>
    public static readonly string[] SupportedExtensions =
    [
        ".heic", ".heif", ".hif", ".avif",
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    ];

    /// <summary>The HEIC family scanned by the library.</summary>
    public static readonly string[] HeicExtensions = [".heic", ".heif", ".hif", ".avif"];

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && SupportedExtensions.Contains(ext.ToLowerInvariant());
    }

    public static bool IsHeicFamily(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && HeicExtensions.Contains(ext.ToLowerInvariant());
    }

    public static DecodeResult Decode(string path)
    {
        // Primary: Magick.NET (bundles libheif + libde265). Decodes every HEIC/HEIF/HIF/AVIF
        // profile — including 10-bit HEIX — with no Windows codec and no paid HEVC extension.
        try
        {
            using var frames = new MagickImageCollection();
            frames.Read(path);
            if (frames.Count == 0)
                throw new InvalidOperationException("No image frames found.");

            // HEIC containers often carry thumbnails, previews or depth maps as extra items.
            // Show the largest one (the primary image).
            var best = frames[0];
            foreach (var frame in frames)
                if ((long)frame.Width * frame.Height > (long)best.Width * best.Height)
                    best = frame;

            best.AutoOrient();
            best.ColorSpace = ColorSpace.sRGB;
            best.HasAlpha = true;            // ensure 4 channels so GetArea returns RGBA

            int width = (int)best.Width;
            int height = (int)best.Height;
            byte[] rgba = best.GetPixels().GetArea(0, 0, (uint)width, (uint)height);

            // Magick yields RGBA; WPF's Bgra32 wants BGRA.
            var bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, bgra, width * 4);
            source.Freeze();

            var exif = ExtractExif(best);
            return new DecodeResult(source, width, height, frames.Count, "libheif", exif, null);
        }
        catch (Exception magickError)
        {
            // Fallback: Windows Imaging Component. Works when the (optional) HEIF/HEVC codecs
            // are installed, and covers anything else WIC can decode.
            try
            {
                var source = DecodeWithWic(path);
                return new DecodeResult(source, source.PixelWidth, source.PixelHeight, 1, "WIC",
                    Array.Empty<KeyValuePair<string, string>>(), null);
            }
            catch (Exception wicError)
            {
                return new DecodeResult(null, 0, 0, 0, "none",
                    Array.Empty<KeyValuePair<string, string>>(),
                    $"libheif: {FirstLine(magickError.Message)} | WIC: {FirstLine(wicError.Message)}");
            }
        }
    }

    private static BitmapSource DecodeWithWic(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidOperationException("WIC found no image frames.");
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static List<KeyValuePair<string, string>> ExtractExif(IMagickImage<byte> image)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        try
        {
            var exif = image.GetExifProfile();
            if (exif is null)
                return pairs;

            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    pairs.Add(new KeyValuePair<string, string>(key, value));
            }

            string? make = null, model = null;
            try { make = exif.GetValue<string>(ExifTag.Make)?.Value; } catch { }
            try { model = exif.GetValue<string>(ExifTag.Model)?.Value; } catch { }
            if (make is not null && model is not null &&
                model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
                model = model[make.Length..].Trim();
            Add("Camera", string.Join(" ", new[] { make, model }.Where(s => !string.IsNullOrWhiteSpace(s))));

            try { Add("Date taken", exif.GetValue<string>(ExifTag.DateTimeOriginal)?.Value); } catch { }

            try
            {
                var exposure = exif.GetValue<Rational>(ExifTag.ExposureTime)?.Value;
                if (exposure is Rational er && er.Denominator != 0)
                {
                    double s = (double)er.Numerator / er.Denominator;
                    Add("Exposure", s < 1 ? $"1/{Math.Round(1 / s)} s" : $"{s:0.##} s");
                }
            }
            catch { }

            try
            {
                var fnum = exif.GetValue<Rational>(ExifTag.FNumber)?.Value;
                if (fnum is Rational fr && fr.Denominator != 0)
                    Add("Aperture", $"f/{(double)fr.Numerator / fr.Denominator:0.#}");
            }
            catch { }

            try
            {
                var iso = exif.GetValue<ushort[]>(ExifTag.ISOSpeedRatings)?.Value;
                if (iso is { Length: > 0 })
                    Add("ISO", string.Join(", ", iso));
            }
            catch { }

            try
            {
                var fl = exif.GetValue<Rational>(ExifTag.FocalLength)?.Value;
                if (fl is Rational fr2 && fr2.Denominator != 0)
                    Add("Focal length", $"{(double)fr2.Numerator / fr2.Denominator:0.#} mm");
            }
            catch { }
        }
        catch
        {
            // EXIF is optional — never let it break decoding.
        }
        return pairs;
    }

    /// <summary>Normalizes DPI to 96 so 1 image pixel = 1 screen pixel at 100% zoom.</summary>
    private static BitmapSource Force96Dpi(BitmapSource source)
    {
        if (source.DpiX == 96 && source.DpiY == 96)
            return source;

        int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        var normalized = BitmapSource.Create(source.PixelWidth, source.PixelHeight,
            96, 96, source.Format, source.Palette, pixels, stride);
        normalized.Freeze();
        return normalized;
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', '\r')[0];
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
