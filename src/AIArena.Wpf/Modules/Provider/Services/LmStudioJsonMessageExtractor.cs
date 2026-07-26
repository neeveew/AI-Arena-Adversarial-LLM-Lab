using System.Text.Json;

namespace AIArena.Wpf.Services;

internal static class LmStudioJsonMessageExtractor
{
    public static string ExtractMessage(string body, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ExtractMessage(doc.RootElement, propertyNames);
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    public static string ExtractMessage(JsonElement element, params string[] propertyNames)
    {
        if (propertyNames.Length == 0)
        {
            propertyNames = ["message", "error", "detail"];
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()?.Trim() ?? "";
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value))
            {
                var message = ExtractValueMessage(value);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }

        return "";
    }

    private static string ExtractValueMessage(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()?.Trim() ?? "";
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var message = ExtractValueMessage(item);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }

                return "";
            case JsonValueKind.Object:
                foreach (var propertyName in new[] { "message", "error", "detail", "reason", "code" })
                {
                    if (value.TryGetProperty(propertyName, out var child))
                    {
                        var message = ExtractValueMessage(child);
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            return message;
                        }
                    }
                }

                foreach (var property in value.EnumerateObject())
                {
                    var message = ExtractValueMessage(property.Value);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }

                return "";
            default:
                return "";
        }
    }
}
