using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace HeicViewer;

/// <summary>
/// One tile in the library grid or the viewer's thumbnail strip.
/// Loads its thumbnail lazily (disk cache first) and releases it when scrolled out.
/// </summary>
public partial class ThumbTile : UserControl
{
    public event Action<ImageItem>? Clicked;

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(ImageItem), typeof(ThumbTile),
        new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty IsStripProperty = DependencyProperty.Register(
        nameof(IsStrip), typeof(bool), typeof(ThumbTile),
        new PropertyMetadata(false, OnLayoutChanged));

    public ImageItem? Item
    {
        get => (ImageItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public bool IsStrip
    {
        get => (bool)GetValue(IsStripProperty);
        set => SetValue(IsStripProperty, value);
    }

    private ImageItem? _subscribed;

    public ThumbTile()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseLeftButtonUp += OnMouseUp;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThumbTile)d).RefreshItem((ImageItem?)e.NewValue);

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThumbTile)d).ApplyLayout();

    private void RefreshItem(ImageItem? newItem)
    {
        if (_subscribed is not null)
            _subscribed.CacheReady -= OnCacheReady;
        _subscribed = newItem;
        if (newItem is not null)
            newItem.CacheReady += OnCacheReady;
        ShowThumb();
    }

    private void OnCacheReady()
    {
        if (IsLoaded)
            ShowThumb();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        ShowThumb();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Release decoded pixels for items that scrolled out of view.
        if (Item is not null)
            Item.Thumb = null;
    }

    private void ShowThumb()
    {
        var item = Item;
        if (item is null)
            return;

        if (item.Thumb is not null)
        {
            ThumbImage.Source = item.Thumb;
            ThumbImage.Visibility = Visibility.Visible;
            Placeholder.Visibility = Visibility.Collapsed;
            return;
        }

        string cachePath = ThumbnailService.CachePathFor(item);
        if (File.Exists(cachePath))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 320;
                bi.UriSource = new Uri(cachePath);
                bi.EndInit();
                bi.Freeze();
                item.Thumb = bi;
                ThumbImage.Source = bi;
                ThumbImage.Visibility = Visibility.Visible;
                Placeholder.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // Corrupt cache file — fall through and re-render it.
            }
        }

        ThumbImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        ThumbnailService.Request(item, urgent: IsStrip || IsLoaded);
    }

    private void ApplyLayout()
    {
        Height = IsStrip ? 88 : 212;
        Caption.Visibility = IsStrip ? Visibility.Collapsed : Visibility.Visible;
        ImageArea.Margin = new Thickness(IsStrip ? 5 : 6);
        Root.Margin = new Thickness(IsStrip ? 3 : 4);

        // Bound the thumbnail's measured size so tiles never force the grid row wider
        // than its share of the viewport.
        ThumbImage.MaxWidth = IsStrip ? 78 : 205;
        ThumbImage.MaxHeight = IsStrip ? 78 : 160;
    }

    private void OnMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Item is not null)
        {
            Clicked?.Invoke(Item);
            e.Handled = true;
        }
    }
}
