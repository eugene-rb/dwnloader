using System.Net;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// 85po の動画ページから、現在有効なトークン付き MP4 URL を取り出す。
/// yt-dlp の汎用抽出は旧 KVS の flashvars を前提にしており、現行ページの
/// video_url / video_alt_url を読めないため、ページ解析だけアプリ側で行う。
/// </summary>
public static partial class EightyFivePoResolver
{
    public sealed record Resolved(string MediaUrl, string Title, int Height);

    [GeneratedRegex(
        "[\\\"']?(?<key>video(?:_alt)?_url\\d*)[\\\"']?\\s*:\\s*['\\\"](?<url>https?://[^'\\\"]+)['\\\"]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoUrl();

    [GeneratedRegex(@"\bhref\s*=\s*['""'](?<url>[^'""']+)['""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkUrl();

    [GeneratedRegex(
        @"<meta\s+property=[""']og:title[""']\s+content=[""'](?<title>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OgTitle();

    [GeneratedRegex(@"_(?<height>\d{3,4})p\.mp4", RegexOptions.IgnoreCase)]
    private static partial Regex HeightInUrl();

    /// <summary>HTML 内の候補から、設定した上限を超えない最高画質を選ぶ。</summary>
    public static Resolved? Extract(string html, string preferredQuality, string? pageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var candidates = new List<(string Url, int Height)>();
        foreach (Match match in VideoUrl().Matches(html))
        {
            var key = match.Groups["key"].Value;
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                continue;

            int height = 0;
            var label = Regex.Match(html,
                "[\\\"']?" + Regex.Escape(key) + "_text[\\\"']?\\s*:\\s*['\\\"](?<height>\\d+)p['\\\"]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (label.Success)
                int.TryParse(label.Groups["height"].Value, out height);
            if (height <= 0 && HeightInUrl().Match(url) is { Success: true } inUrl)
                int.TryParse(inUrl.Groups["height"].Value, out height);
            if (height <= 0) height = 480;

            if (!candidates.Any(c => c.Url == url)) candidates.Add((url, height));
        }

        // 現行ページには画質別の MP4 ダウンロードリンクもある。プレーヤーの
        // JavaScript が変わった場合は、こちらから同じ実体 URL を取得する。
        if (candidates.Count == 0)
        {
            foreach (Match match in LinkUrl().Matches(html))
            {
                var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
                    && (pageUrl is null || !Uri.TryCreate(new Uri(pageUrl), href, out uri)))
                    continue;
                if (uri is null || uri.Scheme is not ("http" or "https")
                    || !uri.AbsolutePath.Contains("/get_file/", StringComparison.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    continue;

                int height = 480;
                if (HeightInUrl().Match(uri.AbsolutePath) is { Success: true } inUrl)
                    int.TryParse(inUrl.Groups["height"].Value, out height);
                if (!candidates.Any(c => c.Url == uri.AbsoluteUri))
                    candidates.Add((uri.AbsoluteUri, height));
            }
        }
        if (candidates.Count == 0) return null;

        int limit = preferredQuality switch
        {
            "1080" => 1080,
            "720" => 720,
            "480" => 480,
            _ => int.MaxValue,
        };
        var withinLimit = candidates.Where(c => c.Height <= limit).ToList();
        var selected = (withinLimit.Count > 0 ? withinLimit : candidates)
            .OrderByDescending(c => c.Height)
            .First();

        var titleMatch = OgTitle().Match(html);
        var title = titleMatch.Success
            ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value).Trim()
            : "";
        return new Resolved(selected.Url, title, selected.Height);
    }

    public static async Task<Resolved> ResolveAsync(
        HttpClient client, string pageUrl, string preferredQuality,
        SettingsData settings, CancellationToken ct)
    {
        var uri = new Uri(pageUrl);
        var origin = uri.GetLeftPart(UriPartial.Authority) + "/";
        var page = await Net.GetWithRetryAsync(
            client, pageUrl,
            new Dictionary<string, string> { ["Referer"] = origin },
            settings.Timeout, settings.Retries, null, ct).ConfigureAwait(false);
        if (page.StatusCode != 200)
            throw new SiteException($"85po のページを取得できません (HTTP {page.StatusCode})");

        return Extract(page.Text(), preferredQuality, pageUrl)
               ?? throw new SiteException(
                   "85po のページ構造が変わったようです（動画リンクが見つかりません）");
    }
}
