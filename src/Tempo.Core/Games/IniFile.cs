using System.Text;

namespace Tempo.Core.Games;

/// <summary>
/// Minimal INI reader/writer that preserves comments, ordering and unknown keys.
/// Game configs are hand-tuned files people care about — rewriting them wholesale
/// would destroy settings we don't understand, so edits are surgical.
/// </summary>
public class IniFile
{
    private readonly List<string> _lines;
    public string Path { get; }

    public IniFile(string path)
    {
        Path = path;
        _lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    }

    private static string SectionOf(string line) =>
        line.TrimStart().StartsWith('[') ? line.Trim().Trim('[', ']') : "";

    /// <summary>Reads a key within a section. Returns null when absent.</summary>
    public string? Get(string section, string key)
    {
        string cur = "";
        foreach (var raw in _lines)
        {
            var s = SectionOf(raw);
            if (s.Length > 0) { cur = s; continue; }
            if (!cur.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var line = raw.TrimStart();
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Sets a key in place, creating the section or key if needed.</summary>
    public void Set(string section, string key, string value)
    {
        string cur = "";
        int sectionEnd = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var s = SectionOf(_lines[i]);
            if (s.Length > 0)
            {
                if (cur.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionEnd = i;
                cur = s;
                continue;
            }
            if (!cur.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var line = _lines[i].TrimStart();
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = key + "=" + value;   // replace in place
                return;
            }
        }
        if (cur.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionEnd = _lines.Count;

        if (sectionEnd >= 0) { _lines.Insert(sectionEnd, key + "=" + value); return; }
        // section missing entirely
        if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
        _lines.Add("[" + section + "]");
        _lines.Add(key + "=" + value);
    }

    public bool HasSection(string section) =>
        _lines.Any(l => SectionOf(l).Equals(section, StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // game configs are plain ASCII/UTF-8 without BOM; adding one can break parsers
        File.WriteAllText(Path, string.Join(Environment.NewLine, _lines), new UTF8Encoding(false));
    }
}
