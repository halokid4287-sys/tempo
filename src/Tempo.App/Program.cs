using System.Diagnostics;
using System.Runtime.InteropServices;
using Tempo.Core.Logging;

namespace Tempo.App;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    private const int SW_RESTORE = 9;

    private const string MutexName = @"Global\TempoOptimizerSingleInstance";
    /// <summary>Flag passed to the freshly installed build so it waits for us to let go.</summary>
    public const string UpdatedFlag = "--updated";

    private static Mutex? _instanceLock;

    /// <summary>
    /// Hands the single-instance lock to the incoming build during a self-update.
    /// Without this the new process would see "already running", focus our window and quit,
    /// so the update would install but never actually start.
    /// </summary>
    public static void ReleaseInstanceLock()
    {
        try { _instanceLock?.ReleaseMutex(); } catch { }
        try { _instanceLock?.Dispose(); } catch { }
        _instanceLock = null;
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool afterUpdate = args.Any(a => a.Equals(UpdatedFlag, StringComparison.OrdinalIgnoreCase));

        // Single instance: WebView2 cannot share its user-data folder between processes, so a
        // second launch (double-clicked desktop icon) would fail. Focus the open window instead.
        _instanceLock = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst && afterUpdate)
        {
            // We were started BY the outgoing build; give it a moment to exit and release.
            try { isFirst = _instanceLock.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { isFirst = true; }   // it died holding the lock
        }
        if (!isFirst)
        {
            FocusExistingWindow();
            return;
        }

        // remove the previous build left behind by a completed self-update
        Tempo.Core.Updates.UpdateService.CleanupPrevious();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("ui-thread", e.Exception);
            MessageBox.Show("Tempo hit an unexpected error (logged to " + Log.LogPath + "):\n\n" + e.Exception.Message,
                "Tempo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal", e.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        Log.Write("app", "started v" + Tempo.Core.Updates.UpdateService.CurrentVersion()
                         + (afterUpdate ? " (after update)" : ""));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
        Log.Write("app", "exited normally");
        ReleaseInstanceLock();
    }

    private static void FocusExistingWindow()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch { }
    }
}
