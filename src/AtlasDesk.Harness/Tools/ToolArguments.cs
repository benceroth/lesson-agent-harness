using System.Text.Json;

namespace AtlasDesk.Harness.Tools;

/// <summary>Validated tool arguments. Values are only string, long or bool.</summary>
public sealed class ToolArguments
{
    private readonly SortedDictionary<string, object> _values;

    public ToolArguments(IEnumerable<KeyValuePair<string, object>> values)
    {
        _values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in values) _values[k] = v;
    }

    public IReadOnlyDictionary<string, object> Values => _values;

    public string GetString(string name) =>
        _values.TryGetValue(name, out var v) ? v.ToString() ?? "" : throw new KeyNotFoundException(name);

    public long GetLong(string name) =>
        _values.TryGetValue(name, out var v) ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : throw new KeyNotFoundException(name);

    /// <summary>Canonical JSON: keys sorted ordinally, no whitespace. Type-preserving ("1" differs from 1).</summary>
    public string ToCanonicalJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in _values)
            {
                switch (v)
                {
                    case string s: w.WriteString(k, s); break;
                    case bool b: w.WriteBoolean(k, b); break;
                    default: w.WriteNumber(k, Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)); break;
                }
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
