using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ExplorerTabUtility.Managers;

internal static class UpdateChecks
{
    private static readonly string[] Required = ["ExplorerTabUtility.exe", "ExplorerTabUtility.dll", "ExplorerTabUtility.pri", "App.xbf", "UI/MainWindow.xbf", "Assets/Icon.ico", "installed-files.json"];
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Package(string root, string text, bool obsolete = false)
    {
        foreach (var file in Required)
        {
            var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
        }
        var files = Required.ToList();
        if (obsolete) { files.Add("obsolete.txt"); File.WriteAllText(Path.Combine(root, "obsolete.txt"), "old"); }
        File.WriteAllText(Path.Combine(root, "installed-files.json"), JsonSerializer.Serialize(files));
    }
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ETU-update-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Release(string architecture, string digest, bool preview = false) => JsonSerializer.Serialize(new
            {
                draft = false, prerelease = preview, tag_name = "v3.0.2",
                assets = new[] { new { name = $"ExplorerTabUtility_v3.0.2_{architecture}_WinUI3.zip", browser_download_url = $"https://github.com/sheetau/ExplorerTabUtility/releases/download/v3.0.2/ExplorerTabUtility_v3.0.2_{architecture}_WinUI3.zip", size = 100, digest } }
            });
            var json = Release("x64", "sha256:" + new string('a', 64));
            Assert(UpdateManager.ParseRelease(json, new Version(3, 0, 1, 0), "x64").Update?.CanInstall == true, "New architecture-matched release missing");
            Assert(UpdateManager.ParseRelease(json, new Version(3, 0, 2, 0), "x64").Update == null, "Equal versions must not update");
            Assert(UpdateManager.ParseRelease(json, new Version(3, 1), "x64").Update == null, "Downgrade offered");
            Assert(UpdateManager.ParseRelease(json, new Version(3, 0, 1), "arm64").Update?.CanInstall == false, "Wrong architecture accepted");
            Assert(UpdateManager.ParseRelease(Release("x64", "invalid"), new Version(3, 0, 1), "x64").Update?.CanInstall == false, "Missing digest accepted");
            Assert(UpdateManager.ParseRelease(Release("x64", "sha256:" + new string('a', 64), true), new Version(3, 0, 1), "x64").Update == null, "Prerelease offered");
            Console.WriteLine("PASS release version/architecture/digest filtering");
            var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
            Package(source, "new"); Package(target, "old", true);
            File.WriteAllText(Path.Combine(target, "my-note.txt"), "keep");
            var transaction = UpdateInstaller.ApplyFiles(source, target, Path.Combine(root, "backup"));
            Assert(File.ReadAllText(Path.Combine(target, Required[0])) == "new" && !File.Exists(Path.Combine(target, "obsolete.txt")), "Update did not replace/prune owned files");
            Assert(File.ReadAllText(Path.Combine(target, "my-note.txt")) == "keep", "Unowned file changed");
            transaction.Rollback();
            Assert(File.ReadAllText(Path.Combine(target, Required[0])) == "old" && File.Exists(Path.Combine(target, "obsolete.txt")), "Rollback did not restore old files");
            using (File.Open(Path.Combine(target, Required[1]), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { UpdateInstaller.ApplyFiles(source, target, Path.Combine(root, "locked-backup")); throw new Exception("Locked file accepted"); }
                catch (IOException) { }
            }
            Assert(File.ReadAllText(Path.Combine(target, Required[0])) == "old", "Failed preflight changed files");
            var zipPath = Path.Combine(root, "bad.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) writer.Write("bad");
            try { UpdateManager.ExtractPackage(zipPath, Path.Combine(root, "extract")); throw new Exception("Unsafe archive accepted"); }
            catch (InvalidDataException) { }
            Assert(!File.Exists(Path.Combine(root, "escape.txt")), "Archive escaped staging");
            Console.WriteLine("PASS replace/prune/rollback, locked destination and archive path rejection");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void CopyPackage(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
    }
    public static async Task<int> VerifyRelease(string jsonPath)
    {
        try
        {
            var release = UpdateManager.ParseRelease(File.ReadAllText(jsonPath), new Version(3, 0, 0), UpdateManager.ArchitectureName).Update;
            Assert(release?.CanInstall == true, "Published release has no verified package");
            var update = await UpdateManager.DownloadAsync(release!, null, default);
            UpdateInstaller.ReadManifest(update.Directory);
            UpdateInstaller.DeleteWorkDirectory(Directory.GetParent(update.Directory)!.FullName);
            Assert((await UpdateManager.CheckAsync()).Update == null, "Current version was offered as a new update");
            Console.WriteLine("PASS live GitHub release, architecture selection, download and SHA-256 verification");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static Process? FindApp(string target)
    {
        foreach (var process in Process.GetProcessesByName("ExplorerTabUtility"))
        {
            try { if (string.Equals(process.MainModule!.FileName, Path.Combine(target, "ExplorerTabUtility.exe"), StringComparison.OrdinalIgnoreCase)) return process; }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return null;
    }
    private static async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed.TotalSeconds > 45) throw new TimeoutException("Update lifecycle did not complete"); await Task.Delay(100); }
    }
    public static async Task<int> Lifecycle(string package)
    {
        if (Process.GetProcessesByName("ExplorerTabUtility").Length != 0) { Console.Error.WriteLine("Stop other utility instances first."); return 2; }
        var root = Path.Combine(Path.GetTempPath(), "ETU-update-lifecycle-" + Guid.NewGuid().ToString("N"));
        var originalDataDirectory = Environment.GetEnvironmentVariable("EXPLORER_TAB_UTILITY_DATA_DIR");
        try
        {
            var settings = Path.Combine(root, "settings"); Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "settings.json"), JsonSerializer.Serialize(new { IsFirstRun = false, AutoUpdate = false, WindowHook = false, KeyboardHook = false, HotKeyProfiles = "[{\"Name\":\"Keep me\"}]" }));
            Environment.SetEnvironmentVariable("EXPLORER_TAB_UTILITY_DATA_DIR", settings);
            foreach (var corrupt in new[] { false, true })
            {
                var target = Path.Combine(root, corrupt ? "rollback" : "success"); CopyPackage(Path.GetFullPath(package), target);
                var work = Path.Combine(UpdateInstaller.CacheDirectory, Guid.NewGuid().ToString("N"));
                var source = Path.Combine(work, "app"); CopyPackage(Path.GetFullPath(package), source);
                if (corrupt)
                {
                    // XBF is embedded in the PRI; changing the loose copy does
                    // not break startup. Corrupt only the settings page payload,
                    // leaving the helper's Application resources usable.
                    var index = Path.Combine(source, "ExplorerTabUtility.pri");
                    var bytes = File.ReadAllBytes(index);
                    var payload = File.ReadAllBytes(Path.Combine(source, "UI/MainWindow.xbf"));
                    var offset = bytes.AsSpan().IndexOf(payload);
                    Assert(offset >= 0, "Settings XBF was not embedded in PRI");
                    bytes[offset] = 0;
                    File.WriteAllBytes(index, bytes);
                }
                File.WriteAllText(Path.Combine(target, "my-note.txt"), "keep");
                var initial = new ProcessStartInfo(Path.Combine(target, "ExplorerTabUtility.exe")) { UseShellExecute = false };
                initial.ArgumentList.Add("--settings");
                using var parent = Process.Start(initial)!;
                await Until(() => { parent.Refresh(); return parent.MainWindowHandle != 0; });
                await UpdateInstaller.StartHelperAsync(new(source, UpdateManager.CurrentVersion), target, parent);
                var exit = new ProcessStartInfo(Path.Combine(target, "ExplorerTabUtility.exe")) { UseShellExecute = false }; exit.ArgumentList.Add("--exit");
                using (var request = Process.Start(exit)!) await request.WaitForExitAsync();
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() =>
                {
                    using var app = FindApp(target);
                    return app != null && app.Id != parent.Id && app.MainWindowHandle != 0 &&
                        File.ReadAllBytes(Path.Combine(target, "ExplorerTabUtility.pri")).SequenceEqual(File.ReadAllBytes(Path.Combine(package, "ExplorerTabUtility.pri")));
                });
                Assert(File.ReadAllText(Path.Combine(target, "my-note.txt")) == "keep", "User file changed during restart");
                Assert(File.ReadAllText(Path.Combine(settings, "settings.json")).Contains("Keep me"), "Settings were not retained");
                // Wait for the updater's final acknowledgement and cache cleanup.
                await Until(() => !Directory.Exists(work));
                using (var request = Process.Start(exit)!) await request.WaitForExitAsync();
                await Until(() => { using var app = FindApp(target); return app == null; });
                Console.WriteLine(corrupt ? "PASS real helper boot-failure rollback and restart" : "PASS real helper apply, restart, settings retention and staging cleanup");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable("EXPLORER_TAB_UTILITY_DATA_DIR", originalDataDirectory);
            if (FindApp(Path.Combine(root, "success")) == null && FindApp(Path.Combine(root, "rollback")) == null && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
