using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace HeicViewer;

public partial class MainWindow : Window
{
    private bool _viewerActive;
    private bool _fullscreen;
    private WindowState _restoreState = WindowState.Normal;
    private WindowStyle _restoreStyle = WindowStyle.SingleBorderWindow;

    public MainWindow()
    {
        InitializeComponent();

        Library.OpenRequested += (items, index) =>
        {
            Viewer.Show(items, index);
            ActivateViewer();
        };
        Library.OpenFileRequested += (_, _) => OpenFileViaDialog();

        Viewer.BackRequested += ShowLibrary;
        Viewer.CurrentItemChanged += item =>
            Title = item is null ? "HEIC Viewer" : item.Name + " — HEIC Viewer";
    }

    // ---------------------------------------------------------------- public entry points

    /// <summary>Launch state: build the full library from all drives.</summary>
    public void StartLibrary() => Library.ScanAllDrives();

    public void HandleExternal(string path)
    {
        if (File.Exists(path))
            OpenFileInViewer(path);
        else if (Directory.Exists(path))
            OpenFolderLibrary(path);
    }

    public void OpenFileInViewer(string path)
    {
        string full = Path.GetFullPath(path);
        var items = BuildFolderItems(Path.GetDirectoryName(full) ?? "");
        int index = items.FindIndex(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            items.Insert(0, new ImageItem(full));
            index = 0;
        }
        Viewer.Show(items, index);
        ActivateViewer();
    }

    public void OpenFolderLibrary(string folder)
    {
        ShowLibrary();
        Library.OpenFolder(folder);
    }

    private static List<ImageItem> BuildFolderItems(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(DecodeService.IsSupported)
                .Select(f => new ImageItem(f))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- view switching

    private void ActivateViewer()
    {
        _viewerActive = true;
        Library.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Visible;
        FadeIn(Viewer);
    }

    private void ShowLibrary()
    {
        Viewer.StopSlideshow();
        Viewer.InfoPanel.Visibility = Visibility.Collapsed;
        _viewerActive = false;
        Viewer.Visibility = Visibility.Collapsed;
        Library.Visibility = Visibility.Visible;
        FadeIn(Library);
        Title = "HEIC Viewer";
    }

    private static void FadeIn(FrameworkElement element)
    {
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void OpenFileViaDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open image",
            Filter = "Images|*.heic;*.heif;*.hif;*.avif;*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
                     "HEIC (HEIF family)|*.heic;*.heif;*.hif|" +
                     "All files|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            OpenFileInViewer(dlg.FileName);
    }

    public void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            WindowStyle = _restoreStyle;
            WindowState = _restoreState;
            ResizeMode = ResizeMode.CanResize;
            _fullscreen = false;
            Viewer.FullscreenMode = false;
        }
        else
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            _fullscreen = true;
            Viewer.FullscreenMode = true;
        }
        Viewer.NotifyActivity();
    }

    // ---------------------------------------------------------------- input routing

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenFileViaDialog();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                return;
            case Key.Escape:
                if (_fullscreen)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                    return;
                }
                if (_viewerActive)
                {
                    ShowLibrary();
                    e.Handled = true;
                    return;
                }
                break;
        }

        if (_viewerActive)
            Viewer.HandleKey(e);
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewerActive)
            Viewer.NotifyActivity();
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && _viewerActive)
            ShowLibrary();
    }

    // ---------------------------------------------------------------- drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;

        foreach (var file in files)
        {
            if (Directory.Exists(file))
            {
                OpenFolderLibrary(file);
                return;
            }
        }
        foreach (var file in files)
        {
            if (File.Exists(file) && DecodeService.IsSupported(file))
            {
                OpenFileInViewer(file);
                return;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Library.CancelScan();
        base.OnClosed(e);
    }
}
