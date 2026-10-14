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

    [STAThread]
    static void Main()
    {
        // Single instance: WebView2 cannot share its user-data folder between processes, so a
        // second launch (double-clicked desktop icon) would fail. Focus the open window instead.
        using var mutex = new Mutex(true, @"Global\TempoOptimizerSingleInstance", out bool isFirst);
        if (!isFirst)
        {
            FocusExistingWindow();
            return;
        }

        // Global crash logging — "it glitched" must always leave a trace in %ProgramData%\Tempo\app.log
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("ui-thread", e.Exception);
            MessageBox.Show("Tempo hit an unexpected error (logged to " + Log.LogPath + "):\n\n" + e.Exception.Message,
                "Tempo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal", e.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        // remove the previous build left behind by a completed self-update
        Tempo.Core.Updates.UpdateService.CleanupPrevious();

        Log.Write("app", "started v" + Tempo.Core.Updates.UpdateService.CurrentVersion());
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
        Log.Write("app", "exited normally");
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
