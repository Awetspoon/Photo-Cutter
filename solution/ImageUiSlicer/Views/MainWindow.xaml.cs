using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ImageUiSlicer.Models;
using ImageUiSlicer.Services;
using ImageUiSlicer.ViewModels;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ImageUiSlicer.Views;

public partial class MainWindow : Window
{
    private readonly CutoutPreviewFactory _cutoutPreviewFactory = new();
    private CutoutGalleryWindow? _galleryWindow;
    private bool _syncingSelection;
    private bool _applyingPaneLayout;
    private bool _galleryRefreshPending;
    private const int PolygonEscapeClearHoldMilliseconds = 3000;
    private readonly DispatcherTimer _polygonEscapeHoldTimer = new();
    private bool _polygonEscapeHoldPending;
    private bool _polygonEscapeHoldTriggered;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        DataContextChanged += MainWindow_DataContextChanged;
        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;
        _polygonEscapeHoldTimer.Interval = TimeSpan.FromMilliseconds(PolygonEscapeClearHoldMilliseconds);
        _polygonEscapeHoldTimer.Tick += PolygonEscapeHoldTimer_Tick;
        HookViewModel(null, DataContext as MainViewModel);
    }

    private MainViewModel VM => (MainViewModel)DataContext;

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HookViewModel(e.OldValue as MainViewModel, e.NewValue as MainViewModel);
    }

    private void HookViewModel(MainViewModel? oldViewModel, MainViewModel? newViewModel)
    {
        if (oldViewModel is not null)
        {
            oldViewModel.SelectionSyncRequested -= ViewModel_SelectionSyncRequested;
            oldViewModel.GalleryRefreshRequested -= ViewModel_GalleryRefreshRequested;
        }

        if (newViewModel is not null)
        {
            newViewModel.SelectionSyncRequested += ViewModel_SelectionSyncRequested;
            newViewModel.GalleryRefreshRequested += ViewModel_GalleryRefreshRequested;
            if (IsLoaded)
            {
                ApplyPaneLayout();
            }
        }
    }

    private void ViewModel_SelectionSyncRequested(object? sender, EventArgs e)
    {
        SyncListSelectionFromViewModel();
    }

    private void ViewModel_GalleryRefreshRequested(object? sender, EventArgs e)
    {
        ScheduleGalleryRefresh();
    }

    private void ScheduleGalleryRefresh()
    {
        if (_galleryRefreshPending || _galleryWindow is null)
        {
            return;
        }

        _galleryRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _galleryRefreshPending = false;
            RefreshGalleryWindow(activateIfVisible: false);
        }, DispatcherPriority.Background);
    }

    private void RefreshGalleryWindow(bool activateIfVisible)
    {
        if (_galleryWindow is null)
        {
            return;
        }

        var items = VM.SourceBitmap is null
            ? Array.Empty<CutoutPreviewItem>()
            : _cutoutPreviewFactory.BuildItems(VM.SourceBitmap, VM.Project.Cutouts);
        _galleryWindow.UpdateItems(items);

        if (activateIfVisible && _galleryWindow.IsVisible)
        {
            _galleryWindow.Activate();
        }
    }
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyPaneLayout();
    }

    private void TogglePreviewFocus_Click(object sender, RoutedEventArgs e)
    {
        if (!VM.IsPreviewFocusMode)
        {
            PersistVisiblePaneWidths();
        }

        VM.SetPreviewFocusMode(!VM.IsPreviewFocusMode);
        ApplyPaneLayout();
        CanvasHost.FocusCanvas();
    }

    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        PersistVisiblePaneWidths();
    }

    private void ApplyPaneLayout()
    {
        if (DataContext is not MainViewModel)
        {
            return;
        }

        _applyingPaneLayout = true;
        try
        {
            var isFocused = VM.IsPreviewFocusMode;
            LeftPaneCard.Visibility = isFocused ? Visibility.Collapsed : Visibility.Visible;
            RightPaneCard.Visibility = isFocused ? Visibility.Collapsed : Visibility.Visible;
            LeftPaneSplitter.Visibility = isFocused ? Visibility.Collapsed : Visibility.Visible;
            RightPaneSplitter.Visibility = isFocused ? Visibility.Collapsed : Visibility.Visible;
            LeftPaneColumn.Width = isFocused ? new GridLength(0) : new GridLength(VM.LeftPaneWidth);
            LeftSplitterColumn.Width = isFocused ? new GridLength(0) : new GridLength(8);
            RightSplitterColumn.Width = isFocused ? new GridLength(0) : new GridLength(8);
            RightPaneColumn.Width = isFocused ? new GridLength(0) : new GridLength(VM.RightPaneWidth);
        }
        finally
        {
            _applyingPaneLayout = false;
        }
    }

    private void PersistVisiblePaneWidths()
    {
        if (_applyingPaneLayout || VM.IsPreviewFocusMode)
        {
            return;
        }

        VM.StorePaneWidths(LeftPaneColumn.ActualWidth, RightPaneColumn.ActualWidth);
    }

    private void SyncListSelectionFromViewModel()
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        CutoutsList.SelectedItems.Clear();
        foreach (var cutout in VM.SelectedCutouts)
        {
            CutoutsList.SelectedItems.Add(cutout);
        }
        _syncingSelection = false;
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmContinueWithUnsavedChanges())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open Source Image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files|*.*",
            InitialDirectory = VM.GetInitialImageFolder(),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var bitmap = VM.LoadBitmap(dialog.FileName);
            VM.ApplyImageProject(dialog.FileName, bitmap);
            CanvasHost.FocusCanvas();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmContinueWithUnsavedChanges())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open Photo Cutter Project",
            Filter = "Photo Cutter Projects|*.iusproj|All Files|*.*",
            InitialDirectory = VM.GetInitialProjectFolder(),
        };

        if (dialog.ShowDialog() == true)
        {
            OpenProjectPath(dialog.FileName);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveProjectInteractive(forceSaveAs: false);
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        SaveProjectInteractive(forceSaveAs: true);
    }

    private void CommitSelection_Click(object sender, RoutedEventArgs e)
    {
        CanvasHost.TryFinalizePendingPolygon();
        if (VM.CommitSelectionCommand.CanExecute(null))
        {
            VM.CommitSelectionCommand.Execute(null);
        }

        CanvasHost.FocusCanvas();
    }

    private void PickExportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the base export folder. Photo Cutter writes into a cut outs subfolder inside it.",
            UseDescriptionForTitle = true,
            InitialDirectory = string.IsNullOrWhiteSpace(VM.ExportFolder) ? VM.GetInitialProjectFolder() : VM.ExportFolder,
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            VM.ExportFolder = dialog.SelectedPath;
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedCutouts.Count == 0)
        {
            return;
        }

        var message = VM.SelectedCutouts.Count == 1
            ? $"Delete cutout '{VM.PrimarySelectedCutout?.Name}'?"
            : $"Delete {VM.SelectedCutouts.Count} selected cutouts?";
        var result = MessageBox.Show(this, message, "Photo Cutter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            VM.DeleteSelectedCommand.Execute(null);
        }
    }

    private void FitView_Click(object sender, RoutedEventArgs e)
    {
        CanvasHost.FitToView();
        CanvasHost.FocusCanvas();
    }

    private void ZoomActual_Click(object sender, RoutedEventArgs e)
    {
        CanvasHost.ZoomActualPixels();
        CanvasHost.FocusCanvas();
    }

    private void OpenGallery_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SourceBitmap is null)
        {
            MessageBox.Show(this, "Load an image first, then commit selections to see them in the gallery.", "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_galleryWindow is null)
        {
            _galleryWindow = new CutoutGalleryWindow
            {
                Owner = this,
            };
            _galleryWindow.Closed += (_, _) =>
            {
                _galleryWindow = null;
                _galleryRefreshPending = false;
            };
        }

        RefreshGalleryWindow(activateIfVisible: false);
        if (_galleryWindow.IsVisible)
        {
            _galleryWindow.Activate();
            return;
        }

        _galleryWindow.Show();
    }
    private void Cutouts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        VM.SetSelectedCutouts(CutoutsList.SelectedItems.Cast<CutoutModel>());
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (dropped is null)
        {
            return;
        }

        var paths = dropped.Where(File.Exists).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        if (!ConfirmContinueWithUnsavedChanges())
        {
            return;
        }

        var projectPath = paths.FirstOrDefault(path => string.Equals(Path.GetExtension(path), ".iusproj", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            OpenProjectPath(projectPath);
            return;
        }

        var imagePaths = paths.Where(VM.IsSupportedImage).ToList();
        if (imagePaths.Count == 0)
        {
            return;
        }

        var chosenImage = imagePaths[0];
        try
        {
            var bitmap = VM.LoadBitmap(chosenImage);
            VM.ApplyImageProject(chosenImage, bitmap);
            if (imagePaths.Count > 1)
            {
                VM.StatusText = $"Loaded {Path.GetFileName(chosenImage)} from {imagePaths.Count} dropped images.";
            }

            CanvasHost.FocusCanvas();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmContinueWithUnsavedChanges())
        {
            e.Cancel = true;
            return;
        }

        _galleryWindow?.CloseForShutdown();
        PersistVisiblePaneWidths();
    }

    private static bool IsKeyboardInputFocused()
    {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is TextBox or System.Windows.Controls.ComboBox or System.Windows.Controls.ComboBoxItem)
            {
                return true;
            }

            current = current switch
            {
                Visual => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current),
            };
        }

        return false;
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        ResetPolygonEscapeHold();
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (!_polygonEscapeHoldPending && !_polygonEscapeHoldTriggered))
        {
            return;
        }

        var shouldUndo = _polygonEscapeHoldPending && !_polygonEscapeHoldTriggered;
        ResetPolygonEscapeHold();
        if (shouldUndo && CanvasHost.UndoPendingPolygonPoint())
        {
            CanvasHost.FocusCanvas();
        }

        e.Handled = true;
    }

    private void BeginPolygonEscapeHold()
    {
        if (_polygonEscapeHoldPending || _polygonEscapeHoldTriggered)
        {
            return;
        }

        _polygonEscapeHoldPending = true;
        _polygonEscapeHoldTriggered = false;
        _polygonEscapeHoldTimer.Stop();
        _polygonEscapeHoldTimer.Start();
        VM.StatusText = $"Release Esc to remove the last polygon line. Hold Esc for {PolygonEscapeClearHoldMilliseconds / 1000} seconds to clear the whole shape.";
    }

    private void PolygonEscapeHoldTimer_Tick(object? sender, EventArgs e)
    {
        _polygonEscapeHoldTimer.Stop();

        if (!_polygonEscapeHoldPending || !CanvasHost.HasPendingPolygon)
        {
            _polygonEscapeHoldPending = false;
            _polygonEscapeHoldTriggered = false;
            return;
        }

        _polygonEscapeHoldPending = false;
        _polygonEscapeHoldTriggered = CanvasHost.ClearPendingPolygon();
        if (_polygonEscapeHoldTriggered)
        {
            CanvasHost.FocusCanvas();
        }
    }

    private void ResetPolygonEscapeHold()
    {
        _polygonEscapeHoldTimer.Stop();
        _polygonEscapeHoldPending = false;
        _polygonEscapeHoldTriggered = false;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isEditorInputFocused = IsKeyboardInputFocused();
        if (!isEditorInputFocused && e.Key == Key.Escape && (CanvasHost.HasPendingPolygon || _polygonEscapeHoldPending || _polygonEscapeHoldTriggered))
        {
            if (!_polygonEscapeHoldPending && !_polygonEscapeHoldTriggered && !e.IsRepeat)
            {
                BeginPolygonEscapeHold();
            }

            e.Handled = true;
            return;
        }

        if (!isEditorInputFocused && CanvasHost.HandleEditorKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.N:
                    OpenImage_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.O:
                    OpenProject_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.S:
                    SaveProjectInteractive(forceSaveAs: modifiers.HasFlag(ModifierKeys.Shift));
                    e.Handled = true;
                    return;
                case Key.D0:
                    FitView_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D1:
                    ZoomActual_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.A when !isEditorInputFocused:
                    SelectAllCutouts();
                    e.Handled = true;
                    return;
                case Key.D when !modifiers.HasFlag(ModifierKeys.Shift) && !isEditorInputFocused:
                    if (VM.DuplicateSelectedCutoutCommand.CanExecute(null))
                    {
                        VM.DuplicateSelectedCutoutCommand.Execute(null);
                        CanvasHost.FocusCanvas();
                    }
                    e.Handled = true;
                    return;
                case Key.V when modifiers.HasFlag(ModifierKeys.Shift) && !isEditorInputFocused:
                    if (VM.PasteSelectedShapeCommand.CanExecute(null))
                    {
                        VM.PasteSelectedShapeCommand.Execute(null);
                        CanvasHost.FocusCanvas();
                    }
                    e.Handled = true;
                    return;
                case Key.Z when !isEditorInputFocused:
                    if (VM.UndoCommand.CanExecute(null))
                    {
                        VM.UndoCommand.Execute(null);
                    }
                    e.Handled = true;
                    return;
                case Key.Y when !isEditorInputFocused:
                    if (VM.RedoCommand.CanExecute(null))
                    {
                        VM.RedoCommand.Execute(null);
                    }
                    e.Handled = true;
                    return;
            }
        }

        if (isEditorInputFocused)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.V:
                VM.IsSelectTool = true;
                e.Handled = true;
                break;
            case Key.L:
                VM.IsLassoTool = true;
                e.Handled = true;
                break;
            case Key.P:
                VM.IsPolygonTool = true;
                e.Handled = true;
                break;
            case Key.S:
                VM.IsShapeTool = true;
                e.Handled = true;
                break;
            case Key.B:
                VM.IsBrushAddTool = true;
                e.Handled = true;
                break;
            case Key.E:
                VM.IsBrushEraseTool = true;
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelected_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F11:
                TogglePreviewFocus_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                e.Handled = HandleNudge(-GetNudgeStep(modifiers), 0);
                break;
            case Key.Right:
                e.Handled = HandleNudge(GetNudgeStep(modifiers), 0);
                break;
            case Key.Up:
                e.Handled = HandleNudge(0, -GetNudgeStep(modifiers));
                break;
            case Key.Down:
                e.Handled = HandleNudge(0, GetNudgeStep(modifiers));
                break;
        }
    }

    private bool HandleNudge(int dx, int dy)
    {
        var handled = VM.TryNudgeSelectionOrCutouts(dx, dy);
        if (handled)
        {
            CanvasHost.FocusCanvas();
        }

        return handled;
    }

    private int GetNudgeStep(ModifierKeys modifiers) => modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;

    private void SelectAllCutouts()
    {
        if (VM.Project.Cutouts.Count == 0)
        {
            return;
        }

        _syncingSelection = true;
        CutoutsList.SelectedItems.Clear();
        foreach (var cutout in VM.Project.Cutouts)
        {
            CutoutsList.SelectedItems.Add(cutout);
        }
        _syncingSelection = false;
        VM.SetSelectedCutouts(VM.Project.Cutouts);
    }

    private bool SaveProjectInteractive(bool forceSaveAs)
    {
        try
        {
            var projectPath = VM.Project.ProjectFilePath;
            if (forceSaveAs || string.IsNullOrWhiteSpace(projectPath))
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save Photo Cutter Project",
                    Filter = "Photo Cutter Projects|*.iusproj|All Files|*.*",
                    DefaultExt = ".iusproj",
                    AddExtension = true,
                    FileName = $"{VM.Project.ProjectName}.iusproj",
                    InitialDirectory = VM.GetInitialProjectFolder(),
                };

                if (dialog.ShowDialog() != true)
                {
                    return false;
                }

                projectPath = dialog.FileName;
            }

            VM.SaveProject(projectPath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool ConfirmContinueWithUnsavedChanges()
    {
        if (!VM.IsDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "Save changes before continuing?\n\nYes = Save, No = Discard, Cancel = Stay here.",
            "Photo Cutter",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => SaveProjectInteractive(forceSaveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void OpenProjectPath(string projectPath)
    {
        try
        {
            var project = VM.LoadProjectMetadata(projectPath);
            var imagePath = project.SourceImage.Path;
            var originalWidth = project.SourceImage.PixelWidth;
            var originalHeight = project.SourceImage.PixelHeight;

            if (!File.Exists(imagePath))
            {
                MessageBox.Show(this, "Source image not found. Pick the replacement image to continue.", "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Information);
                var locateDialog = new OpenFileDialog
                {
                    Title = "Locate Source Image",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files|*.*",
                    InitialDirectory = VM.GetInitialImageFolder(),
                };
                if (locateDialog.ShowDialog() != true)
                {
                    return;
                }

                imagePath = locateDialog.FileName;
            }

            var bitmap = VM.LoadBitmap(imagePath);
            project.SourceImage.Path = imagePath;
            project.SourceImage.PixelWidth = bitmap.Width;
            project.SourceImage.PixelHeight = bitmap.Height;
            VM.ApplyLoadedProject(project, bitmap, projectPath);

            if ((originalWidth > 0 && originalWidth != bitmap.Width) || (originalHeight > 0 && originalHeight != bitmap.Height))
            {
                MessageBox.Show(this, "The relinked image dimensions differ from the original project. Existing cutouts may no longer align perfectly.", "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            CanvasHost.FocusCanvas();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Photo Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}



















