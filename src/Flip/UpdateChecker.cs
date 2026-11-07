using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Flip;

internal sealed record ReleaseInfo(string TagName, string Name, string HtmlUrl, bool IsNewer, string CurrentVersion);

internal static class UpdateChecker
{
    const string ReleasesApi = "https://api.github.com/repos/thetinyfoundry/FLIP/releases?per_page=1";
    const string TagsApi = "https://api.github.com/repos/thetinyfoundry/FLIP/tags?per_page=1";
    internal const string ReleasesPageUrl = "https://github.com/thetinyfoundry/FLIP/releases";

    internal static string CurrentVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.5.0";
        }
    }

    internal static async Task<ReleaseInfo> CheckLatestAsync(CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Flip-Desktop-Converter/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        string tag = "";
        string name = "";
        string url = ReleasesPageUrl;

        // Try releases endpoint first
        try
        {
            using var response = await client.GetAsync(ReleasesApi, token);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var item = doc.RootElement[0];
                    tag = item.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                    name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? tag) : tag;
                    url = item.TryGetProperty("html_url", out var u) ? (u.GetString() ?? ReleasesPageUrl) : ReleasesPageUrl;
                }
            }
        }
        catch
        {
            // Fallback to tags endpoint below
        }

        // If releases didn't return a tag, try tags endpoint
        if (string.IsNullOrEmpty(tag))
        {
            using var response = await client.GetAsync(TagsApi, token);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var item = doc.RootElement[0];
                tag = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                name = tag;
                url = $"{ReleasesPageUrl}/tag/{tag}";
            }
        }

        if (string.IsNullOrEmpty(tag))
            throw new IOException("Nessuna release o tag trovato su GitHub.");

        string current = CurrentVersion;
        bool isNewer = IsRemoteNewer(current, tag);
        return new ReleaseInfo(tag, name, url, isNewer, current);
    }

    internal static bool IsRemoteNewer(string currentRaw, string remoteTagRaw)
    {
        string c = currentRaw.Trim().TrimStart('v', 'V');
        string r = remoteTagRaw.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(r) || string.Equals(c, r, StringComparison.OrdinalIgnoreCase))
            return false;

        string cCore = c.Split('-')[0];
        string rCore = r.Split('-')[0];
        if (Version.TryParse(cCore, out var vCur) && Version.TryParse(rCore, out var vRem))
        {
            if (vRem != vCur) return vRem > vCur;
            bool cPre = c.Contains('-');
            bool rPre = r.Contains('-');
            if (cPre && !rPre) return true;
            if (!cPre && rPre) return false;
            return string.Compare(r, c, StringComparison.OrdinalIgnoreCase) > 0;
        }
        return false;
    }

    internal static void OpenBrowser(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
