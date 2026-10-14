using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Tempo.App;

public class MainForm : Form
{
    private readonly WebView2 _web = new();
    private Bridge? _bridge;

    public MainForm()
    {
        Text = "Tempo";
        MinimumSize = new Size(1080, 680);
        Size = new Size(1340, 840);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 13, 18);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(10, 13, 18);
        Controls.Add(_web);

        HandleCreated += (_, _) => EnableDarkTitleBar();
        Load += async (_, _) => await InitWebView();
    }

    private async Task InitWebView()
    {
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tempo");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(appData, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            var uiDir = ExtractUi(Path.Combine(appData, "ui"));
            core.SetVirtualHostNameToFolderMapping("app.tempo", uiDir, CoreWebView2HostResourceAccessKind.Allow);

            _bridge = new Bridge(this, core);
            core.WebMessageReceived += _bridge.OnWebMessage;

            core.Navigate("https://app.tempo/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Tempo.Core.Logging.Log.Write("startup", "WebView2 runtime missing");
            var r = MessageBox.Show(
                "Tempo needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.\n\n" +
                "It is a free Microsoft component (already included in Windows 11 and most Windows 10 installs).\n\n" +
                "Open Microsoft's download page now?",
                "Tempo — one-time setup needed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
                }
                catch { }
            }
            Close();
        }
        catch (Exception ex)
        {
            Tempo.Core.Logging.Log.Error("startup", ex);
            MessageBox.Show("Tempo could not start its interface:\n\n" + ex.Message +
                "\n\nDetails were written to:\n" + Tempo.Core.Logging.Log.LogPath,
                "Tempo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>
    /// Writes the embedded UI files to disk so WebView2 can serve them. Keeps the app a single
    /// shareable .exe while still using a plain folder mapping. Refreshed on every launch.
    /// </summary>
    private static string ExtractUi(string uiDir)
    {
        var asm = typeof(MainForm).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/", StringComparison.Ordinal)) continue;
            var dest = Path.Combine(uiDir, name["ui/".Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var outFile = File.Create(dest);
            src.CopyTo(outFile);
        }
        return uiDir;
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _bridge?.Dispose();
        base.OnFormClosed(e);
    }
}
