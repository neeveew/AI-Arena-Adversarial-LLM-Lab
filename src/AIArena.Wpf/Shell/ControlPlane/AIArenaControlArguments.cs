using System.Text.Json;

namespace AIArena.Wpf;

internal static class AIArenaControlArguments
{
    public static bool Has(AIArenaControlRequest request, string name)
    {
        return request.Args is not null
            && request.Args.TryGetValue(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    public static string String(AIArenaControlRequest request, string name)
    {
        if (request.Args is null
            || !request.Args.TryGetValue(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();
    }

    public static string? OptionalString(AIArenaControlRequest request, string name)
    {
        var value = String(request, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool TryOptionalBool(AIArenaControlRequest request, string name, out bool? result)
    {
        result = null;
        if (request.Args is null
            || !request.Args.TryGetValue(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                result = false;
                return true;
            case JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed):
                result = parsed;
                return true;
            case JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1:
                result = number == 1;
                return true;
            default:
                return false;
        }
    }

    public static bool TryOptionalString(
        AIArenaControlRequest request,
        string name,
        out string? result,
        bool allowEmpty = true)
    {
        result = null;
        if (request.Args is null
            || !request.Args.TryGetValue(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = value.GetString() ?? "";
        return allowEmpty || !string.IsNullOrWhiteSpace(result);
    }

    public static bool TryOptionalInt(AIArenaControlRequest request, string name, out int? result)
    {
        result = null;
        if (request.Args is null
            || !request.Args.TryGetValue(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            result = number;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out number))
        {
            result = number;
            return true;
        }

        return false;
    }

    public static bool TryOptionalDouble(AIArenaControlRequest request, string name, out double? result)
    {
        result = null;
        if (request.Args is null
            || !request.Args.TryGetValue(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            result = number;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out number))
        {
            result = number;
            return true;
        }

        return false;
    }

    public static bool TryRequiredInt(AIArenaControlRequest request, string name, out int result)
    {
        result = 0;
        if (request.Args is null || !request.Args.TryGetValue(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out result),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out result),
            _ => false
        };
    }
}
