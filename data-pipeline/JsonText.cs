using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ro2.DataPipeline;

/// <summary>
/// Writes record sets with the same bytes Python's json.dumps(ensure_ascii=False) produces, which is
/// what convert_xlsx.py / convert_strings.py emit and what the committed data/ was generated with:
///   - indent=2 (", " never appears: items are separated by "," + newline, keys by ": "), or the
///     minified (",", ":") form;
///   - tables whose minified form is longer than 1,000,000 characters stay minified (convert_xlsx.py:103-104,
///     which measures len(str), i.e. code points, not bytes - identical for ASCII data);
///   - floats use Python's repr rules ("1.0", "1e-05", "1e+16"); strings escape only " \ and control chars.
///
/// Intentional differences from the old xlsx path, kept in one place:
///   1. DWORD_HEX is a number (the xlsx path stored "0x1A" text and int("0x1A") fell back to 0).
///   2. Line endings are "\n" (the Python script ran on Windows in text mode and wrote "\r\n").
///   3. BOOL columns are written as 1/0 like the old path by default, because the server's generated
///      models declare them as byte and System.Text.Json rejects true/false for a byte. BoolsAsJson
///      switches to true/false.
/// FLOAT cells are written the way the old path did: the single widened to a double and rounded to 15
/// significant digits (1.2f -> 1.20000004768372), so a regenerated table equals the committed one.
/// </summary>
internal sealed class JsonRenderer
{
    public const int MinifyThreshold = 1_000_000;

    /// <summary>Write BOOL cells as true/false instead of 1/0.</summary>
    public bool BoolsAsJson { get; init; }

    /// <summary>Count of NaN/Infinity floats written as 0 (they are not valid JSON).</summary>
    public int NonFiniteWritten { get; private set; }

    /// <summary>Chooses indented vs minified exactly like convert_xlsx.py:103-104.</summary>
    public StringBuilder RenderTable(RecordSet set)
    {
        StringBuilder minified = Render(set, indent: false);
        return CodePointCountExceeds(minified, MinifyThreshold) ? minified : Render(set, indent: true);
    }

    public StringBuilder Render(RecordSet set, bool indent)
    {
        var sb = new StringBuilder(set.Rows.Count * Math.Max(16, set.Keys.Length * 12));
        if (set.Rows.Count == 0)
            return sb.Append("[]");

        sb.Append('[');
        for (int r = 0; r < set.Rows.Count; r++)
        {
            if (r > 0) sb.Append(',');
            if (indent) sb.Append("\n  ");
            sb.Append('{');
            object[] row = set.Rows[r];
            for (int c = 0; c < Math.Min(set.Keys.Length, row.Length); c++)            {
                if (c > 0) sb.Append(',');
                if (indent) sb.Append("\n    ");
                AppendString(sb, set.Keys[c]);
                sb.Append(indent ? ": " : ":");
                AppendValue(sb, row[c]);
            }
            if (indent) sb.Append("\n  ");
            sb.Append('}');
        }
        if (indent) sb.Append('\n');
        return sb.Append(']');
    }

    /// <summary>Compact {"id":"text",...} object, as convert_strings.py writes the string tables.</summary>
    public static StringBuilder RenderStringMap(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendString(sb, entries[i].Key);
            sb.Append(':');
            AppendString(sb, entries[i].Value);
        }
        return sb.Append('}');
    }

    private void AppendValue(StringBuilder sb, object v)
    {
        switch (v)
        {
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case bool b: sb.Append(BoolsAsJson ? (b ? "true" : "false") : (b ? "1" : "0")); break;
            case string s: AppendString(sb, s); break;
            case float f:
                if (float.IsFinite(f)) AppendPythonFloat(sb, WidenedSingleText(f));
                else { NonFiniteWritten++; sb.Append('0'); }
                break;
            case double d:
                if (double.IsFinite(d)) AppendPythonFloat(sb, d.ToString("R", CultureInfo.InvariantCulture));
                else { NonFiniteWritten++; sb.Append('0'); }
                break;
            default: throw new InvalidOperationException($"Unsupported value type {v.GetType()}");
        }
    }

    public static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// The single widened to a double and rendered with 15 significant digits, rounding half UP like the
    /// old xlsx writer (.NET's own "G15" rounds an exact tie to even, so 916.9f would end in ...062 instead
    /// of ...063). A tie needs the exact expansion to stop at the 16th digit, which "E16" cannot tell from
    /// a longer one, so that rare case is checked exactly with BigInteger.
    /// </summary>
    internal static string WidenedSingleText(float f)
    {
        double d = f;
        string e16 = d.ToString("E16", CultureInfo.InvariantCulture);        bool negative = e16[0] == '-';
        string mantissa = negative ? e16[1..] : e16;
        int ePos = mantissa.IndexOf('E');
        string digits = mantissa[0] + mantissa.Substring(2, ePos - 2);        if (digits[15] != '5' || digits[16] != '0' || !ExpansionStopsAtSixteenDigits(Math.Abs(d)))
            return d.ToString("G15", CultureInfo.InvariantCulture);

        int exponent = int.Parse(mantissa.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string rounded = (BigInteger.Parse(digits[..15], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        if (rounded.Length > 15) { rounded = rounded[..15]; exponent++; }
        return (negative ? "-" : "") + rounded[0] + "." + rounded[1..] + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static bool ExpansionStopsAtSixteenDigits(double positive)
    {
        long bits = BitConverter.DoubleToInt64Bits(positive);
        int biased = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mantissa = biased == 0 ? fraction : fraction | (1L << 52);
        int exp2 = (biased == 0 ? 1 : biased) - 1075;
        BigInteger scaled = exp2 >= 0 ? mantissa << exp2 : mantissa * BigInteger.Pow(5, -exp2);
        return scaled.ToString(CultureInfo.InvariantCulture).TrimEnd('0').Length == 16;
    }

    /// <summary>
    /// Re-formats a .NET shortest round-trip string ("R": "123.456", "1E-05", "-0") the way Python's
    /// repr(float) does: exponent form when the decimal exponent is below -4 or above 15, always at
    /// least one digit after the point in fixed form, two-digit minimum exponent with a sign.
    /// </summary>
    public static void AppendPythonFloat(StringBuilder sb, string r)
    {
        int i = 0;
        bool negative = false;
        if (r[0] == '-') { negative = true; i = 1; }

        int e = r.IndexOf('E');
        string mantissa = e < 0 ? r[i..] : r[i..e];
        int exponent = e < 0 ? 0 : int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        int dot = mantissa.IndexOf('.');
        string intPart = dot < 0 ? mantissa : mantissa[..dot];
        string frac = dot < 0 ? "" : mantissa[(dot + 1)..];

        string digits = intPart + frac;
        int decpt = intPart.Length + exponent;
        int lead = 0;
        while (lead < digits.Length && digits[lead] == '0') lead++;
        if (lead == digits.Length)
        {
            sb.Append(negative ? "-0.0" : "0.0");
            return;
        }
        digits = digits[lead..];
        decpt -= lead;
        digits = digits.TrimEnd('0');

        if (negative) sb.Append('-');
        if (decpt > 16 || decpt < -3)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            int exp10 = decpt - 1;
            sb.Append('e').Append(exp10 < 0 ? '-' : '+').Append(Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.").Append('0', -decpt).Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
    }

    /// <summary>True when the builder holds more than <paramref name="limit"/> Unicode code points.</summary>
    private static bool CodePointCountExceeds(StringBuilder sb, int limit)
    {
        if (sb.Length <= limit) return false;        if (sb.Length / 2 > limit) return true;        int count = 0;
        foreach (var chunk in sb.GetChunks())
        {
            foreach (char ch in chunk.Span)
                if (!char.IsLowSurrogate(ch)) count++;
        }
        return count > limit;
    }
}
