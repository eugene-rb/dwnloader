using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dwnloader.Core;

/// <summary>
/// テキストからURLを切り出し、動画・音声として扱えるサイトかを判定する。
///
/// Python 版は yt-dlp の抽出器（約1700個）を総当りしていたが、C# からは
/// その一覧を取れない。代わりに主要サイトを正規表現で判定し、当たらない
/// URLは「汎用」として yt-dlp に賭ける（Python 版の allow_generic と同じ）。
/// 判定の役割は2つだけなので、これで振る舞いはほぼ変わらない:
///   1. カードに出す配信元の名前を決める
///   2. 既知サイトは除外リストを無視して拾う
/// </summary>
public static partial class UrlDetect
{
    /// <summary>テキストからURLらしき部分を切り出す。</summary>
    [GeneratedRegex(@"https?://[^\s<>""'\]\)]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();

    /// <summary>
    /// テキストからURLらしき部分を全部切り出す（重複除去なし）。
    /// ここは「候補を切り出す」だけの役割で、本当にURLと呼べるかは
    /// <see cref="IsUrl"/> が別途判定する。
    /// </summary>
    public static List<string> FindUrls(string? text)
    {
        text ??= "";
        var found = UrlInText().Matches(text).Select(m => m.Value).ToList();
        if (found.Count == 0 && text.Trim().Length > 0)
            found.Add(text.Trim());
        return found;
    }

    /// <summary>
    /// 文字列が実際にURLと呼べるかを判定する（FindUrls とは独立した判定）。
    /// scheme が http/https で、ホスト名がドメインらしい形を持つことを求める。
    /// </summary>
    public static bool IsUrl(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) return false;
        return host == "localhost" || host.Contains('.');
    }

    /// <summary>URL からホスト名を取り出す（小文字）。取れなければ空文字。</summary>
    public static string HostOf(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return "";
        return uri.Host.ToLowerInvariant();
    }

    // ------------------------------------------------------------ メディアサイト

    private sealed record MediaSite(string Name, Regex Pattern);

    /// <summary>
    /// 主要な動画・音声サイト。`id` という名前のグループがあればそれを識別子に使う。
    ///
    /// ID を取ることには意味がある。youtu.be/X と youtube.com/watch?v=X を
    /// 同じ作品と見なせるので、同じ動画のカードが2つ並ばない。ID を取れない
    /// サイトはURL全体のハッシュで代用する（別URLなら別扱いになる）。
    /// </summary>
    private static readonly MediaSite[] Sites =
    {
        new("youtube", Rx(@"^https?://(?:www\.|m\.|music\.)?youtube\.com/(?:watch\?(?:[^#]*&)?v=|shorts/|live/|embed/|v/)(?<id>[\w-]{6,})")),
        new("youtube", Rx(@"^https?://youtu\.be/(?<id>[\w-]{6,})")),
        new("youtube", Rx(@"^https?://(?:www\.|m\.)?youtube\.com/(?:playlist\?|@|c/|channel/|user/)")),
        new("twitter", Rx(@"^https?://(?:www\.|mobile\.)?(?:twitter|x)\.com/[^/]+/status/(?<id>\d+)")),
        new("niconico", Rx(@"^https?://(?:www\.|sp\.)?nicovideo\.jp/watch/(?<id>[a-z]{2}\d+)")),
        new("niconico", Rx(@"^https?://live\.nicovideo\.jp/watch/(?<id>lv\d+)")),
        new("bilibili", Rx(@"^https?://(?:www\.|m\.)?bilibili\.com/video/(?<id>[\w]+)")),
        new("vimeo", Rx(@"^https?://(?:www\.|player\.)?vimeo\.com/(?:video/)?(?<id>\d+)")),
        new("twitch", Rx(@"^https?://(?:www\.|m\.)?twitch\.tv/(?:videos/(?<id>\d+)|[^/]+/clip/(?<id2>[\w-]+))")),
        new("twitch", Rx(@"^https?://clips\.twitch\.tv/(?<id>[\w-]+)")),
        new("tiktok", Rx(@"^https?://(?:www\.|m\.)?tiktok\.com/@[^/]+/video/(?<id>\d+)")),
        new("tiktok", Rx(@"^https?://(?:vm|vt)\.tiktok\.com/(?<id>[\w]+)")),
        new("instagram", Rx(@"^https?://(?:www\.)?instagram\.com/(?:p|reel|reels|tv)/(?<id>[\w-]+)")),
        new("reddit", Rx(@"^https?://(?:www\.|old\.|new\.)?reddit\.com/r/[^/]+/comments/(?<id>\w+)")),
        new("soundcloud", Rx(@"^https?://(?:www\.|m\.)?soundcloud\.com/[^/]+/[^/?#]+")),
        new("dailymotion", Rx(@"^https?://(?:www\.)?dailymotion\.com/video/(?<id>[\w]+)")),
        new("bluesky", Rx(@"^https?://(?:www\.)?bsky\.app/profile/[^/]+/post/(?<id>[\w]+)")),
        new("facebook", Rx(@"^https?://(?:www\.|m\.|web\.)?facebook\.com/.+/(?:videos|watch)")),
        new("bandcamp", Rx(@"^https?://[\w-]+\.bandcamp\.com/(?:track|album)/(?<id>[\w-]+)")),
        new("nicovideo", Rx(@"^https?://seiga\.nicovideo\.jp/watch/(?<id>mg\d+)")),
        new("streamable", Rx(@"^https?://streamable\.com/(?<id>\w+)")),
        new("vk", Rx(@"^https?://(?:www\.)?vk\.com/video(?<id>-?\d+_\d+)")),
        new("odysee", Rx(@"^https?://odysee\.com/@[^/]+/(?<id>[^/?#]+)")),
        new("rumble", Rx(@"^https?://rumble\.com/(?<id>v[\w-]+)")),
        new("iwara", Rx(@"^https?://(?:www\.|ecchi\.)?iwara\.tv/videos?/(?<id>[\w-]+)")),
        new("pornhub", Rx(@"^https?://(?:[\w-]+\.)?pornhub\.com/view_video\.php\?viewkey=(?<id>\w+)")),
        new("xvideos", Rx(@"^https?://(?:www\.)?xvideos\.com/(?<id>video[\w.]+)")),
        new("fc2", Rx(@"^https?://video\.fc2\.com/(?:[a-z]{2}/)?content/(?<id>\w+)")),
        new("openrec", Rx(@"^https?://(?:www\.)?openrec\.tv/(?:live|movie)/(?<id>[\w-]+)")),
        new("mildom", Rx(@"^https?://(?:www\.)?mildom\.com/(?<id>\d+)")),
        new("spotify", Rx(@"^https?://open\.spotify\.com/(?:track|episode)/(?<id>\w+)")),
    };

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>直リンクの動画・音声ファイル。拡張子で判断する。</summary>
    [GeneratedRegex(@"\.(?:mp4|m4v|mkv|webm|mov|avi|flv|ts|m3u8|mpd|mp3|m4a|aac|flac|ogg|opus|wav)(?:$|[?#])",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DirectMediaFile();

    public sealed record MediaMatch(string Site, string Gid);

    /// <summary>
    /// 既知の動画・音声サイトに当たるかを調べる。当たらなければ null。
    /// 通信はしない（正規表現の照合だけ）。
    /// </summary>
    public static MediaMatch? MatchKnownSite(string url)
    {
        foreach (var site in Sites)
        {
            var m = site.Pattern.Match(url);
            if (!m.Success) continue;

            var id = m.Groups["id"].Success ? m.Groups["id"].Value
                   : m.Groups["id2"].Success ? m.Groups["id2"].Value
                   : "";
            return new MediaMatch(site.Name, id.Length > 0 ? id : Hashed(url));
        }

        if (DirectMediaFile().IsMatch(url))
            return new MediaMatch("file", Hashed(url));

        return null;
    }

    /// <summary>URL から短い安定した識別子を作る（IDが取れないサイト用）。</summary>
    public static string Hashed(string url)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    /// <summary>カンマ・改行区切りのドメイン一覧を集合にする。</summary>
    public static HashSet<string> ParseHostList(string? raw) =>
        (raw ?? "")
            .Replace("\r", "")
            .Replace("\n", ",")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant())
            .ToHashSet();

    /// <summary>
    /// ドメインの完全一致のみ。サブドメインは別ドメインとして区別する
    /// （docs.google.com は google.com には一致しない）。サブドメインごとに
    /// 挙動が違うことがあり、まとめて扱うと片方の成功・失敗が無関係な
    /// サブドメインへ波及してしまうため。
    /// </summary>
    public static bool HostMatches(string url, HashSet<string> hosts)
    {
        if (hosts.Count == 0) return false;
        var host = HostOf(url);
        return host.Length > 0 && hosts.Contains(host);
    }
}
