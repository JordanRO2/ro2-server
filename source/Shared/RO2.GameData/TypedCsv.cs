using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RO2.GameData;

/// <summary>
/// Reader for the game's own typed-CSV table format (the master data tables the client ships):
/// <code>
/// ##META&lt;TAB&gt;magic=RO2!&lt;TAB&gt;timestamp=
/// #DWORD,STRING,FLOAT,...        (type row, informational)
/// ID,DESC,Rate,...               (column names)
/// 1,"a, quoted",0.5,...          (data rows, RFC 4180 quoting)
/// </code>
/// Each row becomes one deserialized record. A column maps to the public settable property whose
/// <see cref="JsonPropertyNameAttribute"/> equals the column name, else whose own name does (both
/// case-insensitive), so a column is spelled exactly like the matching JSON key. The PROPERTY's type
/// decides how a cell is converted; the type row is not consulted. An empty cell is 0 / "" (the same
/// convention as the data pipeline). Unknown columns are ignored and missing ones keep the property default.
/// </summary>
public static class TypedCsv
{
    /// <summary>Reads <paramref name="path"/> (UTF-8, BOM optional) into a list of <typeparamref name="T"/>.</summary>
    public static List<T> Load<T>(string path) => Parse<T>(File.ReadAllText(path, Encoding.UTF8), path);

    /// <summary>Parses typed-CSV <paramref name="text"/>; <paramref name="source"/> only labels error messages.</summary>
    public static List<T> Parse<T>(string text, string source = "<text>")
    {
        var rows = ReadRows(text);
        var next = 0;
        if (next < rows.Count && rows[next][0].StartsWith("##META", StringComparison.Ordinal)) next++;
        if (next < rows.Count && rows[next][0].StartsWith('#')) next++;
        if (next >= rows.Count) return new List<T>();

        var header = rows[next++];
        var columns = new Column?[header.Length];
        for (var i = 0; i < header.Length; i++)
            columns[i] = TypeMap<T>.Columns.GetValueOrDefault(header[i].Trim());

        var result = new List<T>(rows.Count - next);
        for (; next < rows.Count; next++)
        {
            var row = rows[next];
            var item = Activator.CreateInstance<T>();
            for (var i = 0; i < row.Length && i < columns.Length; i++)
            {
                var column = columns[i];
                if (column is null) continue;
                try
                {
                    column.Property.SetValue(item, column.Convert(row[i]));
                }
                catch (Exception e) when (e is FormatException or OverflowException or NotSupportedException)
                {
                    throw new FormatException(
                        $"{source}: row {next + 1}, column '{header[i]}': cannot read '{row[i]}' as {column.Property.PropertyType.Name}", e);
                }
            }
            result.Add(item);
        }
        return result;
    }

    private sealed record Column(PropertyInfo Property, Func<string, object?> Convert);

    private static class TypeMap<T>
    {
        public static readonly Dictionary<string, Column> Columns = Build();

        private static Dictionary<string, Column> Build()
        {
            var map = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetSetMethod() is null || p.GetIndexParameters().Length != 0) continue;
                var column = new Column(p, Converter(p.PropertyType));
                var jsonName = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (jsonName is not null) map[jsonName] = column;
                map.TryAdd(p.Name, column);
            }
            return map;
        }
    }

    private static Func<string, object?> Converter(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return s => s;
        if (type == typeof(byte)) return s => IsBlank(s) ? (byte)0 : checked((byte)Whole(s));
        if (type == typeof(sbyte)) return s => IsBlank(s) ? (sbyte)0 : checked((sbyte)Whole(s));
        if (type == typeof(short)) return s => IsBlank(s) ? (short)0 : checked((short)Whole(s));
        if (type == typeof(ushort)) return s => IsBlank(s) ? (ushort)0 : checked((ushort)Whole(s));
        if (type == typeof(int)) return s => IsBlank(s) ? 0 : checked((int)Whole(s));
        if (type == typeof(uint)) return s => IsBlank(s) ? 0u : checked((uint)Whole(s));
        if (type == typeof(long)) return s => IsBlank(s) ? 0L : Whole(s);
        if (type == typeof(ulong))
            return s => IsBlank(s) ? 0UL : ulong.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return s => IsBlank(s) ? 0f : float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return s => IsBlank(s) ? 0d : double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (type == typeof(bool))
            return s => !IsBlank(s) && (s.Trim() is "1" || s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        // MerchantEntry.NPC_Name is a JsonElement because the XLSX column is a number in some rows and text in
        // others: a numeric cell becomes a JSON number, anything else a JSON string, a blank one 0.
        if (type == typeof(JsonElement))
            return s => IsBlank(s) ? JsonDocument.Parse("0").RootElement
                : double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    ? JsonDocument.Parse(s.Trim()).RootElement
                    : JsonSerializer.SerializeToElement(s);
        throw new NotSupportedException($"typed CSV has no reader for property type {type}");
    }

    private static bool IsBlank(string s) => s.Length == 0 || s.AsSpan().Trim().IsEmpty;

    /// <summary>Integer cell; tolerates a trailing ".0" the way a spreadsheet export writes it.</summary>
    private static long Whole(string s)
    {
        s = s.Trim();
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        var d = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (d != Math.Floor(d)) throw new FormatException($"'{s}' is not a whole number");
        return checked((long)d);
    }

    /// <summary>RFC 4180 reader: quoted fields may hold commas, quotes ("") and line breaks. Blank lines are skipped.</summary>
    private static List<string[]> ReadRows(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        var sawContent = false; // a row (or at least one separator/quote) has started

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            if (sawContent || fields.Count > 0)
            {
                EndField();
                rows.Add(fields.ToArray());
            }
            fields.Clear();
            sawContent = false;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' && field.Length == 0)
            {
                sawContent = true;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    field.Append(text[i++]);
                }
            }
            else if (c == ',') { sawContent = true; EndField(); i++; }
            else if (c == '\r') { i++; }
            else if (c == '\n') { EndRow(); i++; }
            else { sawContent = true; field.Append(c); i++; }
        }
        EndRow();
        return rows;
    }
}
