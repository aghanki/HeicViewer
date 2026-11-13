using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HeicViewer;

/// <summary>One image in the library / thumbnail strip.</summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public string FolderName { get; }
    public string DriveLabel { get; }
    public long Length { get; }
    public DateTime Modified { get; }
    public string SizeLabel { get; }
    public string DateLabel { get; }

    private BitmapSource? _thumb;
    public BitmapSource? Thumb
    {
        get => _thumb;
        set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; OnPropertyChanged(); } }
    }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; OnPropertyChanged(); } }
    }

    /// <summary>Raised on the UI thread when a cached thumbnail for this item becomes available.</summary>
    public event Action? CacheReady;
    public void RaiseCacheReady() => CacheReady?.Invoke();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ImageItem(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(full);

        Path = full;
        Name = System.IO.Path.GetFileName(full);
        Folder = System.IO.Path.GetDirectoryName(full) ?? "";
        FolderName = new DirectoryInfo(Folder).Name;
        DriveLabel = System.IO.Path.GetPathRoot(full) ?? "";
        Length = info.Exists ? info.Length : 0;
        Modified = info.Exists ? info.LastWriteTime : DateTime.MinValue;

        SizeLabel = Length switch
        {
            >= 1_000_000 => $"{Length / 1_000_000.0:0.#} MB",
            >= 1_000 => $"{Length / 1_000.0:0.#} KB",
            _ => $"{Length} B"
        };
        DateLabel = Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    }
}
