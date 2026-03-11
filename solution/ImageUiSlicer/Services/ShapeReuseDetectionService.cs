using ImageUiSlicer.CanvasEngine;
using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed class ShapeReuseDetectionService
{
    public sealed class MatchOptions
    {
        public int MaxResults { get; init; } = 6;

        public float SimilarityThreshold { get; init; } = 0.73f;

        public int SearchStride { get; init; }
    }

    private readonly record struct BoundaryFeature(
        PointF BoundaryOffset,
        PointF InnerOffset,
        PointF OuterOffset,
        float TemplateContrast,
        float TemplateBoundaryLuminance,
        float Weight);

    private readonly record struct Candidate(int X, int Y, float Score);

    public IReadOnlyList<AutoCutoutService.AutoCutoutSuggestion> FindMatches(
        SKBitmap bitmap,
        CutoutModel sourceCutout,
        MatchOptions? options = null)
    {
        options ??= new MatchOptions();
        if (bitmap.Width < 24 ||
            bitmap.Height < 24 ||
            !GeometryHelper.IsValidGeometry(sourceCutout.Geometry))
        {
            return Array.Empty<AutoCutoutService.AutoCutoutSuggestion>();
        }

        var sourceBounds = sourceCutout.BBox.W > 0 && sourceCutout.BBox.H > 0
            ? sourceCutout.BBox
            : GeometryHelper.ComputeBBox(sourceCutout.Geometry.Points);
        if (sourceBounds.W < 10 || sourceBounds.H < 10)
        {
            return Array.Empty<AutoCutoutService.AutoCutoutSuggestion>();
        }

        var searchStride = options.SearchStride > 0
            ? options.SearchStride
            : Math.Clamp(Math.Min(sourceBounds.W, sourceBounds.H) / 16, 2, 10);

        var luminanceMap = BuildLuminanceMap(bitmap);
        using var sourcePath = GeometryHelper.BuildPath(sourceCutout.Geometry);
        var features = BuildBoundaryFeatures(luminanceMap, bitmap.Width, bitmap.Height, sourceBounds, sourceCutout.Geometry, sourcePath);
        if (features.Count < 18)
        {
            return Array.Empty<AutoCutoutService.AutoCutoutSuggestion>();
        }

        var coarseCandidates = ScanCandidates(
            luminanceMap,
            bitmap.Width,
            bitmap.Height,
            sourceBounds,
            features,
            searchStride,
            Math.Clamp(options.MaxResults * 6, 12, 72),
            options.SimilarityThreshold * 0.93f);

        if (coarseCandidates.Count == 0)
        {
            return Array.Empty<AutoCutoutService.AutoCutoutSuggestion>();
        }

        var refinedCandidates = RefineCandidates(
            luminanceMap,
            bitmap.Width,
            bitmap.Height,
            sourceBounds,
            features,
            coarseCandidates,
            searchStride,
            options.SimilarityThreshold);

        var deduped = DeduplicateCandidates(refinedCandidates, sourceBounds)
            .Where(candidate => candidate.Score >= options.SimilarityThreshold)
            .OrderByDescending(candidate => candidate.Score)
            .Take(Math.Clamp(options.MaxResults, 1, 16))
            .ToList();

        if (deduped.Count == 0)
        {
            return Array.Empty<AutoCutoutService.AutoCutoutSuggestion>();
        }

        var suggestions = new List<AutoCutoutService.AutoCutoutSuggestion>(deduped.Count);
        foreach (var candidate in deduped)
        {
            var dx = candidate.X - sourceBounds.X;
            var dy = candidate.Y - sourceBounds.Y;
            var geometry = GeometryHelper.Translate(sourceCutout.Geometry, dx, dy);
            suggestions.Add(new AutoCutoutService.AutoCutoutSuggestion
            {
                Kind = "smart-match",
                Label = $"{sourceCutout.Name} Match",
                Confidence = Math.Clamp(candidate.Score, 0f, 0.99f),
                Geometry = geometry,
                Bounds = GeometryHelper.ComputeBBox(geometry.Points),
            });
        }

        return suggestions;
    }

    private static List<BoundaryFeature> BuildBoundaryFeatures(
        float[] luminanceMap,
        int imageWidth,
        int imageHeight,
        BBox bounds,
        PathGeometryModel geometry,
        SKPath path)
    {
        var perimeter = EstimatePerimeter(geometry.Points, geometry.Closed);
        var sampleSpacing = Math.Clamp(perimeter / 72f, 5f, 14f);
        var boundarySamples = SampleBoundaryPoints(geometry.Points, geometry.Closed, sampleSpacing);
        if (boundarySamples.Count == 0)
        {
            return new List<BoundaryFeature>();
        }

        var center = new PointF(bounds.X + (bounds.W * 0.5f), bounds.Y + (bounds.H * 0.5f));
        var insetDistance = Math.Clamp(Math.Min(bounds.W, bounds.H) / 18f, 2.5f, 7f);
        var features = new List<BoundaryFeature>(boundarySamples.Count);

        for (var index = 0; index < boundarySamples.Count; index++)
        {
            var point = boundarySamples[index];
            var previous = boundarySamples[(index - 1 + boundarySamples.Count) % boundarySamples.Count];
            var next = boundarySamples[(index + 1) % boundarySamples.Count];
            var normal = ResolveInwardNormal(path, point, previous, next, center, insetDistance);
            if (Math.Abs(normal.X) < 0.0001f && Math.Abs(normal.Y) < 0.0001f)
            {
                continue;
            }

            var innerPoint = new PointF(point.X + (normal.X * insetDistance), point.Y + (normal.Y * insetDistance));
            var outerPoint = new PointF(point.X - (normal.X * insetDistance), point.Y - (normal.Y * insetDistance));

            var boundaryLum = SampleLuminance(luminanceMap, imageWidth, imageHeight, point.X, point.Y);
            var innerLum = SampleLuminance(luminanceMap, imageWidth, imageHeight, innerPoint.X, innerPoint.Y);
            var outerLum = SampleLuminance(luminanceMap, imageWidth, imageHeight, outerPoint.X, outerPoint.Y);
            var contrast = innerLum - outerLum;
            var contrastWeight = 0.7f + Math.Min(0.9f, Math.Abs(contrast) / 70f);

            features.Add(new BoundaryFeature(
                new PointF(point.X - bounds.X, point.Y - bounds.Y),
                new PointF(innerPoint.X - bounds.X, innerPoint.Y - bounds.Y),
                new PointF(outerPoint.X - bounds.X, outerPoint.Y - bounds.Y),
                contrast,
                boundaryLum,
                contrastWeight));
        }

        if (features.Count <= 140)
        {
            return features;
        }

        var step = features.Count / 140f;
        var reduced = new List<BoundaryFeature>(140);
        for (var index = 0; index < 140; index++)
        {
            reduced.Add(features[(int)Math.Floor(index * step)]);
        }

        return reduced;
    }

    private static List<Candidate> ScanCandidates(
        float[] luminanceMap,
        int imageWidth,
        int imageHeight,
        BBox sourceBounds,
        IReadOnlyList<BoundaryFeature> features,
        int stride,
        int keepCount,
        float minimumScore)
    {
        var candidates = new List<Candidate>(keepCount);
        var maxX = imageWidth - sourceBounds.W;
        var maxY = imageHeight - sourceBounds.H;

        for (var y = 0; y <= maxY; y += stride)
        {
            for (var x = 0; x <= maxX; x += stride)
            {
                if (IsNearOriginalBounds(x, y, sourceBounds, stride))
                {
                    continue;
                }

                var score = ScoreCandidate(luminanceMap, imageWidth, imageHeight, x, y, features);
                if (score < minimumScore)
                {
                    continue;
                }

                AddTopCandidate(candidates, new Candidate(x, y, score), keepCount);
            }
        }

        return candidates;
    }

    private static List<Candidate> RefineCandidates(
        float[] luminanceMap,
        int imageWidth,
        int imageHeight,
        BBox sourceBounds,
        IReadOnlyList<BoundaryFeature> features,
        IReadOnlyList<Candidate> coarseCandidates,
        int coarseStride,
        float minimumScore)
    {
        var refined = new List<Candidate>(coarseCandidates.Count * 2);
        var maxX = imageWidth - sourceBounds.W;
        var maxY = imageHeight - sourceBounds.H;

        foreach (var coarse in coarseCandidates.OrderByDescending(candidate => candidate.Score).Take(18))
        {
            var best = coarse;
            var startX = Math.Max(0, coarse.X - coarseStride);
            var endX = Math.Min(maxX, coarse.X + coarseStride);
            var startY = Math.Max(0, coarse.Y - coarseStride);
            var endY = Math.Min(maxY, coarse.Y + coarseStride);

            for (var y = startY; y <= endY; y++)
            {
                for (var x = startX; x <= endX; x++)
                {
                    if (IsNearOriginalBounds(x, y, sourceBounds, 1))
                    {
                        continue;
                    }

                    var score = ScoreCandidate(luminanceMap, imageWidth, imageHeight, x, y, features);
                    if (score >= best.Score)
                    {
                        best = new Candidate(x, y, score);
                    }
                }
            }

            if (best.Score >= minimumScore)
            {
                refined.Add(best);
            }
        }

        return refined;
    }

    private static IEnumerable<Candidate> DeduplicateCandidates(IEnumerable<Candidate> candidates, BBox sourceBounds)
    {
        var results = new List<Candidate>();
        var width = sourceBounds.W;
        var height = sourceBounds.H;

        foreach (var candidate in candidates.OrderByDescending(item => item.Score))
        {
            var overlapsExisting = results.Any(existing => ComputeOverlapRatio(existing, candidate, width, height) > 0.45f);
            if (!overlapsExisting)
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private static float ScoreCandidate(
        float[] luminanceMap,
        int imageWidth,
        int imageHeight,
        int candidateX,
        int candidateY,
        IReadOnlyList<BoundaryFeature> features)
    {
        var weightedScore = 0f;
        var totalWeight = 0f;

        foreach (var feature in features)
        {
            var boundaryLum = SampleLuminance(luminanceMap, imageWidth, imageHeight, candidateX + feature.BoundaryOffset.X, candidateY + feature.BoundaryOffset.Y);
            var innerLum = SampleLuminance(luminanceMap, imageWidth, imageHeight, candidateX + feature.InnerOffset.X, candidateY + feature.InnerOffset.Y);
            var outerLum = SampleLuminance(luminanceMap, imageWidth, imageHeight, candidateX + feature.OuterOffset.X, candidateY + feature.OuterOffset.Y);

            var candidateContrast = innerLum - outerLum;
            var contrastDiff = Math.Abs(candidateContrast - feature.TemplateContrast) / 255f;
            var boundaryDiff = Math.Abs(boundaryLum - feature.TemplateBoundaryLuminance) / 255f;

            var sampleScore = 1f - Math.Min(1f, (contrastDiff * 0.85f) + (boundaryDiff * 0.15f));
            if ((candidateContrast > 0f) != (feature.TemplateContrast > 0f))
            {
                sampleScore *= 0.78f;
            }

            weightedScore += sampleScore * feature.Weight;
            totalWeight += feature.Weight;
        }

        if (totalWeight <= 0.001f)
        {
            return 0f;
        }

        return weightedScore / totalWeight;
    }

    private static void AddTopCandidate(List<Candidate> candidates, Candidate candidate, int keepCount)
    {
        candidates.Add(candidate);
        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        if (candidates.Count > keepCount)
        {
            candidates.RemoveRange(keepCount, candidates.Count - keepCount);
        }
    }

    private static bool IsNearOriginalBounds(int candidateX, int candidateY, BBox sourceBounds, int padding)
    {
        var toleranceX = Math.Max(padding, sourceBounds.W / 8);
        var toleranceY = Math.Max(padding, sourceBounds.H / 8);
        return Math.Abs(candidateX - sourceBounds.X) <= toleranceX &&
               Math.Abs(candidateY - sourceBounds.Y) <= toleranceY;
    }

    private static float ComputeOverlapRatio(Candidate left, Candidate right, int width, int height)
    {
        var overlapLeft = Math.Max(left.X, right.X);
        var overlapTop = Math.Max(left.Y, right.Y);
        var overlapRight = Math.Min(left.X + width, right.X + width);
        var overlapBottom = Math.Min(left.Y + height, right.Y + height);
        if (overlapRight <= overlapLeft || overlapBottom <= overlapTop)
        {
            return 0f;
        }

        var intersection = (overlapRight - overlapLeft) * (overlapBottom - overlapTop);
        var union = (width * height * 2) - intersection;
        return union <= 0 ? 0f : intersection / (float)union;
    }

    private static PointF ResolveInwardNormal(SKPath path, PointF point, PointF previous, PointF next, PointF center, float insetDistance)
    {
        var tangent = new PointF(next.X - previous.X, next.Y - previous.Y);
        var tangentLength = MathF.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
        if (tangentLength <= 0.0001f)
        {
            tangent = new PointF(center.X - point.X, center.Y - point.Y);
            tangentLength = MathF.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
            if (tangentLength <= 0.0001f)
            {
                return default;
            }
        }

        var normalA = new PointF(-tangent.Y / tangentLength, tangent.X / tangentLength);
        var normalB = new PointF(-normalA.X, -normalA.Y);

        var candidateA = new PointF(point.X + (normalA.X * insetDistance), point.Y + (normalA.Y * insetDistance));
        if (path.Contains(candidateA.X, candidateA.Y))
        {
            return normalA;
        }

        var candidateB = new PointF(point.X + (normalB.X * insetDistance), point.Y + (normalB.Y * insetDistance));
        if (path.Contains(candidateB.X, candidateB.Y))
        {
            return normalB;
        }

        var radial = new PointF(center.X - point.X, center.Y - point.Y);
        var radialLength = MathF.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y));
        if (radialLength <= 0.0001f)
        {
            return default;
        }

        return new PointF(radial.X / radialLength, radial.Y / radialLength);
    }

    private static List<PointF> SampleBoundaryPoints(IReadOnlyList<PointF> points, bool closed, float spacing)
    {
        var sampled = new List<PointF>();
        if (points.Count == 0)
        {
            return sampled;
        }

        var segmentCount = closed ? points.Count : points.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = MathF.Sqrt((dx * dx) + (dy * dy));
            var steps = Math.Max(1, (int)MathF.Ceiling(length / Math.Max(1f, spacing)));

            for (var step = 0; step < steps; step++)
            {
                var t = step / (float)steps;
                sampled.Add(new PointF(start.X + (dx * t), start.Y + (dy * t)));
            }
        }

        if (!closed)
        {
            sampled.Add(points[^1]);
        }

        return sampled;
    }

    private static float EstimatePerimeter(IReadOnlyList<PointF> points, bool closed)
    {
        if (points.Count < 2)
        {
            return 0f;
        }

        var total = 0f;
        var segmentCount = closed ? points.Count : points.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            total += MathF.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
        }

        return total;
    }

    private static float[] BuildLuminanceMap(SKBitmap bitmap)
    {
        var luminance = new float[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                luminance[(y * bitmap.Width) + x] = (color.Red * 0.2126f) + (color.Green * 0.7152f) + (color.Blue * 0.0722f);
            }
        }

        return luminance;
    }

    private static float SampleLuminance(float[] luminanceMap, int imageWidth, int imageHeight, float x, float y)
    {
        var sampleX = Math.Clamp((int)MathF.Round(x), 0, imageWidth - 1);
        var sampleY = Math.Clamp((int)MathF.Round(y), 0, imageHeight - 1);
        return luminanceMap[(sampleY * imageWidth) + sampleX];
    }
}
