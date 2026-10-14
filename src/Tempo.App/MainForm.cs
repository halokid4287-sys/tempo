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

            // Serve the UI straight out of the .exe. The previous approach extracted the files
            // to AppData and used a virtual-host folder mapping, but WebView2's renderer runs in
            // a locked-down AppContainer that can't traverse into that folder, so every page load
            // failed with ERR_ACCESS_DENIED. Answering the requests in-process sidesteps file
            // permissions, elevation and antivirus entirely, and leaves nothing on disk.
            core.AddWebResourceRequestedFilter("https://app.tempo/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => ServeEmbedded(env, e);

            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) Tempo.Core.Logging.Log.Write("ui", "interface loaded");
                else Tempo.Core.Logging.Log.Write("ui", "interface FAILED to load: " + e.WebErrorStatus);
            };

            _bridge = new Bridge(this, core);
            core.WebMessageReceived += _bridge.OnWebMessage;

            core.Navigate("https://app.tempo/index.html");
            RemoveLegacyUiFolder();
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
    /// Answers https://app.tempo/* from resources embedded in the executable.
    /// Nothing is written to disk, so there are no file permissions to get wrong.
    /// </summary>
    private static void ServeEmbedded(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var path = new Uri(e.Request.Uri).AbsolutePath.Trim('/');
            if (path.Length == 0) path = "index.html";

            var asm = typeof(MainForm).Assembly;
            using var src = asm.GetManifestResourceStream("ui/" + path);
            if (src == null)
            {
                Tempo.Core.Logging.Log.Write("ui", "404 " + path);
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            // CreateWebResourceResponse needs a stream it can read after this handler returns
            var ms = new MemoryStream();
            src.CopyTo(ms);
            ms.Position = 0;
            e.Response = env.CreateWebResourceResponse(ms, 200, "OK",
                "Content-Type: " + MimeFor(path) + "\r\nCache-Control: no-cache");
        }
        catch (Exception ex)
        {
            Tempo.Core.Logging.Log.Error("ui", ex);
            try { e.Response = env.CreateWebResourceResponse(null, 500, "Server Error", ""); } catch { }
        }
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    /// <summary>Removes the folder the old extract-to-disk approach left behind.</summary>
    private static void RemoveLegacyUiFolder()
    {
        try
        {
            var old = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tempo", "ui");
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        catch { }
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
