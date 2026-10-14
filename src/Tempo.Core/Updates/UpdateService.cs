using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tempo.Core.Logging;

namespace Tempo.Core.Updates;

/// <summary>What the hosted manifest describes. Kept deliberately small and stable.</summary>
public class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("released")] public string Released { get; set; } = "";
}

/// <summary>Result handed to the UI.</summary>
public class UpdateCheckResult
{
    public bool Available { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Released { get; set; } = "";
    public string? Error { get; set; }
    public bool CanSelfUpdate { get; set; } = true;   // false when the install folder isn't writable
    public string? Blocked { get; set; }              // why self-update isn't possible
}

/// <summary>
/// Checks a hosted manifest for a newer build, then downloads, verifies and swaps the
/// running .exe in place so users never re-download manually.
///
/// Security notes (honest ones):
///  - The manifest and binary are fetched over HTTPS only.
///  - The download is verified against the SHA-256 in the manifest, which protects against
///    corrupted or truncated downloads. It does NOT protect against a compromised host,
///    since whoever controls the manifest also controls the hash. The real protections are
///    HTTPS and GitHub's own account security. Proper code signing would need a paid
///    certificate, which this project deliberately doesn't have.
/// </summary>
public class UpdateService
{
    // ---- the one place to change if hosting ever moves ----
    public const string ManifestUrl =
        "https://raw.githubusercontent.com/halokid4287-sys/tempo/main/update/latest.json";

    private const string OldSuffix = ".old.exe";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Tempo-Updater/" + CurrentVersion());
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version
                ?? typeof(UpdateService).Assembly.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Path of the running executable (the single-file host, not the extracted DLL).</summary>
    public static string ExePath()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return p;
        }
        catch { }
        return Process.GetCurrentProcess().MainModule?.FileName ?? "";
    }

    /// <summary>Deletes the previous version left behind by a completed update.</summary>
    public static void CleanupPrevious()
    {
        try
        {
            var exe = ExePath();
            if (exe.Length == 0) return;
            var old = Path.ChangeExtension(exe, null) + OldSuffix;
            if (File.Exists(old))
            {
                File.SetAttributes(old, FileAttributes.Normal);
                File.Delete(old);
                Log.Write("update", "removed previous version");
            }
            foreach (var stale in Directory.GetFiles(Path.GetDirectoryName(exe)!, "*.update.tmp"))
            { try { File.Delete(stale); } catch { } }
        }
        catch { /* a leftover file is harmless */ }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { CurrentVersion = CurrentVersion() };
        try
        {
            var json = await Http.GetStringAsync(ManifestUrl, ct);
            var m = JsonSerializer.Deserialize<UpdateManifest>(json);
            if (m == null || m.Version.Length == 0 || m.Url.Length == 0)
            { result.Error = "The update manifest is empty or malformed."; return result; }
            if (!m.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            { result.Error = "Refusing a non-HTTPS download URL."; return result; }

            result.LatestVersion = m.Version;
            result.Notes = m.Notes;
            result.Released = m.Released;
            result.Available = CompareVersions(m.Version, result.CurrentVersion) > 0;

            if (result.Available)
            {
                var block = SelfUpdateBlockedReason();
                if (block != null) { result.CanSelfUpdate = false; result.Blocked = block; }
            }
            Log.Write("update", $"checked: current={result.CurrentVersion} latest={m.Version} available={result.Available}");
        }
        catch (Exception ex)
        {
            result.Error = Friendly(ex);
            Log.Write("update", "check failed: " + ex.Message);
        }
        return result;
    }

    /// <summary>Downloads, verifies and stages the new build. Returns null on success, else an error.</summary>
    public async Task<string?> DownloadAndApplyAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var manifestJson = await Http.GetStringAsync(ManifestUrl, ct);
            var m = JsonSerializer.Deserialize<UpdateManifest>(manifestJson);
            if (m == null || m.Url.Length == 0) return "The update manifest is empty or malformed.";
            if (!m.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Refusing a non-HTTPS download URL.";

            var exe = ExePath();
            if (exe.Length == 0) return "Could not locate the running program.";
            var blocked = SelfUpdateBlockedReason();
            if (blocked != null) return blocked;

            var dir = Path.GetDirectoryName(exe)!;
            var tmp = Path.Combine(dir, "Tempo.update.tmp");

            // ---- download with progress ----
            progress?.Invoke("Downloading version " + m.Version + "…");
            using (var resp = await Http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buffer = new byte[128 * 1024];
                long done = 0; int lastPct = -5; int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct >= lastPct + 5)
                        {
                            lastPct = pct;
                            progress?.Invoke($"Downloading… {pct}%  ({done / 1048576} of {total / 1048576} MB)");
                        }
                    }
                }
            }

            // ---- verify ----
            progress?.Invoke("Verifying download…");
            if (m.Sha256.Length > 0)
            {
                var actual = await Sha256OfFileAsync(tmp, ct);
                if (!actual.Equals(m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch { }
                    Log.Write("update", $"hash mismatch: expected {m.Sha256} got {actual}");
                    return "The download didn't match its expected checksum, so it was discarded. Nothing was changed. Try again later.";
                }
            }
            else
            {
                try { File.Delete(tmp); } catch { }
                return "The update manifest has no checksum, so the download can't be verified. Update cancelled.";
            }

            // ---- swap (Windows allows renaming a RUNNING exe, not overwriting it) ----
            progress?.Invoke("Installing…");
            var old = Path.ChangeExtension(exe, null) + OldSuffix;
            try { if (File.Exists(old)) { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); } } catch { }
            File.Move(exe, old);
            try
            {
                File.Move(tmp, exe);
            }
            catch (Exception ex)
            {
                try { File.Move(old, exe); } catch { }   // put it back
                return "Could not install the update: " + ex.Message;
            }
            Log.Write("update", "installed " + m.Version + ", restarting");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            return Friendly(ex);
        }
    }

    /// <summary>Launches the freshly installed exe. Caller should exit immediately after.</summary>
    public static void RelaunchAfterUpdate()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath()) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("update", ex); }
    }

    private static string? SelfUpdateBlockedReason()
    {
        try
        {
            var exe = ExePath();
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return "Could not locate the install folder.";
            var probe = Path.Combine(dir, ".tempo-write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return null;
        }
        catch
        {
            return "Tempo can't write to its own folder, so it can't update itself here. " +
                   "Move Tempo.exe somewhere like your Desktop or Documents and try again.";
        }
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    /// <summary>Compares dotted numeric versions. Returns &gt;0 when a is newer than b.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string s) => s.Split('.', '-', '+')
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Parse(a); var y = Parse(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int xi = i < x.Length ? x[i] : 0, yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        TaskCanceledException => "The update server didn't respond in time. Check your internet connection.",
        HttpRequestException => "Couldn't reach the update server. Check your internet connection.",
        UnauthorizedAccessException => "Windows blocked writing the update. Try running Tempo again.",
        _ => ex.Message
    };
}
