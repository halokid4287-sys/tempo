using Tempo.Core.Updates;

// Full apply-path test. Runs inside the disposable bin folder: this harness replaces
// ITSELF with the published Tempo.exe, proving download -> verify -> swap works.
var svc = new UpdateService();
var exe = UpdateService.ExePath();
Console.WriteLine("running as : " + exe);
Console.WriteLine("version    : " + UpdateService.CurrentVersion());

var check = await svc.CheckAsync();
Console.WriteLine($"available  : {check.Available} ({check.CurrentVersion} -> {check.LatestVersion})");
if (!check.Available) { Console.WriteLine("nothing to apply"); return 1; }

Console.WriteLine("\napplying...");
var err = await svc.DownloadAndApplyAsync(m => Console.WriteLine("   " + m));
if (err != null) { Console.WriteLine("FAILED: " + err); return 1; }

Console.WriteLine("\n--- result ---");
var old = Path.ChangeExtension(exe, null) + ".old.exe";
Console.WriteLine($"swapped file exists : {File.Exists(exe)}");
Console.WriteLine($"previous kept as    : {File.Exists(old)} ({Path.GetFileName(old)})");
var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
Console.WriteLine($"new file version    : {fvi.FileVersion}");
Console.WriteLine($"new file product    : {fvi.ProductName}");
Console.WriteLine($"new file size       : {new FileInfo(exe).Length / 1048576} MB");
Console.WriteLine($"leftover .tmp files : {Directory.GetFiles(Path.GetDirectoryName(exe)!, "*.update.tmp").Length}");

// prove CleanupPrevious removes the old copy, as it does on next startup
UpdateService.CleanupPrevious();
Console.WriteLine($"after cleanup, old removed : {!File.Exists(old)}");
return 0;
