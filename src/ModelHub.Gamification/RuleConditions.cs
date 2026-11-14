using System.Globalization;
using System.Text.Json;

namespace ModelHub.Gamification;

/// <summary>
/// Evaluates rule conditions: a JSON object whose keys must all match the event's context.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>{"streakDays":7}</c>: equals. Numbers compare numerically, text ignores case, true/false exactly.</description></item>
/// <item><description><c>{"score":{"gte":80}}</c>: comparison with <c>eq</c>, <c>ne</c>, <c>lt</c>, <c>lte</c>, <c>gt</c> or <c>gte</c>.</description></item>
/// <item><description><c>{"localHour":{"lt":7},"dayOfWeek":"Saturday"}</c>: every key must match.</description></item>
/// </list>
/// A key missing from the context, or a malformed condition, never matches. Context keys are matched ignoring case.
/// </remarks>
public static class RuleConditions
{
    private static readonly string[] Operators = ["eq", "ne", "lt", "lte", "gt", "gte"];

    /// <summary>True when <paramref name="condition"/> is empty, or every key in it matches <paramref name="context"/>.</summary>
    /// <param name="condition">The JSON condition, or null for "always".</param>
    /// <param name="context">The event's facts.</param>
    public static bool Matches(string? condition, IReadOnlyDictionary<string, object?>? context)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;

        JsonDocument document;
        try { document = JsonDocument.Parse(condition); }
        catch (JsonException) { return false; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!TryGet(context, property.Name, out var actual) || actual is null) return false;

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var op in property.Value.EnumerateObject())
                        if (!Compare(actual, op.Name, op.Value)) return false;
                }
                else if (!Compare(actual, "eq", property.Value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Checks that <paramref name="condition"/> is empty or a well-formed condition, before you store it.</summary>
    /// <param name="condition">The JSON condition.</param>
    /// <param name="error">Why it is not valid; null when it is.</param>
    public static bool IsValid(string? condition, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(condition)) return true;

        try
        {
            using var document = JsonDocument.Parse(condition);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "A condition must be a JSON object, such as {\"score\":{\"gte\":80}}.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False:
                        break;

                    case JsonValueKind.Object:
                        foreach (var op in property.Value.EnumerateObject())
                        {
                            if (!Operators.Contains(op.Name))
                            {
                                error = $"Unknown operator '{op.Name}' on '{property.Name}'. Use eq, ne, lt, lte, gt or gte.";
                                return false;
                            }
                            var isEquality = op.Name is "eq" or "ne";
                            var valueIsScalar = op.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String
                                or JsonValueKind.True or JsonValueKind.False;
                            if (isEquality ? !valueIsScalar : op.Value.ValueKind != JsonValueKind.Number)
                            {
                                error = isEquality
                                    ? $"'{op.Name}' on '{property.Name}' needs a number, text or true/false."
                                    : $"'{op.Name}' on '{property.Name}' needs a number.";
                                return false;
                            }
                        }
                        break;

                    default:
                        error = $"'{property.Name}' must be a number, text, true/false or an operator object such as {{\"gte\":80}}.";
                        return false;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"The condition is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool Compare(object actual, string op, JsonElement expected)
    {
        switch (op)
        {
            case "eq":
                return AreEqual(actual, expected);
            case "ne":
                return !AreEqual(actual, expected);
            case "lt" or "lte" or "gt" or "gte":
                if (expected.ValueKind != JsonValueKind.Number || !expected.TryGetDouble(out var limit)
                    || !TryNumber(actual, out var number))
                    return false;
                return op switch
                {
                    "lt" => number < limit,
                    "lte" => number <= limit,
                    "gt" => number > limit,
                    _ => number >= limit,
                };
            default:
                return false;
        }
    }

    private static bool AreEqual(object actual, JsonElement expected) => expected.ValueKind switch
    {
        JsonValueKind.Number => expected.TryGetDouble(out var e) && TryNumber(actual, out var n) && n == e,
        JsonValueKind.True or JsonValueKind.False => TryBool(actual, out var b) && b == expected.GetBoolean(),
        JsonValueKind.String => string.Equals(AsText(actual), expected.GetString(), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool TryGet(IReadOnlyDictionary<string, object?>? context, string key, out object? value)
    {
        value = null;
        if (context is null) return false;
        if (context.TryGetValue(key, out value)) return true;

        foreach (var pair in context)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        return false;
    }

    // Context values arrive as CLR numbers, strings, or JsonElement when they were deserialised from a request body.
    internal static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case short s: number = s; return true;
            case byte b: number = b; return true;
            case uint ui: number = ui; return true;
            case ulong ul: number = ul; return true;
            case double d: number = d; return true;
            case float f: number = f; return true;
            case decimal m: number = (double)m; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element: return element.TryGetDouble(out number);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            case string text:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryBool(object value, out bool result)
    {
        switch (value)
        {
            case bool b: result = b; return true;
            case JsonElement { ValueKind: JsonValueKind.True }: result = true; return true;
            case JsonElement { ValueKind: JsonValueKind.False }: result = false; return true;
            case JsonElement { ValueKind: JsonValueKind.String } element: return bool.TryParse(element.GetString(), out result);
            case string text: return bool.TryParse(text, out result);
            default: result = false; return false;
        }
    }

    private static string? AsText(object value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
