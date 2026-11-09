using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace SuperSemantics.Vsl;

/// <summary>
/// JSON exactly as Python's <c>json.dumps(value, sort_keys=True)</c> writes it, so ledger hashes match
/// vsl-core byte for byte: keys sorted by code point, every non-ASCII character escaped as
/// <c>\uXXXX</c> (lowercase hex, surrogate pairs for characters outside the BMP), and floats in
/// Python's <c>repr</c> form (<c>2.0</c>, <c>1e-05</c>, <c>1e+16</c>, <c>NaN</c>, <c>Infinity</c>).
/// Integers and floats stay distinct: <c>2</c> (an <see cref="int"/> or <see cref="long"/>) and
/// <c>2.0</c> (a <see cref="double"/>) serialize differently, as in Python.
/// </summary>
/// <remarks>
/// Supported values: null, <see cref="bool"/>, <see cref="string"/>, <see cref="char"/>, the integer
/// types and <see cref="BigInteger"/>, <see cref="double"/> and <see cref="float"/>, enums (written as
/// their name, or the wire spelling for this package's own enums), dictionaries with string keys,
/// other enumerables (as arrays) and <see cref="JsonElement"/>. <see cref="decimal"/> is refused:
/// Python's json module cannot write a Decimal either, and silently turning one into a double would
/// change its value.
/// </remarks>
public static class PythonJson
{
    /// <summary>Compact form, <c>separators=(",", ":")</c>: the form ledger entry hashes use.</summary>
    public static string SerializeCompact(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, ",", ":");
        return builder.ToString();
    }

    /// <summary>Python's default separators, <c>", "</c> and <c>": "</c>: the form JSONL ledger lines
    /// and certificate hashes use.</summary>
    public static string SerializeDefault(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, ", ", ": ");
        return builder.ToString();
    }

    /// <summary>
    /// Parses JSON into plain values that serialize back the way Python would after
    /// <c>json.loads</c>: objects become <see cref="Dictionary{TKey,TValue}"/> (last duplicate key wins),
    /// arrays become <see cref="List{T}"/>, numbers with a fraction or exponent become
    /// <see cref="double"/>, other numbers become <see cref="long"/> or <see cref="BigInteger"/>.
    /// </summary>
    public static object? Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        return FromElement(document.RootElement);
    }

    /// <summary>Converts a <see cref="JsonElement"/> the same way <see cref="Parse"/> does.</summary>
    public static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
                {
                    return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                }

                // Box each branch separately: in a ?: expression the long would be widened to BigInteger.
                if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    return l;
                }

                return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }

                return list;
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = FromElement(property.Value);
                }

                return dict;
            default:
                throw new ArgumentException($"Unsupported JSON value kind {element.ValueKind}.", nameof(element));
        }
    }

    /// <summary>A double as Python's <c>repr()</c> (and therefore <c>json.dumps</c>) writes it.</summary>
    public static string FormatFloat(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        // .NET's round-trip format gives the shortest digit string that round-trips, the same digits
        // Python's repr chooses; only the layout differs, so take the digits and decimal position from
        // it and lay them out the way CPython's float_repr does.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        if (negative) text = text[1..];

        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.');
        var digits = dot >= 0 ? text.Remove(dot, 1) : text;
        var decimalPoint = (dot >= 0 ? dot : text.Length) + exponent;

        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0') leadingZeros++;
        digits = digits[leadingZeros..];
        decimalPoint -= leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        string body;
        if (decimalPoint <= -4 || decimalPoint > 16)
        {
            var mantissa = digits.Length > 1 ? $"{digits[0]}.{digits[1..]}" : digits;
            var exp = decimalPoint - 1;
            body = $"{mantissa}e{(exp < 0 ? '-' : '+')}{Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture)}";
        }
        else if (decimalPoint <= 0)
        {
            body = "0." + new string('0', -decimalPoint) + digits;
        }
        else if (decimalPoint >= digits.Length)
        {
            body = digits + new string('0', decimalPoint - digits.Length) + ".0";
        }
        else
        {
            body = $"{digits[..decimalPoint]}.{digits[decimalPoint..]}";
        }

        return negative ? "-" + body : body;
    }

    private static void Write(StringBuilder sb, object? value, string itemSeparator, string keySeparator)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case char c:
                WriteString(sb, c.ToString());
                return;
            case double d:
                sb.Append(FormatFloat(d));
                return;
            case float f:
                sb.Append(FormatFloat(f));
                return;
            case decimal:
                throw new NotSupportedException(
                    "decimal values cannot be written to a ledger payload: Python's json module cannot write one either, "
                    + "and converting to double would change the value. Use a double, an integer (for example minor units) or a string.");
            case sbyte or byte or short or ushort or int or uint or long or ulong or BigInteger:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case LedgerEntryType entryType:
                WriteString(sb, entryType.ToWire());
                return;
            case VerificationResult result:
                WriteString(sb, result.ToWire());
                return;
            case AssuranceLevel level:
                WriteString(sb, level.ToWire());
                return;
            case F2Modification modification:
                WriteString(sb, modification.ToWire());
                return;
            case Enum other:
                WriteString(sb, other.ToString());
                return;
            case JsonElement element:
                Write(sb, FromElement(element), itemSeparator, keySeparator);
                return;
            case IDictionary dictionary:
                WriteObject(sb, ToPairs(dictionary), itemSeparator, keySeparator);
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                WriteObject(sb, pairs, itemSeparator, keySeparator);
                return;
            case IEnumerable enumerable:
                sb.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(itemSeparator);
                    first = false;
                    Write(sb, item, itemSeparator, keySeparator);
                }

                sb.Append(']');
                return;
            default:
                throw new NotSupportedException($"Cannot write a value of type {value.GetType().FullName} as ledger JSON.");
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> ToPairs(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                throw new NotSupportedException("Ledger JSON object keys must be strings.");
            }

            yield return new KeyValuePair<string, object?>(key, entry.Value);
        }
    }

    private static void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> pairs, string itemSeparator, string keySeparator)
    {
        // Python keeps the last value for a repeated key; a dictionary cannot repeat one, so sorting is enough.
        var sorted = pairs.OrderBy(p => p.Key, CodePointComparer.Instance).ToList();
        sb.Append('{');
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0) sb.Append(itemSeparator);
            WriteString(sb, sorted[i].Key);
            sb.Append(keySeparator);
            Write(sb, sorted[i].Value, itemSeparator, keySeparator);
        }

        sb.Append('}');
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ' || c > '~')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
