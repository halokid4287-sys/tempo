namespace Tempo.Core.Logging;

/// <summary>Tiny crash/ops logger → %ProgramData%\Tempo\app.log (so "it glitched" is diagnosable).</summary>
public static class Log
{
    private static readonly object _lock = new();
    public static string LogPath => Path.Combine(Backup.BackupService.DataDir, "app.log");

    public static void Write(string area, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Backup.BackupService.DataDir);
                // keep the log from growing forever
                try { if (new FileInfo(LogPath).Length > 2_000_000) File.WriteAllText(LogPath, ""); } catch { }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Error(string area, Exception ex) => Write(area, "ERROR: " + ex);
}
