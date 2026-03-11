using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed partial class AutoCutoutService
{
    public sealed class DetectionOptions
    {
        public float Strength { get; init; } = 0.65f;

        public int MaxResults { get; init; } = 18;

        public bool DetectSections { get; init; } = true;
    }

    public sealed class AutoCutoutSuggestion
    {
        public required PathGeometryModel Geometry { get; init; }

        public required BBox Bounds { get; init; }

        public float Confidence { get; init; }

        public string Kind { get; init; } = "object";

        public string? Label { get; init; }
    }

    public IReadOnlyList<AutoCutoutSuggestion> Detect(SKBitmap bitmap, DetectionOptions? options = null)
    {
        options ??= new DetectionOptions();
        var strength = Math.Clamp(options.Strength, 0.0f, 1.0f);
        var maxResults = Math.Clamp(options.MaxResults, 1, 64);

        if (bitmap.Width < 16 || bitmap.Height < 16)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var width = bitmap.Width;
        var height = bitmap.Height;
        var imageArea = width * height;

        var mask = BuildForegroundMask(bitmap, strength);
        if (!mask.Any(value => value))
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var smoothIterations = strength > 0.7f ? 1 : 2;
        mask = SmoothMask(mask, width, height, smoothIterations);

        var components = ExtractComponents(mask, width, height);
        if (components.Count == 0)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var minArea = Math.Max(360, imageArea / 900);
        var maxArea = (int)(imageArea * 0.93f);
        var minSide = Math.Max(12, Math.Min(width, height) / 34);

        var suggestions = new List<AutoCutoutSuggestion>();
        foreach (var component in components)
        {
            if (component.Area < minArea || component.Area > maxArea)
            {
                continue;
            }

            if (component.Bounds.W < minSide || component.Bounds.H < minSide)
            {
                continue;
            }

            var bboxArea = Math.Max(1, component.Bounds.W * component.Bounds.H);
            var fillRatio = component.Area / (float)bboxArea;
            if (fillRatio < 0.1f)
            {
                continue;
            }

            var aspect = component.Bounds.W > component.Bounds.H
                ? component.Bounds.W / (float)Math.Max(1, component.Bounds.H)
                : component.Bounds.H / (float)Math.Max(1, component.Bounds.W);
            if (aspect > 12f)
            {
                continue;
            }

            var objectGeometry = BuildGeometryFromBoundary(component.BoundaryPoints, component.Bounds, mode: "auto", maxPoints: 56);
            if (objectGeometry is null)
            {
                continue;
            }

            var sectionSuggestions = options.DetectSections
                ? DetectSectionSuggestions(bitmap, mask, component, strength)
                : Array.Empty<AutoCutoutSuggestion>();

            var preferSections = sectionSuggestions.Count >= 2 && component.Bounds.W >= component.Bounds.H * 2;
            if (!preferSections)
            {
                var objectConfidence = ComputeObjectConfidence(component, fillRatio, imageArea, strength);
                suggestions.Add(new AutoCutoutSuggestion
                {
                    Kind = "object",
                    Confidence = objectConfidence,
                    Bounds = component.Bounds,
                    Geometry = objectGeometry,
                });
            }

            suggestions.AddRange(sectionSuggestions);
        }

        var deduped = DeduplicateSuggestions(suggestions);
        return deduped
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Bounds.W * item.Bounds.H)
            .Take(maxResults)
            .ToList();
    }

    private static IReadOnlyList<AutoCutoutSuggestion> DetectSectionSuggestions(SKBitmap bitmap, bool[] mask, MaskComponent component, float strength)
    {
        var bounds = component.Bounds;
        var aspect = bounds.W / (float)Math.Max(1, bounds.H);
        if (bounds.W < 180 || bounds.H < 28 || aspect < 1.6f)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var energy = Smooth1D(ComputeVerticalBoundaryEnergy(bitmap, mask, bounds), radius: 2);
        if (energy.Length < 8)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var persistenceThreshold = 20f + (30f * (1f - strength));
        var persistence = Smooth1D(ComputeBoundaryPersistence(bitmap, mask, bounds, persistenceThreshold), radius: 1);

        var maxEnergy = Math.Max(0.001f, energy.Max());
        var score = new float[energy.Length];
        for (var x = 0; x < score.Length; x++)
        {
            var normalizedEnergy = energy[x] / maxEnergy;
            score[x] = Math.Clamp((normalizedEnergy * 0.62f) + (persistence[x] * 0.38f), 0f, 1f);
        }

        var meanScore = score.Average();
        var variance = score.Select(value => (value - meanScore) * (value - meanScore)).Average();
        var stdDev = (float)Math.Sqrt(Math.Max(0, variance));
        var scoreThreshold = (float)(meanScore + (stdDev * (0.33f - (0.12f * strength))));

        var peaks = new List<int>();
        for (var x = 1; x < score.Length - 1; x++)
        {
            if (score[x] < scoreThreshold || persistence[x] < 0.26f)
            {
                continue;
            }

            if (score[x] >= score[x - 1] && score[x] >= score[x + 1])
            {
                peaks.Add(x);
            }
        }

        if (peaks.Count < 2)
        {
            var fallbackPeaks = Enumerable.Range(1, score.Length - 2)
                .Where(index => persistence[index] >= 0.18f)
                .OrderByDescending(index => score[index])
                .Take(24);

            peaks.AddRange(fallbackPeaks);
        }

        var estimatedSegments = Math.Clamp((int)Math.Round(bounds.W / Math.Max(1f, bounds.H * (1.10f - (0.14f * strength)))), 2, 10);
        var minGap = Math.Max(14, (int)Math.Round(bounds.W / Math.Max(2f, estimatedSegments * 2.8f)));

        var boundaryOffsets = SelectSectionBoundaries(peaks, score, bounds.W, estimatedSegments, minGap);
        if (boundaryOffsets.Count == 0 && aspect >= 2.6f)
        {
            boundaryOffsets = CollapseNearOffsets(
                Enumerable.Range(1, estimatedSegments - 1)
                    .Select(segment => (int)Math.Round((segment * bounds.W) / (float)estimatedSegments))
                    .ToList(),
                bounds.W,
                minGap);
        }

        if (boundaryOffsets.Count == 0)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var splitXs = new List<int> { bounds.X };
        splitXs.AddRange(boundaryOffsets.Select(offset => bounds.X + offset));
        splitXs.Add(bounds.Right);
        splitXs = splitXs.Distinct().OrderBy(value => value).ToList();

        if (splitXs.Count < 3)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        var minSegmentWidth = Math.Max(24, bounds.W / 22);
        var maxSegments = Math.Clamp((int)Math.Round(2 + (strength * 9)), 3, 12);
        var sections = new List<AutoCutoutSuggestion>();

        for (var index = 0; index < splitXs.Count - 1; index++)
        {
            var segmentLeft = splitXs[index];
            var segmentRight = splitXs[index + 1];
            if (segmentRight - segmentLeft < minSegmentWidth)
            {
                continue;
            }

            if (!TryExtractSegmentShape(mask, bitmap.Width, bounds, segmentLeft, segmentRight, out var tightBounds, out var segmentArea, out var boundaryPoints))
            {
                continue;
            }

            var segmentBoxArea = Math.Max(1, tightBounds.W * tightBounds.H);
            var fillRatio = segmentArea / (float)segmentBoxArea;
            if (fillRatio < 0.14f)
            {
                continue;
            }

            var areaFloor = Math.Max(120, bounds.W * bounds.H / 140);
            if (segmentArea < areaFloor)
            {
                continue;
            }

            var geometry = BuildGeometryFromBoundary(boundaryPoints, tightBounds, mode: "auto-section", maxPoints: 42)
                ?? BuildRectGeometry(tightBounds, mode: "auto-section");

            var localIndexLeft = Math.Clamp(segmentLeft - bounds.X, 0, score.Length - 1);
            var localIndexRight = Math.Clamp(segmentRight - bounds.X - 1, 0, score.Length - 1);
            var boundaryScore = MathF.Max(score[localIndexLeft], score[localIndexRight]);
            var persistenceScore = MathF.Max(persistence[localIndexLeft], persistence[localIndexRight]);
            var confidence = Math.Clamp(0.30f + (0.34f * boundaryScore) + (0.20f * fillRatio) + (0.10f * persistenceScore) + (0.08f * strength), 0.12f, 0.95f);

            sections.Add(new AutoCutoutSuggestion
            {
                Kind = "section",
                Confidence = confidence,
                Bounds = tightBounds,
                Geometry = geometry,
            });

            if (sections.Count >= maxSegments)
            {
                break;
            }
        }

        if (sections.Count < 2)
        {
            return Array.Empty<AutoCutoutSuggestion>();
        }

        return sections;
    }

    private static List<int> SelectSectionBoundaries(IReadOnlyList<int> peaks, IReadOnlyList<float> score, int width, int estimatedSegments, int minGap)
    {
        if (peaks.Count == 0 || estimatedSegments <= 1)
        {
            return new List<int>();
        }

        var rankedPeaks = peaks
            .Where(index => index > 1 && index < width - 1)
            .Distinct()
            .OrderByDescending(index => score[index])
            .ToList();

        if (rankedPeaks.Count == 0)
        {
            return new List<int>();
        }

        var searchRadius = Math.Max(10, (int)Math.Round(width / Math.Max(4f, estimatedSegments * 5f)));
        var boundaryOffsets = new List<int>();

        for (var segment = 1; segment < estimatedSegments; segment++)
        {
            var ideal = (int)Math.Round((segment * width) / (float)estimatedSegments);
            int? bestIndex = null;
            var bestScore = float.MinValue;

            foreach (var candidate in rankedPeaks)
            {
                var distance = Math.Abs(candidate - ideal);
                if (distance > searchRadius)
                {
                    continue;
                }

                var candidateScore = score[candidate] - (distance / (float)Math.Max(1, searchRadius) * 0.18f);
                if (candidateScore > bestScore)
                {
                    bestScore = candidateScore;
                    bestIndex = candidate;
                }
            }

            boundaryOffsets.Add(bestIndex ?? ideal);
        }

        return CollapseNearOffsets(boundaryOffsets, width, minGap);
    }

    private static List<int> CollapseNearOffsets(IReadOnlyList<int> offsets, int width, int minGap)
    {
        var accepted = new List<int>();
        foreach (var offset in offsets
                     .Where(value => value > 1 && value < width - 1)
                     .Distinct()
                     .OrderBy(value => value))
        {
            if (accepted.Count == 0 || offset - accepted[^1] >= minGap)
            {
                accepted.Add(offset);
            }
        }

        return accepted;
    }

    private static BBox ExpandBounds(BBox candidate, int padX, int padY, BBox clip)
    {
        var left = Math.Clamp(candidate.X - padX, clip.X, clip.Right - 1);
        var top = Math.Clamp(candidate.Y - padY, clip.Y, clip.Bottom - 1);
        var right = Math.Clamp(candidate.Right + padX, left + 1, clip.Right);
        var bottom = Math.Clamp(candidate.Bottom + padY, top + 1, clip.Bottom);

        return new BBox(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static PathGeometryModel? BuildGeometryFromBoundary(IReadOnlyList<IntPoint> boundaryPoints, BBox bounds, string mode, int maxPoints)
    {
        if (boundaryPoints.Count < 3)
        {
            return null;
        }

        var hull = BuildConvexHull(boundaryPoints);
        if (hull.Count < 3)
        {
            return null;
        }

        var minDistance = Math.Max(1.5f, Math.Min(bounds.W, bounds.H) / 18f);
        var simplifiedHull = SimplifyPolygon(hull, maxPoints, minDistance);
        if (simplifiedHull.Count < 3)
        {
            return null;
        }

        return new PathGeometryModel
        {
            Type = "path",
            Mode = mode,
            Closed = true,
            Points = simplifiedHull.Select(point => new PointF(point.X + 0.5f, point.Y + 0.5f)).ToList(),
        };
    }

    private static float[] ComputeVerticalBoundaryEnergy(SKBitmap bitmap, bool[] mask, BBox bounds)
    {
        var energy = new float[Math.Max(1, bounds.W)];
        for (var x = bounds.X + 1; x < bounds.Right; x++)
        {
            var localX = x - bounds.X;
            var sum = 0f;
            var count = 0;

            for (var y = bounds.Y; y < bounds.Bottom; y++)
            {
                var idx = (y * bitmap.Width) + x;
                var prevIdx = idx - 1;
                if (!mask[idx] && !mask[prevIdx])
                {
                    continue;
                }

                var current = bitmap.GetPixel(x, y);
                var previous = bitmap.GetPixel(x - 1, y);

                var lumDiff = MathF.Abs(ToLuminance(current) - ToLuminance(previous));
                var dr = current.Red - previous.Red;
                var dg = current.Green - previous.Green;
                var db = current.Blue - previous.Blue;
                var colorDiff = MathF.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3f);

                sum += lumDiff + (colorDiff * 0.45f);
                count++;
            }

            energy[localX] = count > 0 ? (sum / count) : 0f;
        }

        return energy;
    }

    private static float[] ComputeBoundaryPersistence(SKBitmap bitmap, bool[] mask, BBox bounds, float perPixelThreshold)
    {
        var persistence = new float[Math.Max(1, bounds.W)];
        for (var x = bounds.X + 1; x < bounds.Right; x++)
        {
            var localX = x - bounds.X;
            var activeRows = 0;
            var strongRows = 0;

            for (var y = bounds.Y; y < bounds.Bottom; y++)
            {
                var idx = (y * bitmap.Width) + x;
                var prevIdx = idx - 1;
                if (!mask[idx] && !mask[prevIdx])
                {
                    continue;
                }

                activeRows++;
                var current = bitmap.GetPixel(x, y);
                var previous = bitmap.GetPixel(x - 1, y);

                var lumDiff = MathF.Abs(ToLuminance(current) - ToLuminance(previous));
                var dr = current.Red - previous.Red;
                var dg = current.Green - previous.Green;
                var db = current.Blue - previous.Blue;
                var colorDiff = MathF.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3f);
                var edgeSignal = lumDiff + (colorDiff * 0.35f);

                if (edgeSignal >= perPixelThreshold)
                {
                    strongRows++;
                }
            }

            persistence[localX] = activeRows > 0 ? strongRows / (float)activeRows : 0f;
        }

        return persistence;
    }

    private static bool TryExtractSegmentShape(bool[] mask, int imageWidth, BBox parentBounds, int segmentLeft, int segmentRight, out BBox tightBounds, out int segmentArea, out List<IntPoint> boundaryPoints)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        segmentArea = 0;
        boundaryPoints = new List<IntPoint>();

        for (var y = parentBounds.Y; y < parentBounds.Bottom; y++)
        {
            for (var x = segmentLeft; x < segmentRight; x++)
            {
                var index = (y * imageWidth) + x;
                if (!mask[index])
                {
                    continue;
                }

                segmentArea++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                var isBoundary = x == segmentLeft ||
                                 x == segmentRight - 1 ||
                                 y == parentBounds.Y ||
                                 y == parentBounds.Bottom - 1;

                if (!isBoundary)
                {
                    var leftOn = mask[index - 1];
                    var rightOn = mask[index + 1];
                    var upOn = mask[index - imageWidth];
                    var downOn = mask[index + imageWidth];
                    isBoundary = !(leftOn && rightOn && upOn && downOn);
                }

                if (isBoundary)
                {
                    boundaryPoints.Add(new IntPoint(x, y));
                }
            }
        }

        if (segmentArea == 0)
        {
            tightBounds = default;
            return false;
        }

        tightBounds = new BBox(minX, minY, Math.Max(1, (maxX - minX) + 1), Math.Max(1, (maxY - minY) + 1));
        return boundaryPoints.Count >= 3;
    }

    private static PathGeometryModel BuildRectGeometry(BBox bounds, string mode)
    {
        var left = bounds.X;
        var top = bounds.Y;
        var right = bounds.Right;
        var bottom = bounds.Bottom;

        return new PathGeometryModel
        {
            Type = "path",
            Mode = mode,
            Closed = true,
            Points = new List<PointF>
            {
                new(left + 0.5f, top + 0.5f),
                new(right - 0.5f, top + 0.5f),
                new(right - 0.5f, bottom - 0.5f),
                new(left + 0.5f, bottom - 0.5f),
            },
        };
    }

    private static float ComputeObjectConfidence(MaskComponent component, float fillRatio, int imageArea, float strength)
    {
        var areaRatio = component.Area / (float)Math.Max(1, imageArea);
        var areaScore = Math.Clamp(areaRatio * 14f, 0f, 1f);
        var fillScore = Math.Clamp((fillRatio - 0.08f) / 0.65f, 0f, 1f);
        var borderPenalty = component.TouchesBorder ? 0.08f : 0f;
        return Math.Clamp(0.42f + (0.30f * fillScore) + (0.20f * areaScore) + (0.08f * strength) - borderPenalty, 0.10f, 0.98f);
    }

    private static IReadOnlyList<AutoCutoutSuggestion> DeduplicateSuggestions(IReadOnlyList<AutoCutoutSuggestion> suggestions)
    {
        var ordered = suggestions
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Bounds.W * item.Bounds.H)
            .ToList();

        var accepted = new List<AutoCutoutSuggestion>();
        foreach (var candidate in ordered)
        {
            var shouldSkip = accepted.Any(existing =>
            {
                var overlap = IoU(existing.Bounds, candidate.Bounds);
                if (overlap > 0.92f)
                {
                    return true;
                }

                if (existing.Kind == "section" && candidate.Kind == "section" && overlap > 0.78f)
                {
                    return true;
                }

                return false;
            });

            if (!shouldSkip)
            {
                accepted.Add(candidate);
            }
        }

        return accepted;
    }

    private static float IoU(BBox a, BBox b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);

        var intersectionW = Math.Max(0, x2 - x1);
        var intersectionH = Math.Max(0, y2 - y1);
        var intersectionArea = intersectionW * intersectionH;
        if (intersectionArea <= 0)
        {
            return 0f;
        }

        var areaA = Math.Max(1, a.W * a.H);
        var areaB = Math.Max(1, b.W * b.H);
        var union = areaA + areaB - intersectionArea;
        return intersectionArea / (float)Math.Max(1, union);
    }

    private static bool[] BuildForegroundMask(SKBitmap bitmap, float strength)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;

        var (meanR, meanG, meanB, stdLum) = SampleBorderColorProfile(bitmap);
        var sensitivity = Math.Clamp(strength, 0f, 1f);
        var responseScale = 1.2f - (0.55f * sensitivity);

        var colorThreshold = Math.Clamp((float)(stdLum * 2.2 + 18) * responseScale, 14f, 96f);
        var edgeThreshold = Math.Clamp((float)(stdLum * 2.8 + 24) * responseScale, 18f, 130f);

        var mask = BuildMaskPass(bitmap, meanR, meanG, meanB, colorThreshold, edgeThreshold);
        var foregroundRatio = mask.Count(value => value) / (float)(width * height);

        if (foregroundRatio < 0.004f)
        {
            mask = BuildMaskPass(bitmap, meanR, meanG, meanB, colorThreshold * 0.8f, edgeThreshold * 0.8f);
        }
        else if (foregroundRatio > 0.97f)
        {
            mask = BuildMaskPass(bitmap, meanR, meanG, meanB, colorThreshold * 1.3f, edgeThreshold * 1.3f);
        }

        return mask;
    }

    private static bool[] BuildMaskPass(SKBitmap bitmap, float meanR, float meanG, float meanB, float colorThreshold, float edgeThreshold)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var mask = new bool[width * height];
        var luminance = new float[width * height];

        var index = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var lum = ToLuminance(pixel);
                luminance[index] = lum;
                if (pixel.Alpha < 24)
                {
                    index++;
                    continue;
                }

                var dr = pixel.Red - meanR;
                var dg = pixel.Green - meanG;
                var db = pixel.Blue - meanB;
                var colorDistance = MathF.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3f);

                var edgeSignal = 0f;
                if (x > 0)
                {
                    edgeSignal += MathF.Abs(lum - luminance[index - 1]);
                }

                if (y > 0)
                {
                    edgeSignal += MathF.Abs(lum - luminance[index - width]);
                }

                mask[index] = colorDistance > colorThreshold || edgeSignal > edgeThreshold;
                index++;
            }
        }

        return mask;
    }

    private static (float meanR, float meanG, float meanB, float stdLum) SampleBorderColorProfile(SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var sumR = 0d;
        var sumG = 0d;
        var sumB = 0d;
        var sumLum = 0d;
        var sumLumSq = 0d;
        var count = 0d;

        void AddSample(int x, int y)
        {
            var pixel = bitmap.GetPixel(x, y);
            sumR += pixel.Red;
            sumG += pixel.Green;
            sumB += pixel.Blue;
            var lum = ToLuminance(pixel);
            sumLum += lum;
            sumLumSq += lum * lum;
            count++;
        }

        for (var x = 0; x < width; x++)
        {
            AddSample(x, 0);
            if (height > 1)
            {
                AddSample(x, height - 1);
            }
        }

        for (var y = 1; y < height - 1; y++)
        {
            AddSample(0, y);
            if (width > 1)
            {
                AddSample(width - 1, y);
            }
        }

        if (count <= 0)
        {
            return (127f, 127f, 127f, 32f);
        }

        var meanR = (float)(sumR / count);
        var meanG = (float)(sumG / count);
        var meanB = (float)(sumB / count);
        var meanLum = sumLum / count;
        var varianceLum = Math.Max(0d, (sumLumSq / count) - (meanLum * meanLum));
        var stdLum = (float)Math.Sqrt(varianceLum);
        return (meanR, meanG, meanB, stdLum);
    }

    private static bool[] SmoothMask(bool[] input, int width, int height, int iterations)
    {
        var current = input;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var next = new bool[current.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var neighborCount = 0;
                    for (var ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                    {
                        for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                        {
                            if (current[(ny * width) + nx])
                            {
                                neighborCount++;
                            }
                        }
                    }

                    var index = (y * width) + x;
                    var isOn = current[index];
                    next[index] = neighborCount >= 5 || (isOn && neighborCount >= 3);
                }
            }

            current = next;
        }

        return current;
    }

    private static IReadOnlyList<MaskComponent> ExtractComponents(bool[] mask, int width, int height)
    {
        var visited = new bool[mask.Length];
        var components = new List<MaskComponent>();
        var queue = new Queue<int>();

        for (var index = 0; index < mask.Length; index++)
        {
            if (!mask[index] || visited[index])
            {
                continue;
            }

            queue.Clear();
            queue.Enqueue(index);
            visited[index] = true;

            var area = 0;
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var touchesBorder = false;
            var boundary = new List<IntPoint>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;

                area++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    touchesBorder = true;
                }

                var isBoundary = false;
                VisitNeighbor(x - 1, y, ref isBoundary);
                VisitNeighbor(x + 1, y, ref isBoundary);
                VisitNeighbor(x, y - 1, ref isBoundary);
                VisitNeighbor(x, y + 1, ref isBoundary);

                if (isBoundary)
                {
                    boundary.Add(new IntPoint(x, y));
                }

                void VisitNeighbor(int nx, int ny, ref bool boundaryFlag)
                {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        boundaryFlag = true;
                        return;
                    }

                    var neighborIndex = (ny * width) + nx;
                    if (!mask[neighborIndex])
                    {
                        boundaryFlag = true;
                        return;
                    }

                    if (visited[neighborIndex])
                    {
                        return;
                    }

                    visited[neighborIndex] = true;
                    queue.Enqueue(neighborIndex);
                }
            }

            if (area > 0 && boundary.Count > 0)
            {
                components.Add(new MaskComponent
                {
                    Area = area,
                    Bounds = new BBox(minX, minY, Math.Max(1, (maxX - minX) + 1), Math.Max(1, (maxY - minY) + 1)),
                    TouchesBorder = touchesBorder,
                    BoundaryPoints = boundary,
                });
            }
        }

        return components;
    }

    private static IReadOnlyList<IntPoint> BuildConvexHull(IReadOnlyList<IntPoint> points)
    {
        if (points.Count < 3)
        {
            return points.ToList();
        }

        var unique = new List<IntPoint>(points.Count);
        var seen = new HashSet<long>();
        foreach (var point in points)
        {
            var key = (((long)point.X) << 32) ^ (uint)point.Y;
            if (seen.Add(key))
            {
                unique.Add(point);
            }
        }

        if (unique.Count < 3)
        {
            return unique;
        }

        unique.Sort(static (a, b) =>
        {
            var cmp = a.X.CompareTo(b.X);
            return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
        });

        var lower = new List<IntPoint>();
        foreach (var point in unique)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<IntPoint>();
        for (var index = unique.Count - 1; index >= 0; index--)
        {
            var point = unique[index];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static IReadOnlyList<IntPoint> SimplifyPolygon(IReadOnlyList<IntPoint> polygon, int maxPoints, float minDistance)
    {
        if (polygon.Count <= 3)
        {
            return polygon.ToList();
        }

        var minDistanceSq = minDistance * minDistance;
        var filtered = new List<IntPoint>(polygon.Count);
        foreach (var point in polygon)
        {
            if (filtered.Count == 0 || DistanceSq(filtered[^1], point) >= minDistanceSq)
            {
                filtered.Add(point);
            }
        }

        if (filtered.Count >= 3 && DistanceSq(filtered[0], filtered[^1]) < minDistanceSq)
        {
            filtered.RemoveAt(filtered.Count - 1);
        }

        if (filtered.Count <= maxPoints)
        {
            return filtered;
        }

        var step = (int)Math.Ceiling(filtered.Count / (double)maxPoints);
        var reduced = new List<IntPoint>(maxPoints);
        for (var index = 0; index < filtered.Count; index += step)
        {
            reduced.Add(filtered[index]);
        }

        return reduced.Count >= 3 ? reduced : filtered;
    }

    private static float[] Smooth1D(float[] values, int radius)
    {
        if (values.Length == 0 || radius <= 0)
        {
            return values;
        }

        var smoothed = new float[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var start = Math.Max(0, index - radius);
            var end = Math.Min(values.Length - 1, index + radius);
            var sum = 0f;
            for (var cursor = start; cursor <= end; cursor++)
            {
                sum += values[cursor];
            }

            smoothed[index] = sum / ((end - start) + 1);
        }

        return smoothed;
    }

    private static long Cross(IntPoint a, IntPoint b, IntPoint c)
    {
        return ((long)(b.X - a.X) * (c.Y - a.Y)) - ((long)(b.Y - a.Y) * (c.X - a.X));
    }

    private static float DistanceSq(IntPoint a, IntPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static float ToLuminance(SKColor color)
    {
        return (0.299f * color.Red) + (0.587f * color.Green) + (0.114f * color.Blue);
    }

    private sealed class MaskComponent
    {
        public int Area { get; init; }

        public BBox Bounds { get; init; }

        public bool TouchesBorder { get; init; }

        public required List<IntPoint> BoundaryPoints { get; init; }
    }

    private readonly record struct IntPoint(int X, int Y);
}







