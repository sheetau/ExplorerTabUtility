using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public sealed record ReleaseUpdate(Version Version, Uri Page, Uri? Download, string? Sha256, long Size)
{
    public bool CanInstall => Download != null && Sha256?.Length == 64 && Size > 0;
}
public sealed record UpdateCheck(string Message, ReleaseUpdate? Update);
public sealed record PreparedUpdate(string Directory, Version Version);

public static class UpdateManager
{
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 0, 1, 0);
    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new PlatformNotSupportedException()
    };
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExplorerTabUtility", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
    public static async Task<UpdateCheck> CheckAsync(CancellationToken cancellation = default)
    {
        using var client = CreateClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(Constants.UpdateUrl, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new("No release has been published yet.", null);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false), CurrentVersion, ArchitectureName);
    }
    public static UpdateCheck ParseRelease(string jsonText, Version current, string architecture)
    {
        using var json = JsonDocument.Parse(jsonText);
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return new("You are up to date.", null);
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var latest) || Normalize(latest) <= Normalize(current)) return new("You are up to date.", null);
        var page = new Uri($"https://github.com/sheetau/ExplorerTabUtility/releases/tag/{Uri.EscapeDataString(tag)}");
        var expected = $"ExplorerTabUtility_{tag}_{architecture}_WinUI3.zip";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expected) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            var size = asset.GetProperty("size").GetInt64();
            var prefix = $"https://github.com/sheetau/ExplorerTabUtility/releases/download/{Uri.EscapeDataString(tag)}/";
            if (url == prefix + expected && digest?.StartsWith("sha256:", StringComparison.Ordinal) == true &&
                digest.Length == 71 && digest[7..].All(Uri.IsHexDigit) && size is > 0 and <= 536870912)
                return new($"Version {tag} is available.", new(latest, page, new Uri(url), digest[7..], size));
        }
        return new($"Version {tag} is available. Download it from the release page.", new(latest, page, null, null, 0));
    }
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static async Task<PreparedUpdate> DownloadAsync(ReleaseUpdate release, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (!release.CanInstall) throw new InvalidOperationException("This release has no verified package for this device.");
        var work = Path.Combine(UpdateInstaller.CacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, "package.zip");
            using var client = CreateClient();
            using var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("The update is larger than its published size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    progress?.Report((double)total / release.Size);
                }
                if (total != release.Size) throw new InvalidDataException("The update download is incomplete.");
            }
            await using (var stream = File.OpenRead(archive))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
                if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update checksum does not match GitHub.");
            }
            var source = Path.Combine(work, "app");
            ExtractPackage(archive, source);
            UpdateInstaller.ReadManifest(source);
            File.Delete(archive);
            return new(source, release.Version);
        }
        catch { UpdateInstaller.DeleteWorkDirectory(work); throw; }
    }
    public static void ExtractPackage(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(entry => entry.Length) > 2147483648L) throw new InvalidDataException("The update package is too large.");
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var target = UpdateInstaller.ResolveFile(destination, entry.FullName.TrimEnd('/', '\\'));
            if (!names.Add(target) || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000) throw new InvalidDataException("The update contains an invalid file entry.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target); }
        }
    }
}
