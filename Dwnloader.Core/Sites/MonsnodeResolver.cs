using System.Text;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// monsnode.com の動画ページを、yt-dlp に渡せる実体URL（video.twimg.com の
/// 直リンク）へ解決する。
///
/// monsnode 自体は yt-dlp の対応外で、ページに &lt;video&gt; も og:video も無い。
/// 代わりに2段の参照をたどる:
///   1. /v&lt;n&gt; ページの左カラムに redirect.php?v=&lt;内部ID&gt; がある
///   2. twjn.php?v=&lt;内部ID&gt; が base64 で包んだ動画URLを持つ
///
/// 元ツイートを yt-dlp の Twitter 抽出器へ渡す手もあるが、monsnode の索引には
/// 既に削除済み・要ログインのツイートが多く、未ログインではほぼ失敗する
/// （2025年時点で実測。ページの「View tweet」も別ツイートを指すことがある）。
/// twimg の直リンクなら素材が生きている限り録れるので、そちらを採る。
/// </summary>
public static partial class MonsnodeResolver
{
    [GeneratedRegex(@"redirect\.php\?v=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RedirectId();

    [GeneratedRegex(@"atob\(\s*['""]([A-Za-z0-9+/=]+)['""]\s*\)")]
    private static partial Regex AtobArg();

    /// <summary>
    /// 動画ページのHTMLから内部ID（redirect.php?v=…）を取り出す。
    ///
    /// ページ下部のおすすめ欄にも同じ形のリンクが数十個並ぶので、必ず
    /// 左カラム（class="left-column" 以降の最初の1件）を採る。先頭一致で
    /// 拾うと将来レイアウトが変わったときにおすすめ欄の無関係な動画を
    /// 静かに落としかねない。
    /// </summary>
    public static string? ExtractInternalId(string pageHtml)
    {
        if (string.IsNullOrEmpty(pageHtml)) return null;

        int lc = pageHtml.IndexOf("left-column", StringComparison.OrdinalIgnoreCase);
        var region = lc >= 0 ? pageHtml[lc..] : pageHtml;

        var m = RedirectId().Match(region);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// twjn.php のHTMLから動画の直リンクを取り出す。
    ///
    /// atob('…') が2つある（動画URLと元ツイートURL）。順序に頼らず、
    /// base64 を解いて host が video.twimg.com のものを選ぶ。
    /// </summary>
    public static string? ExtractMediaUrl(string twjnHtml)
    {
        if (string.IsNullOrEmpty(twjnHtml)) return null;

        foreach (Match m in AtobArg().Matches(twjnHtml))
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            }
            catch (FormatException)
            {
                continue;
            }

            if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
                && uri.Host.Equals("video.twimg.com", StringComparison.OrdinalIgnoreCase))
            {
                return decoded;
            }
        }
        return null;
    }

    /// <summary>
    /// monsnode の動画ページURLを実体URLへ解決する。解決できなければ
    /// <see cref="SiteException"/>（利用者向けの日本語メッセージ付き）。
    /// </summary>
    public static async Task<string> ResolveAsync(
        HttpClient client, string pageUrl, SettingsData settings, CancellationToken ct)
    {
        var page = await Net.GetWithRetryAsync(
            client, pageUrl,
            new Dictionary<string, string> { ["Referer"] = "https://monsnode.com/" },
            settings.Timeout, settings.Retries, null, ct).ConfigureAwait(false);
        if (page.StatusCode != 200)
            throw new SiteException($"monsnode のページを取得できません (HTTP {page.StatusCode})");

        var internalId = ExtractInternalId(page.Text());
        if (internalId is null)
            throw new SiteException("monsnode のページ構造が変わったようです（動画リンクが見つかりません）");

        var twjn = await Net.GetWithRetryAsync(
            client, $"https://monsnode.com/twjn.php?v={internalId}",
            new Dictionary<string, string> { ["Referer"] = pageUrl },
            settings.Timeout, settings.Retries, null, ct).ConfigureAwait(false);
        if (twjn.StatusCode != 200)
            throw new SiteException($"monsnode の動画情報を取得できません (HTTP {twjn.StatusCode})");

        var media = ExtractMediaUrl(twjn.Text());
        if (media is null)
            throw new SiteException("monsnode から動画URLを取り出せませんでした");

        // 素材が生きているか先頭1バイトだけ確かめる。monsnode の索引には元ツイートが
        // 消えた項目も多く、その場合ここで 403/404 が返る。yt-dlp へ渡す前に分かれば
        // 英語のスタックトレースではなく利用者向けの説明を出せる。twimg は Range を
        // 尊重するので本文はほぼ流れない。
        var probe = await Net.GetWithRetryAsync(
            client, media,
            new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
            settings.Timeout, settings.Retries, null, ct).ConfigureAwait(false);
        if (probe.StatusCode is not (200 or 206))
            throw new SiteException(
                "動画の素材が取得できません（元ツイートが削除されたか非公開の可能性があります）");

        return media;
    }
}
