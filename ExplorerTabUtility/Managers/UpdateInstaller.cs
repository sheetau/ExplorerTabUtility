using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ExplorerTabUtility.Managers;

// The staged copy runs as the helper, so the portable app needs no extra updater.
public static class UpdateInstaller
{
    public static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerTabUtility", "Updates");
    private const string ManifestName = "installed-files.json";
    public sealed record InstallRequest(string TargetDirectory, int ParentId, long ParentStart, string Version);
    public static string ResolveFile(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative) ||
            !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path == root)
            throw new InvalidDataException("The update contains an unsafe path.");
        for (var parent = Path.GetDirectoryName(path); parent != null && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Update folders cannot contain links.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Update files cannot be links.");
        return path;
    }
    public static string[] ReadManifest(string directory)
    {
        var files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, ManifestName), Encoding.UTF8)) ?? [];
        if (files.Length is 0 or > 10000 || files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new InvalidDataException("The package manifest is invalid.");
        foreach (var required in new[] { "ExplorerTabUtility.exe", "ExplorerTabUtility.dll", "ExplorerTabUtility.pri", "App.xbf", "UI/MainWindow.xbf", "Assets/Icon.ico", ManifestName })
            if (!files.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("The update is missing required application files.");
        foreach (var relative in files)
            if (!File.Exists(ResolveFile(directory, relative))) throw new InvalidDataException("The update is incomplete.");
        return files;
    }
    public static async Task StartHelperAsync(PreparedUpdate update, string? targetDirectory = null, Process? parent = null)
    {
        targetDirectory ??= AppContext.BaseDirectory;
        parent ??= Process.GetCurrentProcess();
        var probe = Path.Combine(targetDirectory, ".update-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var work = Directory.GetParent(update.Directory)!.FullName;
        ValidateWorkDirectory(work);
        var requestPath = Path.Combine(work, "install.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new InstallRequest(Path.GetFullPath(targetDirectory), parent.Id, parent.StartTime.ToUniversalTime().Ticks, update.Version.ToString())), new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(update.Directory, "ExplorerTabUtility.exe")) { UseShellExecute = false, WorkingDirectory = update.Directory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(requestPath);
        using var helper = Process.Start(start) ?? throw new IOException("The update helper could not start.");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(work, "ready")))
        {
            if (helper.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new IOException("The update helper did not start. The current version is still running.");
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(work, "proceed"), "ok", new UTF8Encoding(false));
    }
    public static async Task RunHelperAsync(string requestPath)
    {
        var work = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
        ValidateWorkDirectory(work);
        var source = Path.Combine(work, "app");
        if (!PathEquals(source, AppContext.BaseDirectory)) throw new InvalidDataException("The update helper must run from its staged package.");
        var request = JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(requestPath, Encoding.UTF8)) ?? throw new InvalidDataException("The update request is missing.");
        var target = Path.GetFullPath(request.TargetDirectory);
        if (PathEquals(target, Path.GetPathRoot(target)!) || target.StartsWith(work, StringComparison.OrdinalIgnoreCase) || source.StartsWith(target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update destination is invalid.");
        var parentExited = false;
        FileUpdate? changes = null;
        try
        {
            var version = Version.Parse(request.Version);
            if (version.Major != UpdateManager.CurrentVersion.Major || version.Minor != UpdateManager.CurrentVersion.Minor || version.Build != UpdateManager.CurrentVersion.Build)
                throw new InvalidDataException("The downloaded version does not match this release.");
            ReadManifest(source);
            using (var parent = Process.GetProcessById(request.ParentId))
            {
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStart || !PathEquals(parent.MainModule!.FileName, Path.Combine(target, "ExplorerTabUtility.exe"))) throw new InvalidDataException("The running app does not match the update request.");
                File.WriteAllText(Path.Combine(work, "ready"), Environment.ProcessId.ToString(), new UTF8Encoding(false));
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                parentExited = true;
            }
            if (!File.Exists(Path.Combine(work, "proceed"))) return;
            changes = ApplyFiles(source, target, Path.Combine(work, "backup"));
            using var restarted = StartApp(target, "--updated", work);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(work, "success")))
            {
                if (restarted.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(20))
                {
                    if (!restarted.HasExited) { restarted.Kill(); await restarted.WaitForExitAsync(); }
                    throw new IOException("The new version could not start. The previous version has been restored.");
                }
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (parentExited)
            {
                changes?.Rollback();
                File.WriteAllText(Path.Combine(work, "error.txt"), ex.Message, new UTF8Encoding(false));
                using var restored = StartApp(target, "--update-error", work);
            }
            else File.WriteAllText(Path.Combine(work, "error.txt"), ex.Message, new UTF8Encoding(false));
        }
    }
    private static Process StartApp(string directory, string flag, string work)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, "ExplorerTabUtility.exe")) { UseShellExecute = false, WorkingDirectory = directory };
        start.ArgumentList.Add(flag); start.ArgumentList.Add(work); start.ArgumentList.Add("--settings");
        return Process.Start(start) ?? throw new IOException("The application could not restart.");
    }
    public static FileUpdate ApplyFiles(string source, string target, string backup)
    {
        var files = ReadManifest(source);
        var previous = File.Exists(Path.Combine(target, ManifestName)) ? ReadManifest(target) : files.Where(file => File.Exists(ResolveFile(target, file))).ToArray();
        var affected = files.Union(previous, StringComparer.OrdinalIgnoreCase).ToArray();
        var changes = new FileUpdate(target, backup, affected);
        foreach (var file in affected)
        {
            var original = ResolveFile(target, file);
            if (Directory.Exists(original)) throw new IOException("An application file has been replaced by a folder.");
            if (!File.Exists(original)) continue;
            using (File.Open(original, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
            var saved = ResolveFile(backup, file);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(original, saved);
        }
        try
        {
            foreach (var file in files)
            {
                var destination = ResolveFile(target, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(ResolveFile(source, file), destination, true);
            }
            foreach (var file in previous.Except(files, StringComparer.OrdinalIgnoreCase)) File.Delete(ResolveFile(target, file));
            return changes;
        }
        catch { changes.Rollback(); throw; }
    }
    public sealed record FileUpdate(string Target, string Backup, string[] Affected)
    {
        public void Rollback()
        {
            foreach (var file in Affected)
            {
                var original = ResolveFile(Backup, file);
                var destination = ResolveFile(Target, file);
                if (File.Exists(original)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(original, destination, true); }
                else if (File.Exists(destination)) File.Delete(destination);
            }
        }
    }
    public static void Acknowledge(string work)
    {
        ValidateWorkDirectory(work);
        File.WriteAllText(Path.Combine(work, "success"), "ok", new UTF8Encoding(false));
        CleanupAfterHelper(work);
    }
    private static void CleanupAfterHelper(string work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var helper = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(work, "ready"), Encoding.UTF8)));
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (ArgumentException) { }
            catch (TimeoutException) { return; }
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                try { DeleteWorkDirectory(work); return; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
    }
    public static string ReadError(string work)
    {
        ValidateWorkDirectory(work);
        var message = File.ReadAllText(Path.Combine(work, "error.txt"), Encoding.UTF8);
        CleanupAfterHelper(work);
        return message;
    }
    private static bool PathEquals(string first, string second) => string.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static void ValidateWorkDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd('\\');
        var cache = Path.GetFullPath(CacheDirectory).TrimEnd('\\') + "\\";
        if (!full.StartsWith(cache, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(full) + "\\", cache, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidDataException("Invalid update working directory.");
    }
    public static void DeleteWorkDirectory(string directory)
    {
        ValidateWorkDirectory(directory);
        if (Directory.Exists(directory)) Directory.Delete(Path.GetFullPath(directory), true);
    }
}
