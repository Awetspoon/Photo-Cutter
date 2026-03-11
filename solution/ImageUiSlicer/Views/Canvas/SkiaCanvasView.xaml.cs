using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;
using ImageUiSlicer.ViewModels;

namespace ImageUiSlicer.Views;

public partial class SkiaCanvasView : UserControl
{
    private readonly CanvasController _controller = new();
    private MainViewModel? _viewModel;
    private ProjectModel? _observedProject;

    public SkiaCanvasView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => InvalidateCanvas();
        SizeChanged += (_, _) => InvalidateCanvas();
    }

    public void FitToView()
    {
        _controller.FitToView();
        InvalidateCanvas();
    }

    public void ZoomActualPixels()
    {
        _controller.ZoomActualPixels();
        InvalidateCanvas();
    }

    public bool HasPendingPolygon => _controller.HasPendingPolygon;

    public bool HandleEditorKey(Key key)
    {
        var handled = _controller.HandleKey(key);
        if (handled)
        {
            InvalidateCanvas();
        }

        return handled;
    }

    public bool TryFinalizePendingPolygon()
    {
        var handled = _controller.TryFinalizePendingPolygon();
        if (handled)
        {
            InvalidateCanvas();
        }

        return handled;
    }

    public bool UndoPendingPolygonPoint()
    {
        var handled = _controller.UndoPendingPolygonPoint();
        if (handled)
        {
            InvalidateCanvas();
        }

        return handled;
    }

    public bool ClearPendingPolygon()
    {
        var handled = _controller.ClearPendingPolygon();
        if (handled)
        {
            InvalidateCanvas();
        }

        return handled;
    }

    public void FocusCanvas() => Focus();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            UnobserveProject(_observedProject);
        }

        _viewModel = e.NewValue as MainViewModel;
        _controller.Attach(_viewModel);
        if (_viewModel is null)
        {
            _observedProject = null;
            return;
        }

        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        ObserveProject(_viewModel.Project);
        InvalidateCanvas();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.Project))
        {
            UnobserveProject(_observedProject);
            ObserveProject(_viewModel.Project);
        }

        if (e.PropertyName == nameof(MainViewModel.ActiveTool))
        {
            _controller.NotifyToolChanged();
        }

        InvalidateCanvas();
    }

    private void ObserveProject(ProjectModel project)
    {
        _observedProject = project;
        _observedProject.Cutouts.CollectionChanged += CutoutsOnCollectionChanged;
        SubscribeCutoutItems(_observedProject.Cutouts);
    }

    private void UnobserveProject(ProjectModel? project)
    {
        if (project is null)
        {
            return;
        }

        project.Cutouts.CollectionChanged -= CutoutsOnCollectionChanged;
        UnsubscribeCutoutItems(project.Cutouts);
        if (ReferenceEquals(_observedProject, project))
        {
            _observedProject = null;
        }
    }

    private void CutoutsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CutoutModel cutout in e.OldItems)
            {
                cutout.PropertyChanged -= CutoutOnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (CutoutModel cutout in e.NewItems)
            {
                cutout.PropertyChanged += CutoutOnPropertyChanged;
            }
        }

        InvalidateCanvas();
    }

    private void CutoutOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateCanvas();
    }

    private void SubscribeCutoutItems(IEnumerable<CutoutModel> cutouts)
    {
        foreach (var cutout in cutouts)
        {
            cutout.PropertyChanged += CutoutOnPropertyChanged;
        }
    }

    private void UnsubscribeCutoutItems(IEnumerable<CutoutModel> cutouts)
    {
        foreach (var cutout in cutouts)
        {
            cutout.PropertyChanged -= CutoutOnPropertyChanged;
        }
    }

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        _controller.Render(e.Surface.Canvas, e.Info.Width, e.Info.Height);
    }

    private void InvalidateCanvas() => CanvasSurface.InvalidateVisual();
    private Point MapMouseToCanvasPixels(MouseEventArgs e)
    {
        var position = e.GetPosition(CanvasSurface);
        var canvasSize = CanvasSurface.CanvasSize;
        if (CanvasSurface.ActualWidth <= 0 ||
            CanvasSurface.ActualHeight <= 0 ||
            canvasSize.Width <= 0 ||
            canvasSize.Height <= 0)
        {
            return position;
        }

        var x = position.X * (canvasSize.Width / CanvasSurface.ActualWidth);
        var y = position.Y * (canvasSize.Height / CanvasSurface.ActualHeight);
        return new Point(x, y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        FocusCanvas();
        CaptureMouse();
        _controller.OnMouseDown(MapMouseToCanvasPixels(e), Keyboard.Modifiers, e.ClickCount);
        InvalidateCanvas();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _controller.OnMouseMove(MapMouseToCanvasPixels(e), e.LeftButton == MouseButtonState.Pressed, e.RightButton == MouseButtonState.Pressed, Keyboard.Modifiers);
        InvalidateCanvas();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        _controller.OnMouseUp(MapMouseToCanvasPixels(e));
        if (!IsMouseOver)
        {
            _controller.OnMouseLeave();
        }
        InvalidateCanvas();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        FocusCanvas();
        CaptureMouse();
        _controller.OnPanStart(MapMouseToCanvasPixels(e), Keyboard.Modifiers);
        InvalidateCanvas();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ReleaseMouseCapture();
        _controller.OnPanEnd();
        if (!IsMouseOver)
        {
            _controller.OnMouseLeave();
        }
        InvalidateCanvas();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _controller.OnMouseWheel(MapMouseToCanvasPixels(e), e.Delta);
        InvalidateCanvas();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured)
        {
            _controller.OnMouseLeave();
        }
        InvalidateCanvas();
    }
}







