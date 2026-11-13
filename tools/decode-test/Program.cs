// Headless check that the bundled libheif decoder opens given HEIC files.
using ImageMagick;

foreach (var path in args)
{
    try
    {
        using var frames = new MagickImageCollection();
        frames.Read(path);
        if (frames.Count == 0)
            throw new InvalidOperationException("No frames found.");

        var best = frames[0];
        foreach (var f in frames)
            if ((long)f.Width * f.Height > (long)best.Width * best.Height)
                best = f;
        best.AutoOrient();

        Console.WriteLine($"{Path.GetFileName(path)}: OK  {best.Width}x{best.Height}  format={best.Format}  frames={frames.Count}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{Path.GetFileName(path)}: FAIL  {ex.GetType().Name}: {ex.Message}");
    }
}
