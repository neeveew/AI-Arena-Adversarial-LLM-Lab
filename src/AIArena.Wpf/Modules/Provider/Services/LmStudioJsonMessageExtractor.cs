using System.Text.Json;

namespace AIArena.Wpf.Services;

internal static class LmStudioJsonMessageExtractor
{
    /// <summary>
    /// Enough to carry a real provider error, short of pasting a web page into
    /// a status line.
    /// </summary>
    internal const int MaxMessageLength = 400;

    internal const string TruncationNotice = "... [truncated]";

    public static string ExtractMessage(string body, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return Cap(ExtractMessage(doc.RootElement, propertyNames));
        }
        catch (JsonException)
        {
            // A body that is not JSON is usually an HTML error page from a proxy
            // or from a base URL pointing at something that is not a provider,
            // and the whole of it used to become the operator-facing message.
            // These strings are shown in status lines and failure dialogs, so an
            // unbounded one is a page of markup where a sentence belongs.
            return Cap(body.Trim());
        }
    }

    private static string Cap(string message)
    {
        return message.Length <= MaxMessageLength
            ? message
            : message[..(MaxMessageLength - TruncationNotice.Length)] + TruncationNotice;
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
