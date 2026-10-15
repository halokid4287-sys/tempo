using System.Text;

namespace Tempo.Core.Games;

/// <summary>
/// Minimal INI reader/writer that preserves comments, ordering, unknown keys, the file's
/// original text encoding and its line endings.
///
/// Game configs are files the game itself has to read back. Rewriting one in a different
/// encoding (or flipping CRLF to LF) can stop a game parsing its own settings, so edits
/// are surgical and everything else about the file is left exactly as found.
/// </summary>
public class IniFile
{
    private readonly List<string> _lines;
    private readonly Encoding _encoding;
    private readonly string _newline;
    private readonly bool _trailingNewline;

    public string Path { get; }

    public IniFile(string path)
    {
        Path = path;
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            _encoding = DetectEncoding(bytes);
            var text = _encoding.GetString(StripBom(bytes, _encoding));
            _newline = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
            _trailingNewline = text.EndsWith('\n');
            _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (_trailingNewline && _lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
        }
        else
        {
            _lines = new List<string>();
            _encoding = new UTF8Encoding(false);
            _newline = Environment.NewLine;
            _trailingNewline = true;
        }
    }

    /// <summary>Works out how the file is stored so it can be written back the same way.</summary>
    private static Encoding DetectEncoding(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(true);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return new UnicodeEncoding(false, true);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return new UnicodeEncoding(true, true);
        // UTF-16 without a BOM shows up as every other byte being zero
        int nulls = 0, checkTo = Math.Min(512, b.Length);
        for (int i = 1; i < checkTo; i += 2) if (b[i] == 0) nulls++;
        if (checkTo > 16 && nulls > checkTo / 4) return new UnicodeEncoding(false, false);
        return new UTF8Encoding(false);
    }

    private static byte[] StripBom(byte[] b, Encoding enc)
    {
        var pre = enc.GetPreamble();
        if (pre.Length == 0 || b.Length < pre.Length) return b;
        for (int i = 0; i < pre.Length; i++) if (b[i] != pre[i]) return b;
        return b.Skip(pre.Length).ToArray();
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
        bool seenSection = false;
        for (int i = 0; i < _lines.Count; i++)
        {
            var s = SectionOf(_lines[i]);
            if (s.Length > 0)
            {
                // record where our section ends, but only the first time we pass through it
                if (!seenSection && cur.Equals(section, StringComparison.OrdinalIgnoreCase))
                { sectionEnd = i; seenSection = true; }
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
                _lines[i] = key + "=" + value;   // replace in place, keeping position
                return;
            }
        }
        if (!seenSection && cur.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionEnd = _lines.Count;

        if (sectionEnd >= 0) { _lines.Insert(sectionEnd, key + "=" + value); return; }
        if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
        _lines.Add("[" + section + "]");
        _lines.Add(key + "=" + value);
    }

    public bool HasSection(string section) =>
        _lines.Any(l => SectionOf(l).Equals(section, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the file back using the encoding and line endings it already had.</summary>
    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var text = string.Join(_newline, _lines) + (_trailingNewline ? _newline : "");
        File.WriteAllBytes(Path, _encoding.GetPreamble().Concat(_encoding.GetBytes(text)).ToArray());
    }
}
