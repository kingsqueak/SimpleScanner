// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Net.Http.Headers;
using System.Text.Json;

namespace SimpleScanner;

/// <summary>
/// Is there a newer SimpleScanner? GitHub's latest release, compared with this version,
/// once at start and from the link in About. Nothing is downloaded or installed, and
/// nothing is sent but the request itself.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/kingsqueak/SimpleScanner/releases";
    private const string LatestApi = "https://api.github.com/repos/kingsqueak/SimpleScanner/releases/latest";

    public sealed record Release(string Version, string Page);

    /// <summary>The latest release, or null when GitHub couldn't be reached.</summary>
    public static async Task<Release?> FetchLatestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version.Length > 0 ? AppInfo.Version : "0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));
            return Parse(doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>GitHub's answer: the tag without its v, and the release's page.</summary>
    public static Release? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String || tag.GetString() is not { Length: > 0 } t) return null;
        string page = root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
            && url.GetString() is { Length: > 0 } u && u.StartsWith("https://github.com/", StringComparison.Ordinal) ? u : ReleasesPage;
        return new Release(t.TrimStart('v', 'V'), page);
    }

    /// <summary>What a check found, in words, and the page to open when there is something there.</summary>
    public static (string Text, (string Text, string Url)? Link) Describe(Release? latest, string current)
    {
        if (latest is null)
            return ("GitHub didn't answer. Check the internet connection, or look at the Releases page.", ("Open the Releases page", ReleasesPage));
        if (!IsNewer(latest.Version, current))
            return ($"Up to date: {current} is the latest.", null);
        return ($"Version {latest.Version} is available. Download and install it from its page when you're ready; your settings and channels are kept.",
            ($"Open the {latest.Version} release page", latest.Page));
    }

    /// <summary>"0.1.10" against "0.1.9", number by number.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => [.. v.Trim().TrimStart('v', 'V').Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), out var n) ? n : 0)];
        int[] a = Parts(candidate), b = Parts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
