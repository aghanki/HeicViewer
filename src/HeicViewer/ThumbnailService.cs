using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ImageMagick;

namespace HeicViewer;

/// <summary>
/// Renders 320px thumbnails for library items, caches them as PNGs under
/// %LOCALAPPDATA%\HeicViewer\thumbs (keyed by path+size+mtime), and decodes
/// in parallel worker threads — visible items get priority.
/// </summary>
public static class ThumbnailService
{
    private const int ThumbSize = 320;

    private static readonly object Lock = new();
    private static readonly HashSet<string> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<ImageItem> Urgent = new();
    private static readonly ConcurrentQueue<ImageItem> Background = new();
    private static CancellationTokenSource? _cts;

    public static string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeicViewer", "thumbs");

    public static void Start()
    {
        Directory.CreateDirectory(CacheDir);
        _cts = new CancellationTokenSource();
        int workers = Math.Clamp(Environment.ProcessorCount, 2, 4);
        for (int i = 0; i < workers; i++)
            Task.Run(() => Worker(_cts!.Token));
    }

    public static void Shutdown()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public static string CachePathFor(ImageItem item) =>
        Path.Combine(CacheDir, Key(item) + ".png");

    public static bool IsCached(ImageItem item) => File.Exists(CachePathFor(item));

    private static string Key(ImageItem item)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{item.Path.ToLowerInvariant()}|{item.Length}|{item.Modified.Ticks}"));
        return Convert.ToHexString(hash)[..20];
    }

    /// <summary>Ensures a cached thumbnail exists. 'urgent' = visible right now.</summary>
    public static void Request(ImageItem item, bool urgent)
    {
        if (File.Exists(CachePathFor(item)))
        {
            Notify(item);
            return;
        }
        string key = Key(item);
        if (Failed.Contains(key))
            return;

        lock (Lock)
        {
            if (!Queued.Add(key))
                return;
            (urgent ? Urgent : Background).Enqueue(item);
        }
    }

    private static void Notify(ImageItem item)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => item.RaiseCacheReady());
    }

    private static async Task Worker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ImageItem? item = Urgent.TryDequeue(out var urgent) ? urgent
                            : Background.TryDequeue(out var bg) ? bg
                            : null;

            if (item is null)
            {
                try { await Task.Delay(120, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            string key = Key(item);
            try
            {
                byte[] png = RenderPng(item.Path);
                File.WriteAllBytes(CachePathFor(item), png);
                Failed.Remove(key);
                Notify(item);
            }
            catch
            {
                lock (Lock) Failed.Add(key);
            }
            finally
            {
                lock (Lock) Queued.Remove(key);
            }
        }
    }

    private static byte[] RenderPng(string path)
    {
        using var frames = new MagickImageCollection();
        frames.Read(path);
        if (frames.Count == 0)
            throw new InvalidOperationException("No frames.");

        var best = frames[0];
        foreach (var frame in frames)
            if ((long)frame.Width * frame.Height > (long)best.Width * best.Height)
                best = frame;

        best.AutoOrient();
        best.Thumbnail(ThumbSize, ThumbSize);

        using var ms = new MemoryStream();
        best.Write(ms, MagickFormat.Png);
        return ms.ToArray();
    }
}
