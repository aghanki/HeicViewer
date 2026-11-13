using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HeicViewer;

public partial class LibraryView : UserControl, INotifyPropertyChanged
{
    public sealed record RowVm(IReadOnlyList<ImageItem> Items, int Columns);

    public event Action<IReadOnlyList<ImageItem>, int>? OpenRequested;
    public event EventHandler? OpenFileRequested;

    public ObservableCollection<RowVm> Rows { get; } = new();

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "SYSTEM VOLUME INFORMATION", "WINDOWS", "PROGRAM FILES",
        "PROGRAM FILES (X86)", "PROGRAMDATA", "APPDATA", "NODE_MODULES", ".GIT", ".SVN",
        "TEMP", "TMP", "MSOCACHE", "PERFLOGS", "RECOVERY", "CONFIG.MSI", "INETPUB",
        "DUMPSTACK.LOG.TMP"
    };

    private static readonly string[] SortNames = ["Name", "Date", "Size", "Folder"];

    private readonly ObservableCollection<ImageItem> _items = new();
    private readonly Dictionary<string, List<ImageItem>> _driveItems = new(StringComparer.OrdinalIgnoreCase);
    private List<ImageItem> _viewList = [];
    private CancellationTokenSource? _scanCts;
    private string? _folderScope;
    private int _sortIndex;
    private string _search = "";
    private int _columns = 6;
    private DispatcherTimer? _searchTimer;
    private DispatcherTimer? _resizeTimer;

    private bool _scanning;
    public bool Scanning
    {
        get => _scanning;
        private set
        {
            if (_scanning == value) return;
            _scanning = value;
            OnPropertyChanged();
            ScanProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            StopBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string _countText = "";
    public string CountText
    {
        get => _countText;
        private set { _countText = value; OnPropertyChanged(); }
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set { _progressText = value; OnPropertyChanged(); }
    }

    public LibraryView()
    {
        InitializeComponent();
        DataContext = this;
        DetectDrives();

        TilesList.SizeChanged += (_, _) =>
        {
            _resizeTimer ??= NewTimer(150, () =>
            {
                int cols = Math.Clamp((int)(TilesList.ActualWidth / 196), 2, 8);
                if (cols != _columns)
                {
                    _columns = cols;
                    RebuildRows();
                }
            });
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };

        SearchBox.TextChanged += (_, _) =>
        {
            _searchTimer ??= NewTimer(300, () =>
            {
                _search = SearchBox.Text.Trim();
                RebuildRows();
            });
            _searchTimer.Stop();
            _searchTimer.Start();
        };
    }

    private DispatcherTimer NewTimer(int ms, Action tick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    // ---------------------------------------------------------------- scanning

    private List<string> SelectedRoots()
    {
        var roots = new List<string>();
        foreach (ToggleButton chip in DriveChips.Children.OfType<ToggleButton>())
            if (chip.IsChecked == true && chip.Tag is string root)
                roots.Add(root);
        return roots;
    }

    private void DetectDrives()
    {
        DriveChips.Children.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d is { IsReady: true, DriveType: DriveType.Fixed }))
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("ChipToggle"),
                Content = drive.Name,
                Tag = drive.Name,
                IsChecked = true
            };
            chip.Checked += OnDriveSelectionChanged;
            chip.Unchecked += OnDriveSelectionChanged;
            DriveChips.Children.Add(chip);
        }
    }

    /// <summary>App entry point: scan every fixed drive.</summary>
    public void ScanAllDrives()
    {
        _folderScope = null;
        UpdateScopeUi();
        StartScan(SelectedRoots());
    }

    /// <summary>Open a specific folder as the library scope.</summary>
    public void OpenFolder(string folder)
    {
        _folderScope = folder;
        UpdateScopeUi();
        StartScan([folder]);
    }

    public void CancelScan()
    {
        _scanCts?.Cancel();
        _scanCts = null;
        Scanning = false;
    }

    private async void StartScan(IReadOnlyList<string> roots)
    {
        CancelScan();
        if (roots.Count == 0)
        {
            RebuildRows();
            return;
        }

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        Scanning = true;
        _items.Clear();
        _driveItems.Clear();
        ProgressText = "Scanning…";
        RebuildRows();

        var channel = Channel.CreateBounded<ImageItem>(1024);
        var ct2 = ct;
        var producers = roots.Select(root =>
            Task.Run(() => ProduceRoot(root, channel.Writer, ct2), ct2)).ToArray();
        var consumer = Task.Run(() => ConsumeAsync(channel.Reader, ct2), ct2);
        _ = Task.WhenAll(producers).ContinueWith(
            _ => channel.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await consumer;
        }
        catch (OperationCanceledException) { }

        if (!ct.IsCancellationRequested)
        {
            Scanning = false;
            RebuildRows();
        }
    }

    private static void ProduceRoot(string root, ChannelWriter<ImageItem> writer, CancellationToken ct)
    {
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && !ct.IsCancellationRequested)
            {
                string dir = pending.Pop();

                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        if (IsSkippedDir(sub)) continue;
                        try
                        {
                            var attrs = File.GetAttributes(sub);
                            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        }
                        catch { continue; }
                        pending.Push(sub);
                    }
                }
                catch { }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (ct.IsCancellationRequested) return;
                        var ext = Path.GetExtension(file);
                        if (ext.Length == 0 || !DecodeService.IsHeicFamily(file)) continue;
                        writer.TryWrite(new ImageItem(file));
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsSkippedDir(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return name.Length > 0 && SkippedDirs.Contains(name);
    }

    private async Task ConsumeAsync(ChannelReader<ImageItem> reader, CancellationToken ct)
    {
        var buffer = new List<ImageItem>(512);
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var item))
                    buffer.Add(item);
                if (buffer.Count > 0)
                {
                    Flush(buffer);
                    buffer = new List<ImageItem>(512);
                }
            }
        }
        catch (OperationCanceledException) { }

        if (buffer.Count > 0)
            Flush(buffer);
    }

    private void Flush(List<ImageItem> batch)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in batch)
            {
                _items.Add(item);
                var root = _folderScope ?? Path.GetPathRoot(item.Path) ?? "";
                if (!_driveItems.TryGetValue(root, out var list))
                    _driveItems[root] = list = new List<ImageItem>();
                list.Add(item);
            }
            ProgressText = $"Scanning…  {_items.Count:N0} found";
            RebuildRows();
        });
    }

    // ---------------------------------------------------------------- grid building

    private void RebuildFromSelected()
    {
        _items.Clear();
        foreach (var chip in DriveChips.Children.OfType<ToggleButton>())
            if (chip.IsChecked == true && chip.Tag is string root &&
                _driveItems.TryGetValue(root, out var list))
                foreach (var item in list)
                    _items.Add(item);
    }

    private void RebuildRows()
    {
        IEnumerable<ImageItem> filtered = _search.Length == 0
            ? _items
            : _items.Where(MatchesSearch);

        List<ImageItem> list;
        if (!_scanning && _sortIndex != 0)
        {
            list = _sortIndex switch
            {
                1 => filtered.OrderByDescending(f => f.Modified).ToList(),
                2 => filtered.OrderByDescending(f => f.Length).ToList(),
                3 => filtered.OrderBy(f => f.Folder, Comparer<string>.Create((a, b) => NaturalCompare(a, b)))
                             .ThenBy(f => f.Name, Comparer<string>.Create((a, b) => NaturalCompare(a, b)))
                             .ToList(),
                _ => filtered.ToList()
            };
        }
        else
        {
            list = filtered.ToList();
        }

        _viewList = list;

        Rows.Clear();
        for (int i = 0; i < list.Count; i += _columns)
            Rows.Add(new RowVm(list.Skip(i).Take(_columns).ToList(), _columns));

        if (_scanning)
            CountText = $"{list.Count:N0} found";
        else
        {
            int folders = list.Select(f => f.Folder).Distinct().Count();
            CountText = folders > 1
                ? $"{list.Count:N0} images · {folders:N0} folders"
                : $"{list.Count:N0} images";
        }

        EmptyState.Visibility = !_scanning && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool MatchesSearch(ImageItem item) =>
        item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        item.Folder.Contains(_search, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- UI events

    private void OnScanAll(object sender, RoutedEventArgs e) => ScanAllDrives();

    private void OnStopScan(object sender, RoutedEventArgs e)
    {
        CancelScan();
        RebuildRows();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_folderScope is not null)
            StartScan([_folderScope]);
        else
            StartScan(SelectedRoots());
    }

    private void OnAllDrives(object sender, RoutedEventArgs e) => ScanAllDrives();

    private void OnDriveSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (Scanning)
            CancelScan();
        RebuildFromSelected();
        RebuildRows();
    }

    private void OnCycleSort(object sender, RoutedEventArgs e)
    {
        _sortIndex = (_sortIndex + 1) % SortNames.Length;
        SortBtn.Content = "Sort: " + SortNames[_sortIndex];
        if (!Scanning)
            RebuildRows();
    }

    private void OnOpenFile(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke(this, EventArgs.Empty);

    private void OnSetDefault(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        try
        {
            FileAssociation.RegisterAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Registration failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var choice = MessageBox.Show(
            owner,
            "HEIC Viewer is now registered in Windows' 'Open with' list for .heic / .heif / .hif / .avif files.\n\n" +
            "To make it the DEFAULT (Windows requires one manual confirmation):\n" +
            "  • Settings → Default apps → HEIC Viewer → set each file type, or\n" +
            "  • right-click any .heic file → Open with → Choose another app → HEIC Viewer → tick 'Always'.\n\n" +
            "Open Windows 'Default apps' settings now?",
            "Set as default", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (choice == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OnTileClicked(ImageItem item)
    {
        int index = _viewList.IndexOf(item);
        if (index >= 0)
            OpenRequested?.Invoke(_viewList, index);
    }

    private void UpdateScopeUi()
    {
        bool folder = _folderScope is not null;
        DriveChips.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        AllDrivesBtn.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int NaturalCompare(string? x, string? y)
    {
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            bool dx = char.IsDigit(x[ix]), dy = char.IsDigit(y[iy]);
            if (dx && dy)
            {
                long nx = 0, ny = 0;
                while (ix < x.Length && char.IsDigit(x[ix])) nx = nx * 10 + (x[ix++] - '0');
                while (iy < y.Length && char.IsDigit(y[iy])) ny = ny * 10 + (y[iy++] - '0');
                int cmp = nx.CompareTo(ny);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (cmp != 0) return cmp;
                ix++;
                iy++;
            }
        }
        return (x.Length - ix).CompareTo(y.Length - iy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
