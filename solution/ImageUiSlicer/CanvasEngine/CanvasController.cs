using System.Windows;
using System.Windows.Input;
using ImageUiSlicer.Models;
using ImageUiSlicer.ViewModels;
using SkiaSharp;

namespace ImageUiSlicer.CanvasEngine;

public sealed class CanvasController
{
    private const float MinScale = 0.05f;
    private const float MaxScale = 32f;
    private const float PolygonSnapStepDegrees = 15f;
    private const float MinPolygonLabelScreenLength = 42f;
    private const double SelectPanThreshold = 6d;
    private const double ActiveSelectionDragThreshold = 4d;

    private MainViewModel? _viewModel;
    private float _viewportWidth;
    private float _viewportHeight;
    private bool _isPanning;
    private Point _lastPointer;
    private bool _isFreehandDrawing;
    private readonly List<PointF> _freehandPoints = new();
    private readonly List<PointF> _polygonPoints = new();
    private bool _isShapeDrawing;
    private PointF _shapeCenterPoint;
    private PointF _shapeEdgePoint;
    private PointF _hoverImagePoint;
    private bool _hasHoverImagePoint;
    private bool _isBrushRefining;
    private bool _brushAddMode;
    private ModifierKeys _currentModifiers;
    private bool _isSelectPanCandidate;
    private Point _selectPanStart;
    private bool _isActiveSelectionMoveCandidate;
    private bool _isMovingActiveSelection;
    private bool _activeSelectionMoveChanged;
    private Point _activeSelectionMoveStartScreen;
    private PointF _activeSelectionMoveStartImagePoint;
    private PathGeometryModel? _activeSelectionMoveOriginGeometry;

    public bool HasPendingPolygon => _viewModel?.ActiveTool == CanvasTool.Polygon && _polygonPoints.Count > 0;

    public void Attach(MainViewModel? viewModel)
    {
        _viewModel = viewModel;
        _freehandPoints.Clear();
        _polygonPoints.Clear();
        _isShapeDrawing = false;
        _hasHoverImagePoint = false;
        _isBrushRefining = false;
        _currentModifiers = ModifierKeys.None;
        _isPanning = false;
        _isSelectPanCandidate = false;
        ResetActiveSelectionMove();
    }

    public void FitToView()
    {
        if (_viewModel?.SourceBitmap is null || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return;
        }

        var fitScale = ComputeFitScale(_viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        _viewModel.SetCanvasView(fitScale, 0, 0);
    }

    public void ZoomActualPixels()
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        _viewModel.SetCanvasView(1, _viewModel.PanX, _viewModel.PanY);
    }

    public void NotifyToolChanged()
    {
        if (_viewModel?.ActiveTool != CanvasTool.Polygon)
        {
            _polygonPoints.Clear();
        }

        if (_viewModel?.ActiveTool != CanvasTool.Lasso)
        {
            _freehandPoints.Clear();
            _isFreehandDrawing = false;
        }

        if (_viewModel?.ActiveTool != CanvasTool.Shape)
        {
            _isShapeDrawing = false;
        }

        if (_viewModel is not null && !IsBrushTool(_viewModel.ActiveTool))
        {
            EndBrushRefine();
        }
    }

    public bool HandleKey(Key key)
    {
        if (_viewModel is null)
        {
            return false;
        }

        if (key == Key.Escape)
        {
            if (_isBrushRefining)
            {
                EndBrushRefine();
                return true;
            }

            if (_isShapeDrawing)
            {
                _isShapeDrawing = false;
                _viewModel.StatusText = "Shape preview cleared.";
                return true;
            }

            if (_polygonPoints.Count > 0)
            {
                return UndoPendingPolygonPoint();
            }

            if (_viewModel.HasActiveSelection)
            {
                _viewModel.ClearActiveSelection();
                _viewModel.StatusText = "Selection cleared.";
                return true;
            }
        }

        if (_viewModel.ActiveTool == CanvasTool.Polygon && key == Key.Back && _polygonPoints.Count > 0)
        {
            _polygonPoints.RemoveAt(_polygonPoints.Count - 1);
            _viewModel.StatusText = "Removed last polygon point.";
            return true;
        }

        if (_viewModel.ActiveTool == CanvasTool.Polygon && key == Key.Enter)
        {
            return FinalizePolygon();
        }

        if (key == Key.Add || key == Key.OemPlus)
        {
            ZoomAtViewportCenter(1.15f);
            _viewModel.StatusText = "Zoom changed for tracing only. Export resolution stays original.";
            return true;
        }

        if (key == Key.Subtract || key == Key.OemMinus)
        {
            ZoomAtViewportCenter(1f / 1.15f);
            _viewModel.StatusText = "Zoom changed for tracing only. Export resolution stays original.";
            return true;
        }

        return false;
    }

    public void OnMouseDown(Point position, ModifierKeys modifiers, int clickCount)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        _currentModifiers = modifiers;
        _lastPointer = position;

        if (Keyboard.IsKeyDown(Key.Space))
        {
            BeginPan(position);
            return;
        }

        switch (_viewModel.ActiveTool)
        {
            case CanvasTool.Select:
                if (TryBeginActiveSelectionMove(position))
                {
                    return;
                }

                if (CanDirectPanOnSelect())
                {
                    _isSelectPanCandidate = true;
                    _selectPanStart = position;
                    return;
                }

                SelectAt(position);
                break;
            case CanvasTool.Lasso:
                BeginFreehand(position);
                break;
            case CanvasTool.Polygon:
                AddPolygonPoint(position, clickCount);
                break;
            case CanvasTool.Shape:
                BeginShape(position);
                break;
            case CanvasTool.BrushAdd:
                BeginBrushRefine(position, addMode: true);
                break;
            case CanvasTool.BrushErase:
                BeginBrushRefine(position, addMode: false);
                break;
        }
    }

    public void OnMouseMove(Point position, bool leftButtonPressed, bool panButtonPressed, ModifierKeys modifiers)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        _currentModifiers = modifiers;
        _hasHoverImagePoint = TryScreenToImage(position, out _hoverImagePoint);
        if (_hasHoverImagePoint)
        {
            _hoverImagePoint = ClampToImage(_hoverImagePoint);
        }

        if (_isSelectPanCandidate && leftButtonPressed && CanDirectPanOnSelect())
        {
            var dx = position.X - _selectPanStart.X;
            var dy = position.Y - _selectPanStart.Y;
            if (!_isPanning && ((dx * dx) + (dy * dy) >= SelectPanThreshold * SelectPanThreshold))
            {
                BeginPan(position);
                _viewModel.StatusText = "Panning zoomed canvas.";
            }
        }

        if ((_isActiveSelectionMoveCandidate || _isMovingActiveSelection) && leftButtonPressed)
        {
            if (!TryScreenToImage(position, out var movePoint) || _activeSelectionMoveOriginGeometry is null)
            {
                return;
            }

            movePoint = ClampToImage(movePoint);
            var screenDx = position.X - _activeSelectionMoveStartScreen.X;
            var screenDy = position.Y - _activeSelectionMoveStartScreen.Y;
            if (!_isMovingActiveSelection && ((screenDx * screenDx) + (screenDy * screenDy) >= ActiveSelectionDragThreshold * ActiveSelectionDragThreshold))
            {
                if (_viewModel.BeginActiveSelectionMove())
                {
                    _isMovingActiveSelection = true;
                    _viewModel.StatusText = "Moving active selection.";
                }
                else
                {
                    ResetActiveSelectionMove();
                    return;
                }
            }

            if (_isMovingActiveSelection)
            {
                var moved = _viewModel.TryMoveActiveSelectionFromOrigin(
                    _activeSelectionMoveOriginGeometry,
                    movePoint.X - _activeSelectionMoveStartImagePoint.X,
                    movePoint.Y - _activeSelectionMoveStartImagePoint.Y);
                _activeSelectionMoveChanged = _activeSelectionMoveChanged || moved;
                return;
            }
        }
        if (_isPanning && (leftButtonPressed || panButtonPressed))
        {
            var deltaX = position.X - _lastPointer.X;
            var deltaY = position.Y - _lastPointer.Y;
            _viewModel.SetCanvasView(_viewModel.CanvasScale, _viewModel.PanX + deltaX, _viewModel.PanY + deltaY);
            _lastPointer = position;
            return;
        }

        if (_isPanning && !leftButtonPressed && !panButtonPressed)
        {
            _isPanning = false;
            _isSelectPanCandidate = false;
        ResetActiveSelectionMove();
    }

        if (_viewModel is not null && _viewModel.ActiveTool == CanvasTool.Shape && _isShapeDrawing)
        {
            if (leftButtonPressed)
            {
                UpdateShape(position);
            }
            else
            {
                EndShape(position);
            }

            return;
        }

        if (_viewModel is not null && IsBrushTool(_viewModel.ActiveTool))
        {
            if (_isBrushRefining && leftButtonPressed)
            {
                ContinueBrushRefine(position);
                return;
            }

            if (_isBrushRefining && !leftButtonPressed)
            {
                EndBrushRefine();
                return;
            }
        }

        if (_viewModel is not null && _viewModel.ActiveTool == CanvasTool.Lasso && _isFreehandDrawing && leftButtonPressed)
        {
            if (!TryScreenToImage(position, out var imagePoint))
            {
                return;
            }

            imagePoint = ClampToImage(imagePoint);
            if (_freehandPoints.Count == 0 || Distance(_freehandPoints[^1], imagePoint) >= 1.5f)
            {
                _freehandPoints.Add(imagePoint);
            }
        }
    }

    public void OnMouseUp(Point position)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        if (_isActiveSelectionMoveCandidate || _isMovingActiveSelection)
        {
            var moved = _activeSelectionMoveChanged;
            if (_isMovingActiveSelection && _viewModel is not null)
            {
                _viewModel.CompleteActiveSelectionMove(moved);
            }

            ResetActiveSelectionMove();
            if (moved)
            {
                return;
            }
        }
        if (_isSelectPanCandidate)
        {
            var shouldSelect = !_isPanning;
            _isSelectPanCandidate = false;
            if (_isPanning)
            {
                _isPanning = false;
                return;
            }

            if (shouldSelect)
            {
                SelectAt(position);
                return;
            }
        }

        if (_isPanning)
        {
            _isPanning = false;
            return;
        }

        if (_viewModel is not null && _viewModel.ActiveTool == CanvasTool.Shape && _isShapeDrawing)
        {
            EndShape(position);
            return;
        }

        if (_viewModel is not null && IsBrushTool(_viewModel.ActiveTool))
        {
            EndBrushRefine();
            return;
        }

        if (_viewModel is not null && _viewModel.ActiveTool == CanvasTool.Lasso && _isFreehandDrawing)
        {
            EndFreehand(position);
        }
    }

    public void OnMouseLeave()
    {
        _hasHoverImagePoint = false;
        _isPanning = false;
        _isSelectPanCandidate = false;
        ResetActiveSelectionMove();
        EndBrushRefine();
    }

    public void OnMouseWheel(Point position, int delta)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        if (!TryScreenToImage(position, out var focusPoint))
        {
            focusPoint = new PointF(_viewModel.SourceBitmap.Width / 2f, _viewModel.SourceBitmap.Height / 2f);
        }

        var scaleFactor = delta > 0 ? 1.1f : 0.9f;
        var currentScale = Math.Max(MinScale, (float)(_viewModel.CanvasScale <= 0 ? ComputeFitScale(_viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height) : _viewModel.CanvasScale));
        var newScale = Math.Clamp(currentScale * scaleFactor, MinScale, MaxScale);

        var offsetBefore = GetImageOffset(currentScale, _viewModel.PanX, _viewModel.PanY, _viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        var screenX = offsetBefore.X + (focusPoint.X * currentScale);
        var screenY = offsetBefore.Y + (focusPoint.Y * currentScale);

        var offsetAfter = GetCenteredOffset(newScale, _viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        var newPanX = screenX - offsetAfter.X - (focusPoint.X * newScale);
        var newPanY = screenY - offsetAfter.Y - (focusPoint.Y * newScale);

        _viewModel.SetCanvasView(newScale, newPanX, newPanY);
    }

    public void Render(SKCanvas canvas, int width, int height)
    {
        _viewportWidth = width;
        _viewportHeight = height;

        canvas.Clear(new SKColor(12, 18, 24));
        DrawBackdrop(canvas, width, height);

        if (_viewModel is null || _viewModel.SourceBitmap is null)
        {
            DrawEmptyState(canvas, width, height);
            return;
        }

        if (_viewModel.CanvasScale <= 0)
        {
            FitToView();
        }

        var bitmap = _viewModel.SourceBitmap;
        var scale = GetUiScale();
        var offset = GetImageOffset(scale, _viewModel.PanX, _viewModel.PanY, bitmap.Width, bitmap.Height);

        using var imageShadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(offset.X - 6, offset.Y - 6, (bitmap.Width * scale) + 12, (bitmap.Height * scale) + 12), 18, 18, imageShadow);

        using var bitmapPaint = new SKPaint
        {
            FilterQuality = SKFilterQuality.None,
            IsAntialias = false,
        };

        canvas.Save();
        canvas.Translate(offset.X, offset.Y);
        canvas.Scale(scale);
        canvas.DrawBitmap(bitmap, 0, 0, bitmapPaint);
        DrawCommittedCutouts(canvas);
        DrawActiveSelection(canvas);
        DrawInProgressSelection(canvas);
        canvas.Restore();

        DrawGettingStartedGuide(canvas, offset, scale, bitmap.Width, bitmap.Height);

        DrawHud(canvas, width, height);
    }

    private void DrawBackdrop(SKCanvas canvas, int width, int height)
    {
        using var gradient = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height),
                new[]
                {
                    new SKColor(18, 30, 36),
                    new SKColor(12, 18, 24),
                },
                null,
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(SKRect.Create(width, height), gradient);
    }

    private static void DrawEmptyState(SKCanvas canvas, int width, int height)
    {
        using var heading = new SKPaint
        {
            Color = new SKColor(238, 244, 246),
            IsAntialias = true,
            TextSize = 34,
            FakeBoldText = true,
        };
        using var body = new SKPaint
        {
            Color = new SKColor(166, 185, 193),
            IsAntialias = true,
            TextSize = 18,
        };

        canvas.DrawText("PHOTO CUTTER", 48, Math.Max(72, (height / 2f) - 20), heading);
        canvas.DrawText("Drop an image, or open one from the header to start pulling clean PNG cutouts.", 48, Math.Max(104, height / 2f + 20), body);
    }

    private void DrawGettingStartedGuide(SKCanvas canvas, SKPoint offset, float scale, int imageWidth, int imageHeight)
    {
        if (_viewModel is null ||
            _viewModel.Project.Cutouts.Count > 0 ||
            _viewModel.HasActiveSelection ||
            _freehandPoints.Count > 0 ||
            _polygonPoints.Count > 0 ||
            _isShapeDrawing ||
            _isBrushRefining)
        {
            return;
        }

        var imageRect = SKRect.Create(offset.X, offset.Y, imageWidth * scale, imageHeight * scale);
        if (imageRect.Width < 220 || imageRect.Height < 140)
        {
            return;
        }

        var title = _viewModel.ActiveTool switch
        {
            CanvasTool.Select => "No cutouts yet",
            CanvasTool.Lasso => "Lasso is ready",
            CanvasTool.Polygon => "Polygon is ready",
            CanvasTool.Shape => "Shapes are ready",
            CanvasTool.BrushAdd => "Brush Add is ready",
            CanvasTool.BrushErase => "Brush Erase is ready",
            _ => "Ready to cut",
        };

        var message = _viewModel.ActiveTool switch
        {
            CanvasTool.Select => "Select and move committed cutouts, drag active selections into place, or drag empty space to pan when zoomed in.",
            CanvasTool.Lasso => "Click and drag around what you want to keep, release to finish the shape, then press Commit Cutout.",
            CanvasTool.Polygon => "Click points around the shape, then click the first point again, double-click, or press Enter to close it. Tap Esc to undo the last line, or hold Esc to clear the whole shape.",
            CanvasTool.Shape => "Choose a preset shape, click the center point, then drag outward to size it. Hold Shift for even proportions on square-style shapes.",
            CanvasTool.BrushAdd => "Select a cutout or close an active selection first, then paint to add missed pixels back into the shape.",
            CanvasTool.BrushErase => "Select a cutout or close an active selection first, then paint to trim spill and clean up edges.",
            _ => "Choose a cutout tool and draw around the part you want to keep.",
        };
        var cardWidth = Math.Min(420f, Math.Max(280f, imageRect.Width * 0.5f));
        var cardHeight = 112f;
        var cardX = imageRect.Left + 18f;
        var cardY = Math.Max(imageRect.Top + 18f, imageRect.Bottom - cardHeight - 18f);
        var cardRect = SKRect.Create(cardX, cardY, cardWidth, cardHeight);

        using var card = new SKPaint
        {
            Color = new SKColor(7, 12, 16, 205),
            IsAntialias = true,
        };
        using var border = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(48, 78, 92, 220),
            IsAntialias = true,
        };
        using var accent = new SKPaint
        {
            Color = new SKColor(255, 151, 90),
            TextSize = 16,
            IsAntialias = true,
            FakeBoldText = true,
        };
        using var body = new SKPaint
        {
            Color = new SKColor(223, 234, 238),
            TextSize = 15,
            IsAntialias = true,
        };

        canvas.DrawRoundRect(cardRect, 18, 18, card);
        canvas.DrawRoundRect(cardRect, 18, 18, border);
        canvas.DrawText(title, cardRect.Left + 18, cardRect.Top + 30, accent);

        var lines = WrapText(message, body, cardRect.Width - 36);
        var textY = cardRect.Top + 58;
        var drawnLines = 0;
        foreach (var line in lines)
        {
            canvas.DrawText(line, cardRect.Left + 18, textY, body);
            textY += 21;
            drawnLines++;
            if (drawnLines == 3)
            {
                break;
            }
        }
    }

    private void DrawCommittedCutouts(SKCanvas canvas)
    {
        if (_viewModel is null || !_viewModel.ShowCutoutsOverlay)
        {
            return;
        }

        using var outline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = GetOverlayStrokeWidth(GetUiScale()),
            IsAntialias = true,
            Color = new SKColor(63, 218, 201, 190),
        };
        using var selectedOutline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(GetOverlayStrokeWidth(GetUiScale()) + (0.55f / GetUiScale()), 0.95f / GetUiScale()),
            IsAntialias = true,
            Color = new SKColor(255, 151, 90),
        };

        foreach (var cutout in _viewModel.Project.Cutouts)
        {
            if (!cutout.IsVisible || !GeometryHelper.IsValidGeometry(cutout.Geometry))
            {
                continue;
            }

            using var path = GeometryHelper.BuildPath(cutout.Geometry);
            var paint = _viewModel.SelectedCutouts.Contains(cutout) ? selectedOutline : outline;
            canvas.DrawPath(path, paint);
        }
    }

    private void DrawActiveSelection(SKCanvas canvas)
    {
        if (_viewModel is null || !_viewModel.HasActiveSelection)
        {
            return;
        }

        using var fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Color = new SKColor(60, 205, 255, 55),
        };
        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeWidth = GetOverlayStrokeWidth(GetUiScale()),
            Color = new SKColor(86, 224, 255),
        };

        using var path = GeometryHelper.BuildPath(_viewModel.ActiveSelection.Geometry);
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
    }

    private void DrawInProgressSelection(SKCanvas canvas)
    {
        if (_viewModel is null)
        {
            return;
        }

        var uiScale = GetUiScale();

        if (_viewModel.ActiveTool == CanvasTool.Lasso && _freehandPoints.Count > 1)
        {
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = GetOverlayStrokeWidth(uiScale),
                Color = new SKColor(255, 208, 114),
            };
            using var path = new SKPath();
            path.MoveTo(_freehandPoints[0].X, _freehandPoints[0].Y);
            for (var index = 1; index < _freehandPoints.Count; index++)
            {
                path.LineTo(_freehandPoints[index].X, _freehandPoints[index].Y);
            }

            canvas.DrawPath(path, stroke);
        }

        if (_viewModel.ActiveTool == CanvasTool.Polygon && _polygonPoints.Count > 0)
        {
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = GetOverlayStrokeWidth(uiScale),
                Color = new SKColor(255, 208, 114),
            };
            using var nodeFill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = new SKColor(255, 151, 90),
            };
            using var nodeStroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = Math.Max(0.8f / uiScale, 0.18f),
                Color = new SKColor(255, 245, 230),
            };
            using var anchorStroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = Math.Max(1.1f / uiScale, 0.24f),
                Color = new SKColor(86, 224, 255, 230),
            };
            using var path = new SKPath();
            path.MoveTo(_polygonPoints[0].X, _polygonPoints[0].Y);
            for (var index = 1; index < _polygonPoints.Count; index++)
            {
                path.LineTo(_polygonPoints[index].X, _polygonPoints[index].Y);
            }

            PointF? previewPoint = null;
            if (TryGetDisplayedPolygonHoverPoint(out var hoveredPolygonPoint))
            {
                previewPoint = hoveredPolygonPoint;
                path.LineTo(hoveredPolygonPoint.X, hoveredPolygonPoint.Y);
            }

            canvas.DrawPath(path, stroke);

            var nodeRadius = GetOverlayNodeRadius(uiScale);
            for (var index = 0; index < _polygonPoints.Count; index++)
            {
                var point = _polygonPoints[index];
                var radius = index == 0 ? nodeRadius * 1.18f : nodeRadius;
                canvas.DrawCircle(point.X, point.Y, radius, nodeFill);
                canvas.DrawCircle(point.X, point.Y, radius, index == 0 ? anchorStroke : nodeStroke);
            }

            DrawPolygonSegmentAngleLabels(canvas, previewPoint, uiScale);
        }

        if (_viewModel.ActiveTool == CanvasTool.Shape && _isShapeDrawing && TryBuildShapePreviewGeometry(out var shapePreview))
        {
            using var fill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = new SKColor(86, 224, 255, 48),
            };
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = GetOverlayStrokeWidth(uiScale),
                Color = new SKColor(255, 208, 114),
            };
            using var centerFill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = new SKColor(255, 151, 90),
            };
            using var centerStroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = Math.Max(1f / uiScale, 0.22f),
                Color = new SKColor(255, 245, 230),
            };
            using var path = GeometryHelper.BuildPath(shapePreview);

            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
            var centerRadius = GetOverlayNodeRadius(uiScale) * 0.92f;
            canvas.DrawCircle(_shapeCenterPoint.X, _shapeCenterPoint.Y, centerRadius, centerFill);
            canvas.DrawCircle(_shapeCenterPoint.X, _shapeCenterPoint.Y, centerRadius, centerStroke);
        }

        if (IsBrushTool(_viewModel.ActiveTool) && _hasHoverImagePoint)
        {
            var radius = GetBrushRadiusImageSpace();
            var isAdd = _viewModel.ActiveTool == CanvasTool.BrushAdd;
            var strokeWidth = Math.Max(1.2f / uiScale, 0.22f);

            using var fill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = isAdd ? new SKColor(63, 218, 201, 55) : new SKColor(255, 151, 90, 55),
            };
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = strokeWidth,
                Color = isAdd ? new SKColor(102, 236, 226) : new SKColor(255, 195, 164),
            };

            canvas.DrawCircle(_hoverImagePoint.X, _hoverImagePoint.Y, radius, fill);
            canvas.DrawCircle(_hoverImagePoint.X, _hoverImagePoint.Y, radius, stroke);
        }
    }
    private void DrawHud(SKCanvas canvas, int width, int height)
    {
        if (_viewModel is null || !_viewModel.ShowCanvasHud)
        {
            return;
        }

        using var pill = new SKPaint
        {
            Color = new SKColor(7, 12, 16, 180),
            IsAntialias = true,
        };
        using var text = new SKPaint
        {
            Color = new SKColor(236, 245, 247),
            TextSize = 15,
            IsAntialias = true,
        };
        using var accent = new SKPaint
        {
            Color = new SKColor(255, 151, 90),
            TextSize = 15,
            IsAntialias = true,
            FakeBoldText = true,
        };

        canvas.DrawRoundRect(SKRect.Create(width - 260, 16, 236, 64), 14, 14, pill);
        canvas.DrawText(_viewModel.ZoomText, width - 236, 42, accent);
        canvas.DrawText(_viewModel.HasImage ? $"{_viewModel.Project.Cutouts.Count} cutout(s)" : "No image", width - 236, 64, text);
        if (_hasHoverImagePoint)
        {
            canvas.DrawText($"{_hoverImagePoint.X:0}, {_hoverImagePoint.Y:0}", width - 130, 42, text);
        }

        if (_viewModel.ActiveTool == CanvasTool.Polygon)
        {
            const string hint = "Wheel/+/- zoom. Right-drag pans. Click first point, double-click, or Enter to close. Esc undoes; hold Esc clears.";
            var hintWidth = text.MeasureText(hint) + 28;
            var hintRect = SKRect.Create(18, height - 48, hintWidth, 30);
            canvas.DrawRoundRect(hintRect, 12, 12, pill);
            canvas.DrawText(hint, hintRect.Left + 14, hintRect.MidY + 5, text);
        }
        else if (_viewModel.ActiveTool == CanvasTool.Shape)
        {
            const string hint = "Click a center point, drag outward, and release to finish. Shift keeps square-style shapes even.";
            var hintWidth = text.MeasureText(hint) + 28;
            var hintRect = SKRect.Create(18, height - 48, hintWidth, 30);
            canvas.DrawRoundRect(hintRect, 12, 12, pill);
            canvas.DrawText(hint, hintRect.Left + 14, hintRect.MidY + 5, text);
        }
    }

    private void BeginFreehand(Point position)
    {
        if (!TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        _freehandPoints.Clear();
        _freehandPoints.Add(ClampToImage(imagePoint));
        _isFreehandDrawing = true;
    }

    private void EndFreehand(Point position)
    {
        _isFreehandDrawing = false;
        if (TryScreenToImage(position, out var imagePoint))
        {
            imagePoint = ClampToImage(imagePoint);
            if (_freehandPoints.Count == 0 || Distance(_freehandPoints[^1], imagePoint) >= 1.5f)
            {
                _freehandPoints.Add(imagePoint);
            }
        }

        if (_freehandPoints.Count < 3)
        {
            _freehandPoints.Clear();
            _viewModel!.StatusText = "Selection too small.";
            return;
        }

        _viewModel!.SetActiveSelection(new PathGeometryModel
        {
            Mode = "freehand",
            Closed = true,
            Points = _freehandPoints.Select(point => new PointF(point.X, point.Y)).ToList(),
        });
        _viewModel.StatusText = "Freehand selection ready to commit.";
        _freehandPoints.Clear();
    }

    private void BeginShape(Point position)
    {
        if (_viewModel?.SourceBitmap is null || !TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        _shapeCenterPoint = ClampToImage(imagePoint);
        _shapeEdgePoint = _shapeCenterPoint;
        _isShapeDrawing = true;
        _viewModel.StatusText = $"{_viewModel.SelectedShapePresetLabel} center placed. Drag outward from the center point.";
    }

    private void UpdateShape(Point position)
    {
        if (!_isShapeDrawing || !TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        _shapeEdgePoint = ClampToImage(imagePoint);
    }

    private void EndShape(Point position)
    {
        if (!_isShapeDrawing || _viewModel is null)
        {
            return;
        }

        if (TryScreenToImage(position, out var imagePoint))
        {
            _shapeEdgePoint = ClampToImage(imagePoint);
        }

        if (!TryBuildShapePreviewGeometry(out var geometry))
        {
            _isShapeDrawing = false;
            _viewModel.StatusText = "Shape selection too small.";
            return;
        }

        var bbox = GeometryHelper.ComputeBBox(geometry.Points);
        if (bbox.W < 2 || bbox.H < 2)
        {
            _isShapeDrawing = false;
            _viewModel.StatusText = "Shape selection too small.";
            return;
        }

        _isShapeDrawing = false;
        _viewModel.SetActiveSelection(geometry);
        _viewModel.StatusText = $"{_viewModel.SelectedShapePresetLabel} selection ready to commit.";
    }

    private bool TryBuildShapePreviewGeometry(out PathGeometryModel geometry)
    {
        geometry = new PathGeometryModel();
        if (_viewModel?.SourceBitmap is null || !_isShapeDrawing)
        {
            return false;
        }

        geometry = BuildShapeGeometry(_shapeCenterPoint, _shapeEdgePoint);
        return GeometryHelper.IsValidGeometry(geometry);
    }

    private PathGeometryModel BuildShapeGeometry(PointF center, PointF edge)
    {
        if (_viewModel is null)
        {
            return new PathGeometryModel();
        }

        var radiusX = MathF.Abs(edge.X - center.X);
        var radiusY = MathF.Abs(edge.Y - center.Y);
        if (_currentModifiers.HasFlag(ModifierKeys.Shift))
        {
            var uniformRadius = MathF.Max(radiusX, radiusY);
            radiusX = uniformRadius;
            radiusY = uniformRadius;
        }

        if (MathF.Max(radiusX, radiusY) < 2f)
        {
            return new PathGeometryModel();
        }

        var points = BuildShapePoints(_viewModel.SelectedShapePreset, center, radiusX, radiusY);
        return new PathGeometryModel
        {
            Type = "path",
            Mode = GetShapeMode(_viewModel.SelectedShapePreset),
            Closed = true,
            Points = points,
        };
    }

    private List<PointF> BuildShapePoints(ShapeCutoutPreset preset, PointF center, float radiusX, float radiusY)
    {
        return preset switch
        {
            ShapeCutoutPreset.Rectangle => NormalizeShapePoints(new List<PointF>
            {
                new(center.X - radiusX, center.Y - radiusY),
                new(center.X + radiusX, center.Y - radiusY),
                new(center.X + radiusX, center.Y + radiusY),
                new(center.X - radiusX, center.Y + radiusY),
            }),
            ShapeCutoutPreset.RoundedRectangle => BuildRoundedRectanglePoints(center, radiusX, radiusY, MathF.Max(4f, MathF.Min(radiusX, radiusY) * 0.38f)),
            ShapeCutoutPreset.Circle => BuildEllipsePoints(center, MathF.Max(radiusX, radiusY), MathF.Max(radiusX, radiusY), 44),
            ShapeCutoutPreset.Ellipse => BuildEllipsePoints(center, radiusX, radiusY, 44),
            ShapeCutoutPreset.Diamond => NormalizeShapePoints(new List<PointF>
            {
                new(center.X, center.Y - radiusY),
                new(center.X + radiusX, center.Y),
                new(center.X, center.Y + radiusY),
                new(center.X - radiusX, center.Y),
            }),
            ShapeCutoutPreset.Triangle => NormalizeShapePoints(new List<PointF>
            {
                new(center.X, center.Y - radiusY),
                new(center.X + radiusX, center.Y + radiusY),
                new(center.X - radiusX, center.Y + radiusY),
            }),
            ShapeCutoutPreset.Hexagon => BuildRegularPolygonPoints(center, radiusX, radiusY, 6, -90f),
            ShapeCutoutPreset.Octagon => BuildRegularPolygonPoints(center, radiusX, radiusY, 8, -90f),
            ShapeCutoutPreset.Capsule => BuildRoundedRectanglePoints(center, radiusX, radiusY, MathF.Min(radiusX, radiusY)),
            ShapeCutoutPreset.Star => BuildStarPoints(center, radiusX, radiusY),
            _ => BuildEllipsePoints(center, radiusX, radiusY, 32),
        };
    }

    private List<PointF> BuildEllipsePoints(PointF center, float radiusX, float radiusY, int segments)
    {
        var points = new List<PointF>(segments);
        for (var index = 0; index < segments; index++)
        {
            var angle = (index / (float)segments) * MathF.PI * 2f;
            points.Add(new PointF(
                center.X + (MathF.Cos(angle) * radiusX),
                center.Y + (MathF.Sin(angle) * radiusY)));
        }

        return NormalizeShapePoints(points);
    }

    private List<PointF> BuildRoundedRectanglePoints(PointF center, float radiusX, float radiusY, float cornerRadius)
    {
        var left = center.X - radiusX;
        var right = center.X + radiusX;
        var top = center.Y - radiusY;
        var bottom = center.Y + radiusY;
        var corner = MathF.Min(cornerRadius, MathF.Min(radiusX, radiusY));
        if (corner <= 1f)
        {
            return NormalizeShapePoints(new List<PointF>
            {
                new(left, top),
                new(right, top),
                new(right, bottom),
                new(left, bottom),
            });
        }

        var points = new List<PointF>();
        AddArcPoints(points, right - corner, top + corner, corner, corner, -90f, 0f, 5);
        AddArcPoints(points, right - corner, bottom - corner, corner, corner, 0f, 90f, 5);
        AddArcPoints(points, left + corner, bottom - corner, corner, corner, 90f, 180f, 5);
        AddArcPoints(points, left + corner, top + corner, corner, corner, 180f, 270f, 5);
        return NormalizeShapePoints(points);
    }

    private static void AddArcPoints(List<PointF> points, float centerX, float centerY, float radiusX, float radiusY, float startDegrees, float endDegrees, int segments)
    {
        for (var index = 0; index <= segments; index++)
        {
            var progress = index / (float)segments;
            var angle = (startDegrees + ((endDegrees - startDegrees) * progress)) * (MathF.PI / 180f);
            points.Add(new PointF(
                centerX + (MathF.Cos(angle) * radiusX),
                centerY + (MathF.Sin(angle) * radiusY)));
        }
    }

    private List<PointF> BuildRegularPolygonPoints(PointF center, float radiusX, float radiusY, int sides, float startAngleDegrees)
    {
        var points = new List<PointF>(sides);
        for (var index = 0; index < sides; index++)
        {
            var angle = (startAngleDegrees + ((360f / sides) * index)) * (MathF.PI / 180f);
            points.Add(new PointF(
                center.X + (MathF.Cos(angle) * radiusX),
                center.Y + (MathF.Sin(angle) * radiusY)));
        }

        return NormalizeShapePoints(points);
    }

    private List<PointF> BuildStarPoints(PointF center, float radiusX, float radiusY)
    {
        var outerRadiusX = MathF.Max(radiusX, 2f);
        var outerRadiusY = MathF.Max(radiusY, 2f);
        var innerRadiusX = outerRadiusX * 0.46f;
        var innerRadiusY = outerRadiusY * 0.46f;
        var points = new List<PointF>(10);
        for (var index = 0; index < 10; index++)
        {
            var angle = (-90f + (36f * index)) * (MathF.PI / 180f);
            var useInner = index % 2 == 1;
            points.Add(new PointF(
                center.X + (MathF.Cos(angle) * (useInner ? innerRadiusX : outerRadiusX)),
                center.Y + (MathF.Sin(angle) * (useInner ? innerRadiusY : outerRadiusY))));
        }

        return NormalizeShapePoints(points);
    }

    private List<PointF> NormalizeShapePoints(IEnumerable<PointF> points)
    {
        var normalized = new List<PointF>();
        foreach (var point in points)
        {
            var clamped = ClampToImage(point);
            if (normalized.Count == 0 || Distance(normalized[^1], clamped) >= 0.75f)
            {
                normalized.Add(clamped);
            }
        }

        if (normalized.Count > 1 && Distance(normalized[0], normalized[^1]) < 0.75f)
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        return normalized;
    }

    private static string GetShapeMode(ShapeCutoutPreset preset)
    {
        return preset switch
        {
            ShapeCutoutPreset.Rectangle => "shape-rectangle",
            ShapeCutoutPreset.RoundedRectangle => "shape-rounded-rectangle",
            ShapeCutoutPreset.Circle => "shape-circle",
            ShapeCutoutPreset.Ellipse => "shape-ellipse",
            ShapeCutoutPreset.Diamond => "shape-diamond",
            ShapeCutoutPreset.Triangle => "shape-triangle",
            ShapeCutoutPreset.Hexagon => "shape-hexagon",
            ShapeCutoutPreset.Octagon => "shape-octagon",
            ShapeCutoutPreset.Capsule => "shape-capsule",
            ShapeCutoutPreset.Star => "shape-star",
            _ => "shape",
        };
    }

    private void BeginBrushRefine(Point position, bool addMode)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        if (!TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        imagePoint = ClampToImage(imagePoint);
        if (!TryPrepareBrushTarget(imagePoint))
        {
            _viewModel.StatusText = "Select a cutout or close an active selection first, then use Brush + or Brush -.";
            return;
        }

        _brushAddMode = addMode;
        _isBrushRefining = _viewModel.BeginBrushRefineStroke(imagePoint, GetBrushRadiusImageSpace(), addMode);
        if (_isBrushRefining)
        {
            _viewModel.StatusText = _brushAddMode
                ? "Brush + active. Paint across the edge to add pixels back into the shape."
                : "Brush - active. Paint across the edge to trim pixels away.";
        }
    }

    private void ContinueBrushRefine(Point position)
    {
        if (!_isBrushRefining || _viewModel?.SourceBitmap is null)
        {
            return;
        }

        if (!TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        imagePoint = ClampToImage(imagePoint);
        _viewModel.ContinueBrushRefineStroke(imagePoint, GetBrushRadiusImageSpace(), _brushAddMode);
    }

    private void EndBrushRefine()
    {
        if (!_isBrushRefining || _viewModel is null)
        {
            return;
        }

        _viewModel.EndBrushRefineStroke(_brushAddMode);
        _isBrushRefining = false;
    }

    private float GetBrushRadiusImageSpace()
    {
        if (_viewModel is null)
        {
            return 1f;
        }

        var scale = GetUiScale();
        var diameter = Math.Clamp(_viewModel.RefineBrushSize, 6, 220);
        return Math.Max(1f, (float)(diameter / (2f * scale)));
    }

    private static bool IsBrushTool(CanvasTool tool)
    {
        return tool is CanvasTool.BrushAdd or CanvasTool.BrushErase;
    }

    private void AddPolygonPoint(Point position, int clickCount)
    {
        if (!TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        imagePoint = ClampToImage(imagePoint);
        if (_polygonPoints.Count > 0 && IsPolygonAngleSnapActive())
        {
            imagePoint = ClampToImage(SnapPolygonPoint(_polygonPoints[^1], imagePoint, PolygonSnapStepDegrees));
        }

        if (_polygonPoints.Count >= 3)
        {
            var closeDistance = Math.Max(8f / GetUiScale(), 3.5f);
            if (Distance(_polygonPoints[0], imagePoint) <= closeDistance)
            {
                _viewModel!.StatusText = "Polygon closed. Ready to commit.";
                FinalizePolygon();
                return;
            }
        }

        if (_polygonPoints.Count == 0 || Distance(_polygonPoints[^1], imagePoint) >= Math.Max(0.75f, 1.2f / GetUiScale()))
        {
            _polygonPoints.Add(imagePoint);
            _viewModel!.StatusText = _polygonPoints.Count == 1
                ? "Polygon anchor placed. Click the first point again, double-click, or press Enter to close. Esc removes the last line, and holding Esc clears the full shape."
                : $"Polygon point {_polygonPoints.Count} placed. Click the first point, double-click, or press Enter to close. Esc removes the last line.";
        }

        if (clickCount > 1)
        {
            FinalizePolygon();
        }
    }

    public bool TryFinalizePendingPolygon()
    {
        if (_viewModel?.ActiveTool != CanvasTool.Polygon)
        {
            return false;
        }

        if (_polygonPoints.Count >= 3)
        {
            return FinalizePolygon();
        }

        _viewModel.StatusText = _polygonPoints.Count > 0
            ? "Add at least 3 polygon points before committing the cutout."
            : "Click to place polygon points, then close the shape to commit it.";
        return true;
    }

    public bool UndoPendingPolygonPoint()
    {
        if (_viewModel?.ActiveTool != CanvasTool.Polygon || _polygonPoints.Count == 0)
        {
            return false;
        }

        _polygonPoints.RemoveAt(_polygonPoints.Count - 1);
        _viewModel.StatusText = _polygonPoints.Count > 0
            ? $"Removed last polygon point. {_polygonPoints.Count} point(s) left."
            : "Polygon cleared.";
        return true;
    }

    public bool ClearPendingPolygon()
    {
        if (_viewModel?.ActiveTool != CanvasTool.Polygon || _polygonPoints.Count == 0)
        {
            return false;
        }

        _polygonPoints.Clear();
        _viewModel.StatusText = "Polygon cleared.";
        return true;
    }

    private bool FinalizePolygon()
    {
        if (_viewModel is null)
        {
            return false;
        }

        if (_polygonPoints.Count < 3)
        {
            _polygonPoints.Clear();
            _viewModel.StatusText = "Selection too small.";
            return true;
        }

        _viewModel.SetActiveSelection(new PathGeometryModel
        {
            Mode = "polygon",
            Closed = true,
            Points = _polygonPoints.Select(point => new PointF(point.X, point.Y)).ToList(),
        });
        _polygonPoints.Clear();
        _viewModel.StatusText = "Polygon selection ready to commit.";
        return true;
    }

    private bool TryGetDisplayedPolygonHoverPoint(out PointF point)
    {
        point = default;
        if (!_hasHoverImagePoint)
        {
            return false;
        }

        point = _hoverImagePoint;
        if (_polygonPoints.Count > 0 && IsPolygonAngleSnapActive())
        {
            point = ClampToImage(SnapPolygonPoint(_polygonPoints[^1], point, PolygonSnapStepDegrees));
        }

        return true;
    }

    private bool IsPolygonAngleSnapActive()
    {
        return _viewModel?.ActiveTool == CanvasTool.Polygon && _currentModifiers.HasFlag(ModifierKeys.Shift);
    }

    private void DrawPolygonSegmentAngleLabels(SKCanvas canvas, PointF? previewPoint, float uiScale)
    {
        using var committedBackground = new SKPaint
        {
            Color = new SKColor(8, 14, 18, 190),
            IsAntialias = true,
        };
        using var previewBackground = new SKPaint
        {
            Color = IsPolygonAngleSnapActive() ? new SKColor(33, 65, 72, 205) : new SKColor(40, 32, 24, 190),
            IsAntialias = true,
        };
        using var committedText = new SKPaint
        {
            Color = new SKColor(242, 247, 249),
            IsAntialias = true,
            FakeBoldText = true,
        };
        using var previewText = new SKPaint
        {
            Color = IsPolygonAngleSnapActive() ? new SKColor(145, 238, 229) : new SKColor(255, 220, 176),
            IsAntialias = true,
            FakeBoldText = true,
        };

        for (var index = 1; index < _polygonPoints.Count; index++)
        {
            DrawSegmentAngleLabel(canvas, _polygonPoints[index - 1], _polygonPoints[index], uiScale, committedBackground, committedText);
        }

        if (previewPoint is not null && _polygonPoints.Count > 0)
        {
            DrawSegmentAngleLabel(canvas, _polygonPoints[^1], previewPoint.Value, uiScale, previewBackground, previewText);
        }
    }

    private static void DrawSegmentAngleLabel(SKCanvas canvas, PointF start, PointF end, float uiScale, SKPaint backgroundPaint, SKPaint textPaint)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var segmentLength = MathF.Sqrt((dx * dx) + (dy * dy));
        if ((segmentLength * uiScale) < MinPolygonLabelScreenLength)
        {
            return;
        }

        var textSize = Math.Clamp(11f / uiScale, 4f, 14f);
        textPaint.TextSize = textSize;

        var label = $"{NormalizeSegmentAngleDegrees(MathF.Atan2(dy, dx) * 180f / MathF.PI):0} deg";
        var textWidth = textPaint.MeasureText(label);
        var paddingX = Math.Max(4f / uiScale, 1.1f);
        var paddingY = Math.Max(3f / uiScale, 0.9f);
        var midX = (start.X + end.X) * 0.5f;
        var midY = (start.Y + end.Y) * 0.5f;
        var normalX = -dy / segmentLength;
        var normalY = dx / segmentLength;
        var offset = Math.Max(11f / uiScale, 2.2f);
        var labelCenterX = midX + (normalX * offset);
        var labelCenterY = midY + (normalY * offset);
        var rect = SKRect.Create(
            labelCenterX - ((textWidth * 0.5f) + paddingX),
            labelCenterY - (textSize + paddingY),
            textWidth + (paddingX * 2f),
            textSize + (paddingY * 2f));

        canvas.DrawRoundRect(rect, Math.Max(5f / uiScale, 1.2f), Math.Max(5f / uiScale, 1.2f), backgroundPaint);
        canvas.DrawText(label, rect.Left + paddingX, rect.Bottom - paddingY, textPaint);
    }

    private static float NormalizeSegmentAngleDegrees(float angle)
    {
        var normalized = angle % 180f;
        if (normalized < 0f)
        {
            normalized += 180f;
        }

        return normalized;
    }

    private static PointF SnapPolygonPoint(PointF origin, PointF point, float stepDegrees)
    {
        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;
        var distance = MathF.Sqrt((dx * dx) + (dy * dy));
        if (distance <= 0.001f)
        {
            return point;
        }

        var stepRadians = stepDegrees * (MathF.PI / 180f);
        var angle = MathF.Atan2(dy, dx);
        var snappedAngle = MathF.Round(angle / stepRadians) * stepRadians;
        return new PointF(
            origin.X + (MathF.Cos(snappedAngle) * distance),
            origin.Y + (MathF.Sin(snappedAngle) * distance));
    }

    public void OnPanStart(Point position, ModifierKeys modifiers)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        _currentModifiers = modifiers;
        BeginPan(position);
        _viewModel.StatusText = "Panning zoomed canvas.";
    }

    public void OnPanEnd()
    {
        _isPanning = false;
        _isSelectPanCandidate = false;
        ResetActiveSelectionMove();
    }

    private void BeginPan(Point position)
    {
        _isPanning = true;
        _isSelectPanCandidate = false;
        _lastPointer = position;
    }

    private bool TryBeginActiveSelectionMove(Point position)
    {
        if (_viewModel?.SourceBitmap is null || !_viewModel.HasActiveSelection || !TryScreenToImage(position, out var imagePoint))
        {
            return false;
        }

        using var path = GeometryHelper.BuildPath(_viewModel.ActiveSelection.Geometry);
        if (!path.Contains(imagePoint.X, imagePoint.Y))
        {
            return false;
        }

        _isActiveSelectionMoveCandidate = true;
        _isMovingActiveSelection = false;
        _activeSelectionMoveChanged = false;
        _activeSelectionMoveStartScreen = position;
        _activeSelectionMoveStartImagePoint = ClampToImage(imagePoint);
        _activeSelectionMoveOriginGeometry = _viewModel.ActiveSelection.Geometry.DeepClone();
        return true;
    }

    private void ResetActiveSelectionMove()
    {
        _isActiveSelectionMoveCandidate = false;
        _isMovingActiveSelection = false;
        _activeSelectionMoveChanged = false;
        _activeSelectionMoveOriginGeometry = null;
    }
    private bool CanDirectPanOnSelect()
    {
        if (_viewModel?.SourceBitmap is null || _viewModel.ActiveTool != CanvasTool.Select)
        {
            return false;
        }

        var fitScale = ComputeFitScale(_viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        return GetUiScale() > fitScale + 0.01f;
    }

    private bool TryPrepareBrushTarget(PointF imagePoint)
    {
        if (_viewModel is null)
        {
            return false;
        }

        if (_viewModel.HasActiveSelection && GeometryHelper.IsValidGeometry(_viewModel.ActiveSelection.Geometry))
        {
            using var activePath = GeometryHelper.BuildPath(_viewModel.ActiveSelection.Geometry);
            if (activePath.Contains(imagePoint.X, imagePoint.Y))
            {
                return true;
            }
        }

        if (TryHitVisibleCutout(imagePoint, out var hitCutout))
        {
            if (!ReferenceEquals(_viewModel.PrimarySelectedCutout, hitCutout))
            {
                _viewModel.SelectSingleCutout(hitCutout);
            }

            return true;
        }

        return _viewModel.HasSelectedCutout || _viewModel.HasActiveSelection;
    }

    private bool TryHitVisibleCutout(PointF imagePoint, out CutoutModel? hitCutout)
    {
        hitCutout = null;
        if (_viewModel is null)
        {
            return false;
        }

        for (var index = _viewModel.Project.Cutouts.Count - 1; index >= 0; index--)
        {
            var cutout = _viewModel.Project.Cutouts[index];
            if (!cutout.IsVisible || !GeometryHelper.IsValidGeometry(cutout.Geometry))
            {
                continue;
            }

            using var path = GeometryHelper.BuildPath(cutout.Geometry);
            if (path.Contains(imagePoint.X, imagePoint.Y))
            {
                hitCutout = cutout;
                return true;
            }
        }

        return false;
    }

    private void SelectAt(Point position)
    {
        if (_viewModel?.SourceBitmap is null || !TryScreenToImage(position, out var imagePoint))
        {
            return;
        }

        if (TryHitVisibleCutout(imagePoint, out var hitCutout) && hitCutout is not null)
        {
            _viewModel.SelectSingleCutout(hitCutout);
            _viewModel.StatusText = $"Selected {hitCutout.Name}.";
            return;
        }

        _viewModel.SelectSingleCutout(null);
    }

    private bool TryScreenToImage(Point position, out PointF point)
    {
        point = default;
        if (_viewModel?.SourceBitmap is null || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return false;
        }

        var scale = GetUiScale();
        var offset = GetImageOffset(scale, _viewModel.PanX, _viewModel.PanY, _viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        var imageX = (float)((position.X - offset.X) / scale);
        var imageY = (float)((position.Y - offset.Y) / scale);
        point = new PointF(imageX, imageY);
        return true;
    }

    private PointF ClampToImage(PointF point)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return point;
        }

        return new PointF(
            Math.Clamp(point.X, 0, _viewModel.SourceBitmap.Width - 1),
            Math.Clamp(point.Y, 0, _viewModel.SourceBitmap.Height - 1));
    }

    private float ComputeFitScale(int imageWidth, int imageHeight)
    {
        if (_viewportWidth <= 0 || _viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return 1;
        }

        var widthScale = (_viewportWidth - 32) / imageWidth;
        var heightScale = (_viewportHeight - 32) / imageHeight;
        return Math.Clamp(Math.Min(widthScale, heightScale), MinScale, MaxScale);
    }

    private void ZoomAtViewportCenter(float scaleFactor)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        if (!TryScreenToImage(new Point(_viewportWidth * 0.5f, _viewportHeight * 0.5f), out var focusPoint))
        {
            focusPoint = new PointF(_viewModel.SourceBitmap.Width * 0.5f, _viewModel.SourceBitmap.Height * 0.5f);
        }

        ZoomAtImagePoint(focusPoint, scaleFactor);
    }

    private void ZoomAtImagePoint(PointF focusPoint, float scaleFactor)
    {
        if (_viewModel?.SourceBitmap is null)
        {
            return;
        }

        var currentScale = GetUiScale();
        var newScale = Math.Clamp(currentScale * scaleFactor, MinScale, MaxScale);
        var offsetBefore = GetImageOffset(currentScale, _viewModel.PanX, _viewModel.PanY, _viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        var screenX = offsetBefore.X + (focusPoint.X * currentScale);
        var screenY = offsetBefore.Y + (focusPoint.Y * currentScale);

        var offsetAfter = GetCenteredOffset(newScale, _viewModel.SourceBitmap.Width, _viewModel.SourceBitmap.Height);
        var newPanX = screenX - offsetAfter.X - (focusPoint.X * newScale);
        var newPanY = screenY - offsetAfter.Y - (focusPoint.Y * newScale);

        _viewModel.SetCanvasView(newScale, newPanX, newPanY);
    }

    private float GetUiScale()
    {
        return (float)Math.Max(_viewModel?.CanvasScale ?? 1, MinScale);
    }

    private static float GetOverlayStrokeWidth(float uiScale)
    {
        return Math.Clamp(2.2f / uiScale, 0.65f, 2.5f);
    }

    private static float GetOverlayNodeRadius(float uiScale)
    {
        return Math.Clamp(4.6f / uiScale, 1.5f, 4.8f);
    }
    private SKPoint GetImageOffset(float scale, double panX, double panY, int imageWidth, int imageHeight)
    {
        var centered = GetCenteredOffset(scale, imageWidth, imageHeight);
        return new SKPoint(centered.X + (float)panX, centered.Y + (float)panY);
    }

    private SKPoint GetCenteredOffset(float scale, int imageWidth, int imageHeight)
    {
        var imageWidthPx = imageWidth * scale;
        var imageHeightPx = imageHeight * scale;
        return new SKPoint((_viewportWidth - imageWidthPx) / 2f, (_viewportHeight - imageHeightPx) / 2f);
    }

    private static float Distance(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static IReadOnlyList<string> WrapText(string text, SKPaint paint, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        var currentLine = words[0];
        for (var index = 1; index < words.Length; index++)
        {
            var candidate = $"{currentLine} {words[index]}";
            if (paint.MeasureText(candidate) <= maxWidth)
            {
                currentLine = candidate;
                continue;
            }

            lines.Add(currentLine);
            currentLine = words[index];
        }

        lines.Add(currentLine);
        return lines;
    }
}













































