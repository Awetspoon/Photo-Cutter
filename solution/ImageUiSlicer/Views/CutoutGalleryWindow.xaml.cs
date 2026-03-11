using System.ComponentModel;
using System.Windows;
using ImageUiSlicer.Models;

namespace ImageUiSlicer.Views;

public partial class CutoutGalleryWindow : Window
{
    private bool _allowClose;

    public CutoutGalleryWindow()
    {
        InitializeComponent();
        Closing += CutoutGalleryWindow_Closing;
    }

    public void UpdateItems(IReadOnlyList<CutoutPreviewItem> items)
    {
        GalleryItemsControl.ItemsSource = items;
        GalleryCountText.Text = items.Count == 1 ? "1 cutout" : $"{items.Count} cutouts";
        var hasItems = items.Count > 0;
        EmptyStatePanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        GalleryScrollViewer.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private void CutoutGalleryWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
