using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageMagick;
using Microsoft.Win32;

namespace HeicViewer;

public partial class ViewerView : UserControl, INotifyPropertyChanged
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 40;
    private const double ZoomStep = 1.2;
    private static readonly TimeSpan SlideInterval = TimeSpan.FromSeconds(3);

    public event Action? BackRequested;
    public event Action<ImageItem?>? CurrentItemChanged;

    private readonly RotateTransform _rotate = new(0);
    private readonly ScaleTransform _scale = new(1, 1);

    private BitmapSource? _source;
    private Size _naturalSize;
    private double _zoom = 1;
    private bool _fitPending;
    private bool _isFit;
    private bool _loading;

    private IReadOnlyList<ImageItem> _items = Array.Empty<ImageItem>();
    private int _index;
    private string? _currentPath;

    private bool _dragging;
    private Point _dragOrigin;
    private double _dragH, _dragV;

    private DispatcherTimer? _slideshow;
    private readonly DispatcherTimer _chromeTimer;
    private bool _chromeVisible = true;
    public bool ChromeVisible
    {
        get => _chromeVisible;
        private set { if (_chromeVisible != value) { _chromeVisible = value; OnPropertyChanged(); } }
    }

    /// <summary>In fullscreen the floating chrome fades after mouse idle; windowed mode keeps it visible.</summary>
    public bool FullscreenMode { get; set; }

    public ViewerView()
    {
        InitializeComponent();
        DataContext = this;

        var contentTransform = new TransformGroup();
        contentTransform.Children.Add(_rotate);
        contentTransform.Children.Add(_scale);
        ContentGrid.LayoutTransform = contentTransform;

        _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _chromeTimer.Tick += (_, _) =>
        {
            if (FullscreenMode)
                ChromeVisible = false;
        };

        Scroller.SizeChanged += (_, _) =>
        {
            if (_fitPending)
            {
                _fitPending = false;
                FitToWindow();
            }
            else if (_isFit)
            {
                FitToWindow();
            }
        };
    }

    // ---------------------------------------------------------------- open

    public void Show(IReadOnlyList<ImageItem> items, int index)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        Strip.ItemsSource = _items;
        HintText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        // Chrome stays visible until the mouse moves (auto-hide starts on first movement).
        LoadCurrent();
    }

    private async void LoadCurrent()
    {
        if (_items.Count == 0 || _index < 0 || _index >= _items.Count)
        {
            ClearImage();
            return;
        }

        var item = _items[_index];
        foreach (var it in _items)
            it.IsCurrent = false;
        item.IsCurrent = true;
        Strip.ScrollIntoView(item);

        _currentPath = item.Path;
        _loading = true;
        SidePrev.IsEnabled = SideNext.IsEnabled = false;
        FileNameLabel.Text = item.Name;
        StatusText.Text = "Loading…";

        CurrentItemChanged?.Invoke(item);

        var result = await Task.Run(() => DecodeService.Decode(item.Path));

        _loading = false;
        if (!string.Equals(_currentPath, item.Path, StringComparison.OrdinalIgnoreCase))
            return;

        if (result.Success)
        {
            _source = result.Image;
            Img.Source = _source;
            _naturalSize = new Size(_source.Width, _source.Height);
            _rotate.Angle = 0;
            UpdateZoomLabel();
            FitToWindow();

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase()
            };
            Img.BeginAnimation(OpacityProperty, fadeIn);

            StatusText.Text =
                $"{result.PixelWidth} × {result.PixelHeight} px  ·  {_index + 1} / {_items.Count}" +
                (result.FrameCount > 1 ? $"  ·  {result.FrameCount} frames" : "") +
                $"  ·  {result.Decoder}";

            FileNameLabel.Text = item.Name;
            SidePrev.IsEnabled = SideNext.IsEnabled = _items.Count > 1;
            FillInfo(item, result);
        }
        else
        {
            ClearImage();
            StatusText.Text = "Could not open this file — " + result.Error;
        }
    }

    private void ClearImage()
    {
        _source = null;
        Img.Source = null;
        HintText.Visibility = Visibility.Visible;
        StatusText.Text = "";
        FileNameLabel.Text = "";
        SidePrev.IsEnabled = SideNext.IsEnabled = false;
        CurrentItemChanged?.Invoke(null);
    }

    private void FillInfo(ImageItem item, DecodeResult result)
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("Dimensions", $"{result.PixelWidth} × {result.PixelHeight}"),
            new("File size", item.SizeLabel),
            new("Modified", item.DateLabel),
            new("Folder", item.Folder),
            new("File", item.Name),
            new("Decoder", result.Decoder)
        };
        pairs.AddRange(result.Exif);
        InfoPairs.ItemsSource = pairs;
    }

    // ---------------------------------------------------------------- navigation

    private void Navigate(int delta)
    {
        if (_loading || _items.Count == 0)
            return;
        _index = (_index + delta + _items.Count) % _items.Count;
        LoadCurrent();
    }

    private int IndexOfItem(ImageItem item)
    {
        for (int i = 0; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], item))
                return i;
        return -1;
    }

    private void OnStripTileClicked(ImageItem item)
    {
        int index = IndexOfItem(item);
        if (index >= 0 && !_loading)
        {
            _index = index;
            LoadCurrent();
            NotifyActivity();
        }
    }

    // ---------------------------------------------------------------- slideshow

    private void OnToggleSlideshow(object sender, RoutedEventArgs e) => ToggleSlideshow();

    public void ToggleSlideshow()
    {
        if (_slideshow is not null)
        {
            StopSlideshow();
            return;
        }
        if (_source is null || _items.Count < 2)
            return;

        _slideshow = new DispatcherTimer { Interval = SlideInterval };
        _slideshow.Tick += (_, _) => Navigate(1);
        _slideshow.Start();
        SlideShowBtn.Content = "\uE769";   // Pause
        NotifyActivity();
    }

    public void StopSlideshow()
    {
        _slideshow?.Stop();
        _slideshow = null;
        SlideShowBtn.Content = "\uE768";   // Play
    }

    // ---------------------------------------------------------------- info

    private void OnToggleInfo(object sender, RoutedEventArgs e) => ToggleInfo();

    private void ToggleInfo()
    {
        InfoPanel.Visibility = InfoPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        NotifyActivity();
    }

    // ---------------------------------------------------------------- save as

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || _loading)
            return;

        var dlg = new SaveFileDialog
        {
            Title = "Save as",
            FileName = Path.GetFileNameWithoutExtension(_currentPath),
            Filter = "JPEG image|*.jpg|PNG image|*.png"
        };
        var owner = Window.GetWindow(this);
        if (dlg.ShowDialog(owner) != true)
            return;

        string source = _currentPath;
        string target = dlg.FileName;
        try
        {
            await Task.Run(() =>
            {
                using var frames = new MagickImageCollection();
                frames.Read(source);
                var best = frames[0];
                foreach (var f in frames)
                    if ((long)f.Width * f.Height > (long)best.Width * best.Height)
                        best = f;
                best.AutoOrient();
                if (target.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    target.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    best.Quality = 92;
                    best.Write(target, MagickFormat.Jpeg);
                }
                else
                {
                    best.Write(target, MagickFormat.Png);
                }
            });
            MessageBox.Show(owner, $"Saved: {target}", "HEIC Viewer",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- zoom / pan (same engine as before)

    private void FitToWindow()
    {
        if (_source is null)
            return;

        double w = _naturalSize.Width, h = _naturalSize.Height;
        if (_rotate.Angle is 90 or 270)
            (w, h) = (h, w);

        double vw = Scroller.ViewportWidth, vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0)
        {
            _fitPending = true;
            return;
        }

        double fit = Math.Min(vw / w, vh / h);
        _zoom = Math.Clamp(fit, MinZoom, 1.0);
        _isFit = true;
        _scale.ScaleX = _scale.ScaleY = _zoom;
        UpdateZoomLabel();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CenterContent);
    }

    private void ZoomAt(double factor, Point? anchorInViewport = null)
    {
        if (_source is null)
            return;

        _isFit = false;
        double old = _zoom;
        double newZoom = Math.Clamp(old * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - old) < 1e-9)
            return;
        factor = newZoom / old;
        _zoom = newZoom;
        _scale.ScaleX = _scale.ScaleY = _zoom;
        Scroller.UpdateLayout();

        var anchor = anchorInViewport ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        double contentX = anchor.X + Scroller.HorizontalOffset;
        double contentY = anchor.Y + Scroller.VerticalOffset;
        Scroller.ScrollToHorizontalOffset(contentX * factor - anchor.X);
        Scroller.ScrollToVerticalOffset(contentY * factor - anchor.Y);
        UpdateZoomLabel();
    }

    private void CenterContent()
    {
        double extraX = Math.Max(0, (Scroller.ExtentWidth - Scroller.ViewportWidth) / 2);
        double extraY = Math.Max(0, (Scroller.ExtentHeight - Scroller.ViewportHeight) / 2);
        Scroller.ScrollToHorizontalOffset(extraX);
        Scroller.ScrollToVerticalOffset(extraY);
    }

    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = Math.Round(_zoom * 100) + "%";
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_source is null)
            return;
        e.Handled = true;
        var p = e.GetPosition(Scroller);
        ZoomAt(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, p);
        NotifyActivity();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_source is null)
            return;

        if (e.ClickCount == 2)
        {
            if (Math.Abs(_zoom - 1.0) < 0.001) FitToWindow();
            else ZoomAt(1.0 / _zoom);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragOrigin = e.GetPosition(Scroller);
        _dragH = Scroller.HorizontalOffset;
        _dragV = Scroller.VerticalOffset;
        ContentGrid.CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var p = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_dragH - (p.X - _dragOrigin.X));
        Scroller.ScrollToVerticalOffset(_dragV - (p.Y - _dragOrigin.Y));
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ContentGrid.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void Rotate(double deltaDegrees)
    {
        if (_source is null)
            return;
        double angle = (_rotate.Angle + deltaDegrees) % 360;
        if (angle < 0)
            angle += 360;
        _rotate.Angle = angle;
        if (_isFit)
            FitToWindow();
        NotifyActivity();
    }

    // ---------------------------------------------------------------- chrome auto-hide

    private void OnRootMouseMove(object sender, MouseEventArgs e) => NotifyActivity();

    public void NotifyActivity()
    {
        ChromeVisible = true;
        if (!FullscreenMode)
        {
            _chromeTimer.Stop();
            return;
        }
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    // ---------------------------------------------------------------- keyboard

    public void HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Navigate(-1); e.Handled = true; break;
            case Key.Right: Navigate(1); e.Handled = true; break;
            case Key.Up:
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - Scroller.ViewportHeight * 0.6);
                e.Handled = true;
                break;
            case Key.Down:
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + Scroller.ViewportHeight * 0.6);
                e.Handled = true;
                break;
            case Key.Add or Key.OemPlus: ZoomAt(ZoomStep); e.Handled = true; break;
            case Key.Subtract or Key.OemMinus: ZoomAt(1 / ZoomStep); e.Handled = true; break;
            case Key.F: FitToWindow(); e.Handled = true; break;
            case Key.D1 or Key.NumPad1: if (_source is not null) ZoomAt(1.0 / _zoom); e.Handled = true; break;
            case Key.L: Rotate(-90); e.Handled = true; break;
            case Key.R: Rotate(90); e.Handled = true; break;
            case Key.I: ToggleInfo(); e.Handled = true; break;
            case Key.Space or Key.F5: ToggleSlideshow(); e.Handled = true; break;
            case Key.Home: if (!_loading && _items.Count > 0) { _index = 0; LoadCurrent(); } e.Handled = true; break;
            case Key.End: if (!_loading && _items.Count > 0) { _index = _items.Count - 1; LoadCurrent(); } e.Handled = true; break;
        }
    }

    // ---------------------------------------------------------------- toolbar events

    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void OnPrev(object sender, RoutedEventArgs e) => Navigate(-1);
    private void OnNext(object sender, RoutedEventArgs e) => Navigate(1);
    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAt(ZoomStep);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAt(1 / ZoomStep);
    private void OnFit(object sender, RoutedEventArgs e) => FitToWindow();
    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        if (_source is not null)
            ZoomAt(1.0 / _zoom);
    }
    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-90);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(90);

    private void OnFullscreen(object sender, RoutedEventArgs e)
    {
        // Let the host window handle it.
        var window = Window.GetWindow(this);
        if (window is MainWindow main)
            main.ToggleFullscreen();
    }

    // ----------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
