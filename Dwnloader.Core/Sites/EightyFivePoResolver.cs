using System.Net;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

public sealed class VideoRemovedException : SiteException
{
    public VideoRemovedException() : base("85po の動画は削除されています") { }
}

/// <summary>
/// 85po の動画ページから、現在有効なトークン付き MP4 URL を取り出す。
/// yt-dlp の汎用抽出は旧 KVS の flashvars を前提にしており、現行ページの
/// video_url / video_alt_url を読めないため、ページ解析だけアプリ側で行う。
/// </summary>
public static partial class EightyFivePoResolver
{
    public sealed record Resolved(string MediaUrl, string Title, int Height, string PageUrl);

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

    [GeneratedRegex(@"<div\b[^>]*class=[""'][^""']*\bno-player\b[^""']*[""'][^>]*>(?<message>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NoPlayer();

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
                || uri.Scheme is not ("http" or "https")
                || !IsMp4Url(uri))
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
        // JavaScript が変わった場合や .php の中継 URL しか示さない場合は、
        // こちらから実体 URL を取得する。.php を yt-dlp に渡すと安全上拒否される。
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
                    || !IsMp4Url(uri))
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
        return new Resolved(selected.Url, title, selected.Height, pageUrl ?? "");
    }

    private static bool IsMp4Url(Uri uri) =>
        uri.AbsolutePath.TrimEnd('/').EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    public static async Task<Resolved> ResolveAsync(
        HttpClient client, string pageUrl, string preferredQuality,
        SettingsData settings, CancellationToken ct)
    {
        var uri = new Uri(pageUrl);
        var page = await FetchPageAsync(client, uri, settings, ct).ConfigureAwait(false);
        // 85po.com が 403 を返しても、同じ動画が 85po.net では公開されている。
        // 動画 ID とパスを保ってミラーを試し、実際に読めたページを Referer に使う。
        if (page.StatusCode == 403 && MirrorOf(uri) is { } mirror)
        {
            uri = mirror;
            page = await FetchPageAsync(client, uri, settings, ct).ConfigureAwait(false);
        }
        if (page.StatusCode != 200)
            throw new SiteException($"85po のページを取得できません (HTTP {page.StatusCode})");

        var html = page.Text();
        var resolved = Extract(html, preferredQuality, uri.AbsoluteUri);
        if (resolved is not null) return resolved;

        var noPlayer = NoPlayer().Match(html);
        if (noPlayer.Success && WebUtility.HtmlDecode(noPlayer.Groups["message"].Value)
                .Contains("削除", StringComparison.Ordinal))
            throw new VideoRemovedException();

        throw new SiteException("85po のページ構造が変わったようです（動画リンクが見つかりません）");
    }

    /// <summary>
    /// 配信先は MP4 リンクから .php へ転送することがある。yt-dlp の拡張子保護を
    /// このサイトに限って緩める前に、転送先が実際に MP4 を返すことを確認する。
    /// </summary>
    public static async Task<bool> VerifyMp4Async(
        HttpClient client, Resolved resolved, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, resolved.MediaUrl);
        request.Headers.Referrer = new Uri(resolved.PageUrl);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SiteException($"85po の動画を取得できません (HTTP {(int)response.StatusCode})");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var header = new byte[8];
        int read = await stream.ReadAtLeastAsync(header, header.Length,
            throwOnEndOfStream: false, cancellationToken: ct).ConfigureAwait(false);
        if (read < header.Length || header[4] != (byte)'f' || header[5] != (byte)'t'
            || header[6] != (byte)'y' || header[7] != (byte)'p')
            throw new SiteException("85po の動画リンクは MP4 データを返しませんでした");

        // yt-dlp は転送先の拡張子を使う。.php でも中身が MP4 と確認できた
        // この1件だけ、呼び出し側が拡張子保護の例外を指定できるようにする。
        return response.RequestMessage?.RequestUri is { } finalUrl && !IsMp4Url(finalUrl);
    }

    private static Task<HttpResult> FetchPageAsync(
        HttpClient client, Uri uri, SettingsData settings, CancellationToken ct) =>
        Net.GetWithRetryAsync(client, uri.AbsoluteUri,
            new Dictionary<string, string>
            {
                ["Referer"] = uri.GetLeftPart(UriPartial.Authority) + "/",
            },
            settings.Timeout, settings.Retries, null, ct);

    private static Uri? MirrorOf(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var mirrorHost = host switch
        {
            "85po.com" or "www.85po.com" => "www.85po.net",
            "85po.net" or "www.85po.net" => "www.85po.com",
            "85xo.com" or "www.85xo.com" => "www.85po.net",
            _ => null,
        };
        return mirrorHost is null ? null : new UriBuilder(uri) { Host = mirrorHost }.Uri;
    }
}
