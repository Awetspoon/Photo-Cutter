using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageUiSlicer.Models;
using SkiaSharp;

namespace ImageUiSlicer.Services;

public sealed class AiCutoutDetectionService
{
    public const string DefaultModel = "gpt-4.1-mini";

    private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AutoCutoutService _autoCutoutService = new();

    public sealed class AiDetectionOptions
    {
        public string? ApiKey { get; init; }

        public string Model { get; init; } = DefaultModel;

        public int MaxResults { get; init; } = 10;

        public float Strength { get; init; } = 0.65f;
    }

    public async Task<IReadOnlyList<AutoCutoutService.AutoCutoutSuggestion>> DetectAsync(
        SKBitmap bitmap,
        AiDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AiDetectionOptions();
        var apiKey = ResolveApiKey(options.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI Detect needs an OpenAI API key. Set OPENAI_API_KEY, then try again.");
        }

        var requestJson = BuildRequestJson(bitmap, options);
        using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildApiErrorMessage(responseText, (int)response.StatusCode));
        }

        var detection = ParseDetectionResponse(responseText);
        return BuildSuggestions(bitmap, detection, options);
    }

    private string BuildRequestJson(SKBitmap bitmap, AiDetectionOptions options)
    {
        var imageDataUrl = EncodeBitmapForVision(bitmap, maxDimension: 1600);
        var maxResults = Math.Clamp(options.MaxResults, 1, 16);

        var requestBody = new
        {
            model = string.IsNullOrWhiteSpace(options.Model) ? DefaultModel : options.Model,
            max_output_tokens = 1800,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = BuildPrompt(maxResults),
                        },
                        new
                        {
                            type = "input_image",
                            image_url = imageDataUrl,
                            detail = "high",
                        },
                    },
                },
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "photo_cutter_detection",
                    strict = true,
                    schema = CreateSchema(),
                },
            },
        };

        return JsonSerializer.Serialize(requestBody, JsonOptions);
    }

    private IReadOnlyList<AutoCutoutService.AutoCutoutSuggestion> BuildSuggestions(SKBitmap bitmap, AiDetectionResponse detection, AiDetectionOptions options)
    {
        var rawSuggestions = new List<AutoCutoutService.AutoCutoutSuggestion>();
        foreach (var element in detection.Elements)
        {
            if (element.Bbox is null)
            {
                continue;
            }

            var bounds = ToPixelBounds(element.Bbox, bitmap.Width, bitmap.Height);
            if (bounds.W < 8 || bounds.H < 8)
            {
                continue;
            }

            var label = CleanLabel(element.Label);
            var kind = NormalizeKind(element.Kind);
            var mode = kind is "button" or "tab" or "section" ? "ai-section" : "ai";
            var preferredGeometry = BuildPreferredGeometry(element.Polygon, bitmap.Width, bitmap.Height, mode);
            var confidence = (float)Math.Clamp(element.Confidence ?? 0.78, 0.08, 0.99);

            rawSuggestions.Add(_autoCutoutService.CreateSuggestionFromHint(
                bitmap,
                bounds,
                kind,
                confidence,
                mode: mode,
                label: label,
                preferredGeometry: preferredGeometry,
                strength: Math.Max(0.35f, options.Strength)));
        }

        return Deduplicate(rawSuggestions)
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Bounds.W * item.Bounds.H)
            .Take(Math.Clamp(options.MaxResults, 1, 16))
            .ToList();
    }

    private static IReadOnlyList<AutoCutoutService.AutoCutoutSuggestion> Deduplicate(IReadOnlyList<AutoCutoutService.AutoCutoutSuggestion> suggestions)
    {
        var ordered = suggestions
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Bounds.W * item.Bounds.H)
            .ToList();

        var accepted = new List<AutoCutoutService.AutoCutoutSuggestion>();
        foreach (var candidate in ordered)
        {
            var overlapsExisting = accepted.Any(existing =>
            {
                var overlap = IoU(existing.Bounds, candidate.Bounds);
                if (overlap >= 0.86f)
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(existing.Label) &&
                    !string.IsNullOrWhiteSpace(candidate.Label) &&
                    string.Equals(existing.Label, candidate.Label, StringComparison.OrdinalIgnoreCase) &&
                    overlap >= 0.42f)
                {
                    return true;
                }

                return false;
            });

            if (!overlapsExisting)
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
        return intersectionArea / (float)Math.Max(1, areaA + areaB - intersectionArea);
    }

    private static BBox ToPixelBounds(AiBbox bbox, int imageWidth, int imageHeight)
    {
        var left = Math.Clamp((int)Math.Floor(Math.Clamp(bbox.X, 0d, 0.995d) * imageWidth), 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(Math.Clamp(bbox.Y, 0d, 0.995d) * imageHeight), 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp((int)Math.Ceiling(Math.Clamp(bbox.X + bbox.Width, 0.005d, 1d) * imageWidth), left + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(Math.Clamp(bbox.Y + bbox.Height, 0.005d, 1d) * imageHeight), top + 1, imageHeight);
        return new BBox(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static PathGeometryModel? BuildPreferredGeometry(IReadOnlyList<AiPoint>? polygon, int imageWidth, int imageHeight, string mode)
    {
        if (polygon is null || polygon.Count < 3)
        {
            return null;
        }

        var points = new List<PointF>(polygon.Count);
        foreach (var point in polygon)
        {
            var x = (float)Math.Clamp(point.X, 0d, 1d) * Math.Max(1, imageWidth - 1);
            var y = (float)Math.Clamp(point.Y, 0d, 1d) * Math.Max(1, imageHeight - 1);

            if (points.Count > 0)
            {
                var previous = points[^1];
                var dx = previous.X - x;
                var dy = previous.Y - y;
                if ((dx * dx) + (dy * dy) < 16f)
                {
                    continue;
                }
            }

            points.Add(new PointF(x, y));
        }

        if (points.Count < 3)
        {
            return null;
        }

        return new PathGeometryModel
        {
            Type = "path",
            Mode = mode,
            Closed = true,
            Points = points,
        };
    }

    private static string EncodeBitmapForVision(SKBitmap bitmap, int maxDimension)
    {
        SKBitmap? resized = null;
        try
        {
            var longestSide = Math.Max(bitmap.Width, bitmap.Height);
            var workingBitmap = bitmap;
            if (longestSide > maxDimension)
            {
                var scale = maxDimension / (float)longestSide;
                var resizedWidth = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                var resizedHeight = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                resized = new SKBitmap(resizedWidth, resizedHeight, bitmap.ColorType, bitmap.AlphaType);
                bitmap.ScalePixels(resized, SKFilterQuality.High);
                workingBitmap = resized;
            }

            using var image = SKImage.FromBitmap(workingBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private static object CreateSchema()
    {
        return new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "elements" },
            properties = new
            {
                elements = new
                {
                    type = "array",
                    minItems = 0,
                    maxItems = 16,
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "label", "kind", "confidence", "bbox" },
                        properties = new
                        {
                            label = new { type = "string" },
                            kind = new { type = "string" },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            bbox = new
                            {
                                type = "object",
                                additionalProperties = false,
                                required = new[] { "x", "y", "width", "height" },
                                properties = new
                                {
                                    x = new { type = "number", minimum = 0, maximum = 1 },
                                    y = new { type = "number", minimum = 0, maximum = 1 },
                                    width = new { type = "number", minimum = 0.001, maximum = 1 },
                                    height = new { type = "number", minimum = 0.001, maximum = 1 },
                                },
                            },
                            polygon = new
                            {
                                type = "array",
                                minItems = 3,
                                maxItems = 16,
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    required = new[] { "x", "y" },
                                    properties = new
                                    {
                                        x = new { type = "number", minimum = 0, maximum = 1 },
                                        y = new { type = "number", minimum = 0, maximum = 1 },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static string BuildPrompt(int maxResults)
    {
        return $"""
You are assisting a desktop image cutout tool.

Analyze the uploaded image and identify the most useful standalone visual assets that should be exported as separate transparent cutouts.

Rules:
- Prioritize whole UI elements and artwork: buttons, tabs, badges, icons, cards, characters, isolated objects, or complete decorative pieces.
- If a long bar or strip contains multiple distinct buttons or tabs, split them into separate elements instead of returning one giant parent strip.
- Do not return duplicate parent-and-child pairs unless the parent is clearly a standalone reusable asset on its own.
- Avoid tiny text fragments, shadows by themselves, or background-only regions.
- Return at most {maxResults} elements.
- Coordinates must be normalized from 0 to 1.
- Use polygon only when it meaningfully helps hug the visible silhouette; otherwise bbox is enough.
- Confidence should reflect how likely each item is a complete exportable cutout.
- Labels should be short, human-friendly names such as Home, Events, Add Button, Gear, Card, Castle, Character.

Return JSON only.
""";
    }

    private static AiDetectionResponse ParseDetectionResponse(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        if (TryExtractRefusal(root, out var refusal))
        {
            throw new InvalidOperationException($"OpenAI refused the image analysis request: {refusal}");
        }

        if (TryExtractStructuredPayload(root, out var payloadJson))
        {
            return JsonSerializer.Deserialize<AiDetectionResponse>(payloadJson, JsonOptions) ?? new AiDetectionResponse();
        }

        throw new InvalidOperationException("OpenAI returned no structured AI detect data.");
    }

    private static bool TryExtractRefusal(JsonElement root, out string refusal)
    {
        refusal = string.Empty;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in output.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeProperty.GetString();
                if (!string.Equals(type, "refusal", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("refusal", out var refusalProperty) && refusalProperty.ValueKind == JsonValueKind.String)
                {
                    refusal = refusalProperty.GetString() ?? "The request was refused.";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractStructuredPayload(JsonElement root, out string payloadJson)
    {
        payloadJson = string.Empty;

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            payloadJson = outputText.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(payloadJson);
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in output.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("json", out var jsonProperty))
                {
                    payloadJson = jsonProperty.GetRawText();
                    return true;
                }

                if (item.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
                {
                    payloadJson = textProperty.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(payloadJson))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string BuildApiErrorMessage(string responseText, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return $"AI detect failed ({statusCode}): {message.GetString()}";
            }
        }
        catch
        {
        }

        return $"AI detect failed ({statusCode}).";
    }

    private static string? ResolveApiKey(string? explicitApiKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            return explicitApiKey.Trim();
        }

        return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    private static string CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var cleaned = label.Trim();
        cleaned = cleaned.Replace('_', ' ');
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Length <= 48 ? cleaned : cleaned[..48].Trim();
    }

    private static string NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "element";
        }

        var normalized = kind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "button" => "button",
            "tab" => "tab",
            "section" => "section",
            "icon" => "icon",
            "card" => "card",
            "panel" => "panel",
            "badge" => "badge",
            _ => "element",
        };
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
    }

    private sealed class AiDetectionResponse
    {
        public List<AiElement> Elements { get; set; } = new();
    }

    private sealed class AiElement
    {
        public string? Label { get; set; }

        public string? Kind { get; set; }

        public double? Confidence { get; set; }

        public AiBbox? Bbox { get; set; }

        public List<AiPoint>? Polygon { get; set; }
    }

    private sealed class AiBbox
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }

    private sealed class AiPoint
    {
        public double X { get; set; }

        public double Y { get; set; }
    }
}
