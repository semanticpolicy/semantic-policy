using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SemanticPolicy;

/// <summary>
/// The JSON Canonicalization Scheme of RFC 8785: members sorted by their names' UTF-16 code units at every
/// depth, arrays in order, no whitespace, strings escaped as ECMAScript's <c>JSON.stringify</c> escapes them,
/// numbers in ECMAScript's <c>Number::toString</c> form, all of it UTF-8. Any JCS implementation in any
/// language produces the same bytes from the same JSON data, which is what makes a hash of them a stable
/// name for that data.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>The canonical form of <paramref name="node"/>, as UTF-8.</summary>
    /// <exception cref="ArgumentException">
    /// The node holds a value JSON has no form for: a lone surrogate in a string or a member name, NaN or
    /// an infinity.
    /// </exception>
    public static byte[] ToUtf8Bytes(JsonNode? node)
    {
        StringBuilder text = new();
        Write(text, node);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static void Write(StringBuilder text, JsonNode? node)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject members:
                WriteObject(text, members);
                break;
            case JsonArray items:
                text.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(',');
                    }

                    Write(text, items[i]);
                }

                text.Append(']');
                break;
            case JsonValue value:
                WriteValue(text, value);
                break;
            default:
                throw new ArgumentException($"A {node.GetType().Name} has no canonical form.", nameof(node));
        }
    }

    private static void WriteObject(StringBuilder text, JsonObject members)
    {
        // Ordinal comparison of .NET strings is a comparison of UTF-16 code units, which is the order the
        // RFC prescribes; a culture-aware one would not be.
        KeyValuePair<string, JsonNode?>[] sorted = [.. members];
        Array.Sort(sorted, static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        text.Append('{');
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            WriteString(text, sorted[i].Key);
            text.Append(':');
            Write(text, sorted[i].Value);
        }

        text.Append('}');
    }

    private static void WriteValue(StringBuilder text, JsonValue value)
    {
        // A number is read back as the double it denotes, never copied from the text it came in: the
        // serializer writes 1E-05 or -0 where JCS writes 0.00001 or 0, and a person may write 0.50.
        if (value.TryGetValue(out double number))
        {
            WriteNumber(text, number);
            return;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(text, value.GetValue<string>());
                break;
            case JsonValueKind.Number:
                // A number held as another CLR type, or one too large for a double, which parses to an
                // infinity and is refused as one.
                WriteNumber(text, double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            case JsonValueKind.Null:
                text.Append("null");
                break;
            default:
                throw new ArgumentException("A JSON value has no canonical form.", nameof(value));
        }
    }

    private static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case < ' ':
                    text.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        text.Append(c).Append(value[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        throw new ArgumentException(
                            "A string holds a lone surrogate, which UTF-8 cannot encode.",
                            nameof(value));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        text.Append('"');
    }

    // ECMAScript's Number::toString (ECMA-262 §7.1.12.1). .NET's round-trip format already gives the
    // shortest digits that read back as the same double, choosing the closest where several are as short,
    // which is how ECMAScript chooses them; only where the decimal point goes, and whether an exponent is
    // written, differ between the two.
    private static void WriteNumber(StringBuilder text, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("NaN and the infinities have no JSON form.", nameof(value));
        }

        // Negative zero too: RFC 8785 writes it as 0.
        if (value == 0)
        {
            text.Append('0');
            return;
        }

        string shortest = value.ToString("R", CultureInfo.InvariantCulture);
        if (shortest[0] == '-')
        {
            text.Append('-');
            shortest = shortest[1..];
        }

        // shortest is "ddd.ddd" or "d.dddE±xx". Reduced to its significant digits s, with n the position of
        // the decimal point relative to them, the value is 0.s × 10^n.
        int exponentAt = shortest.IndexOf('E', StringComparison.Ordinal);
        string mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
        int exponent = exponentAt < 0
            ? 0
            : int.Parse(shortest[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int pointAt = mantissa.IndexOf('.', StringComparison.Ordinal);
        string whole = pointAt < 0 ? mantissa : mantissa[..pointAt];
        string digits = pointAt < 0 ? whole : whole + mantissa[(pointAt + 1)..];
        int n = whole.Length + exponent;
        int leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits[leadingZeros..].TrimEnd('0');
        n -= leadingZeros;
        int k = digits.Length;

        if (k <= n && n <= 21)
        {
            text.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            text.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            text.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            text.Append(digits[0]);
            if (k > 1)
            {
                text.Append('.').Append(digits, 1, k - 1);
            }

            int written = n - 1;
            text.Append('e')
                .Append(written < 0 ? '-' : '+')
                .Append(Math.Abs(written).ToString(CultureInfo.InvariantCulture));
        }
    }
}
