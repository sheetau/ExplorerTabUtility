using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class UpdateManager
{
    public static async Task<(bool Available, string Message, Uri? Link)> CheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExplorerTabUtility", "3.0.0"));
        using var response = await client.GetAsync(Constants.UpdateUrl).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return (false, "No release has been published yet.", null);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 0);
        var available = Version.TryParse(tag.TrimStart('v').Split('-')[0], out var latest) && latest > current;
        var url = json.RootElement.GetProperty("html_url").GetString();
        return (available, available ? $"Version {tag} is available." : "You are up to date.", available && Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Host == "github.com" ? link : null);
    }
}
