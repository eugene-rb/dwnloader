using System.Text.Json;
using System.Text.RegularExpressions;
using Dwnloader.Auth;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// pixiv アダプタ。
///
/// 公開作品は未ログインでも ajax API から取得できる。R-18 作品は
/// PHPSESSID クッキーが必要で、うごイラ（illustType=2）は PDF 化の対象外。
/// </summary>
public sealed partial class PixivAdapter : SiteAdapter
{
    public override string Name => "pixiv";
    public override string Label => "pixiv.net";
    // pixiv は他の2サイトより明確に絞られるので控えめに
    public override int MaxImageWorkers => 2;
    protected override double MinInterval => 1.0;

    private const int TypeManga = 1;
    private const int TypeUgoira = 2;

    [GeneratedRegex(@"pixiv\.net/(?:[a-z]{2}/)?artworks/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"pixiv\.net/member_illust\.php\?[^\s]*illust_id=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyPattern();

    public override SourceRef? Match(string url)
    {
        var m = UrlPattern().Match(url);
        if (!m.Success) m = LegacyPattern().Match(url);
        if (!m.Success) return null;
        var pid = m.Groups[1].Value;
        return new SourceRef(Name, pid, $"https://www.pixiv.net/artworks/{pid}");
    }

    /// <summary>画面に出す案内。文言を1か所にまとめる。</summary>
    private const string LoginHint = "設定の「アカウント」タブから pixiv にログインしてください。";

    /// <summary>
    /// pixiv へ送る Cookie。アプリ内ログインで受け取ったものを使い、
    /// 設定に手入力があればそちらを優先する（自分で PHPSESSID を管理したい人向け）。
    ///
    /// 送るのは PHPSESSID だけに絞る。ログイン時には Cloudflare の
    /// cf_clearance なども一緒に手に入るが、あれは取得時の User-Agent と
    /// 結び付いているため、こちらの User-Agent で送り返すと逆に弾かれる。
    /// </summary>
    private static string CookieHeader(SettingsData settings)
    {
        var manual = (settings.PixivCookie ?? "").Trim();
        if (manual.Length > 0)
        {
            // 生の PHPSESSID 値でも `PHPSESSID=...` 形式でも受け付ける
            return manual.Contains('=') ? manual : $"PHPSESSID={manual}";
        }
        return CookieStore.Header(CookieStore.Pixiv, "PHPSESSID");
    }

    private async Task<JsonElement> ApiAsync(string path, SourceRef reference, SiteContext ctx)
    {
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = reference.Url,
            ["Accept"] = "application/json",
            ["X-Requested-With"] = "XMLHttpRequest",
        };

        var cookie = CookieHeader(ctx.Settings);
        if (cookie.Length > 0) headers["Cookie"] = cookie;

        var resp = await Net.GetWithRetryAsync(
            ctx.Client, $"https://www.pixiv.net/ajax/{path}", headers,
            ctx.Timeout, ctx.Retries, Limiter, ctx.Cancel).ConfigureAwait(false);

        int code = resp.StatusCode;
        if (code is 401 or 403)
            throw new AuthRequiredException("この作品にはログインが必要です。" + LoginHint);
        if (code == 404)
        {
            // R-18 作品は、未ログインだと「無い」ものとして返ってくることがある。
            // 削除済みと区別できないので、ログインしていないときだけ可能性を添える。
            throw new SiteException(
                "作品が見つかりません（非公開・削除済みの可能性があります）"
                + (cookie.Length == 0 ? "。R-18作品の場合は" + LoginHint : ""));
        }
        if (code != 200)
            throw new SiteException($"pixiv API エラー (HTTP {code})");

        var text = resp.Text();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new SiteException($"pixiv API の応答を解釈できません: {e.Message}");
        }

        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
        {
            var message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "").Trim() : "";
            if (cookie.Length == 0)
            {
                throw new AuthRequiredException(
                    $"取得できませんでした（{(message.Length > 0 ? message : "ログインが必要な作品の可能性があります")}）。"
                    + LoginHint);
            }
            throw new SiteException(message.Length > 0 ? message : "pixiv API がエラーを返しました");
        }

        return root.TryGetProperty("body", out var b) ? b : default;
    }

    public override async Task<GalleryMeta> FetchAsync(SourceRef reference, SiteContext ctx)
    {
        var info = await ApiAsync($"illust/{reference.Gid}", reference, ctx).ConfigureAwait(false);

        int illustType = info.ValueKind == JsonValueKind.Object
                         && info.TryGetProperty("illustType", out var it)
                         && it.TryGetInt32(out var itv) ? itv : -1;

        if (illustType == TypeUgoira)
            throw new UnsupportedException("うごイラはPDF化できないためスキップします");

        var pages = await ApiAsync($"illust/{reference.Gid}/pages", reference, ctx)
            .ConfigureAwait(false);
        if (pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0)
            throw new SiteException("ページ情報を取得できません");

        var images = new List<ImageRef>();
        int index = 0;
        foreach (var page in pages.EnumerateArray())
        {
            index++;
            if (!page.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Object)
                continue;

            var original = Str(urls, "original");
            if (original.Length == 0) continue;

            var ext = original[(original.LastIndexOf('.') + 1)..].ToLowerInvariant();
            var fallbacks = new[] { Str(urls, "regular"), Str(urls, "small") }
                .Where(u => u.Length > 0).ToList();

            images.Add(new ImageRef
            {
                Index = index,
                Url = original,
                Fmt = ext is "jpg" or "jpeg" or "png" or "webp" or "avif" ? ext : "jpg",
                // 画像配信は Referer を見るので必ず付ける
                Headers = new Dictionary<string, string> { ["Referer"] = "https://www.pixiv.net/" },
                Fallbacks = fallbacks,
            });
        }

        if (images.Count == 0) throw new SiteException("ダウンロードできる画像がありません");

        var tags = new List<string>();
        if (info.TryGetProperty("tags", out var tagsObj)
            && tagsObj.TryGetProperty("tags", out var tagArr)
            && tagArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagArr.EnumerateArray())
            {
                var name = Str(tag, "tag");
                if (name.Length > 0) tags.Add(name);
                if (tags.Count >= 20) break;
            }
        }

        var author = Str(info, "userName").Trim();
        var title = Str(info, "illustTitle") is { Length: > 0 } t1 ? t1
                  : Str(info, "title") is { Length: > 0 } t2 ? t2
                  : $"pixiv-{reference.Gid}";

        return new GalleryMeta
        {
            Site = Name,
            Gid = Str(info, "illustId") is { Length: > 0 } gi ? gi : reference.Gid,
            Title = title.Trim(),
            PageUrl = reference.Url,
            Images = images,
            Artists = author.Length > 0 ? new List<string> { author } : new List<string>(),
            Tags = tags,
            Language = "",
            Kind = illustType == TypeManga ? "manga" : "illust",
            Published = Util.ParseDateTime(
                Str(info, "createDate") is { Length: > 0 } cd ? cd : Str(info, "uploadDate")),
        };
    }

    private static string Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }
}
