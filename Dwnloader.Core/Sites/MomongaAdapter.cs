using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// momon-ga.com アダプタ。
///
/// 作品ページの img タグに全ページの直リンクが並んでいる。ただし関連作品の
/// サムネイルも同じ形で混ざるため、作品IDで絞り込んでから連番を組み立てる。
/// </summary>
public sealed partial class MomongaAdapter : SiteAdapter
{
    public override string Name => "momonga";
    public override string Label => "momon-ga.com";
    public override int MaxImageWorkers => 5;
    protected override double MinInterval => 0.2;

    /// <summary>末尾のページが lazy-load で抜けている場合に何枚まで追試するか。</summary>
    private const int ProbeAhead = 3;

    [GeneratedRegex(@"momon-ga\.com/([a-z]+)/(mo[\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"<img[^>]+?(?:data-src|data-original|src)=['""](https?://[^'""]+?/galleries/(\d+)/(\d+)\.(\w+))['""]",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ImgPattern();

    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagStrip();

    [GeneratedRegex(@"/\d+\.(\w+)$")]
    private static partial Regex TrailingNumber();

    private static readonly HashSet<string> IndexSections = new(StringComparer.OrdinalIgnoreCase)
        { "tag", "parody", "group", "cartoonist", "character" };

    public override SourceRef? Match(string url)
    {
        var m = UrlPattern().Match(url);
        if (!m.Success) return null;

        var section = m.Groups[1].Value.ToLowerInvariant();
        var slug = m.Groups[2].Value;
        // 一覧ページは作品ではない
        if (IndexSections.Contains(section)) return null;

        return new SourceRef(Name, slug, $"https://momon-ga.com/{section}/{slug}/");
    }

    public override async Task<GalleryMeta> FetchAsync(SourceRef reference, SiteContext ctx)
    {
        var resp = await Net.GetWithRetryAsync(
            ctx.Client, reference.Url,
            new Dictionary<string, string> { ["Referer"] = "https://momon-ga.com/" },
            ctx.Timeout, ctx.Retries, Limiter, ctx.Cancel).ConfigureAwait(false);

        int code = resp.StatusCode;
        if (code == 404) throw new SiteException($"作品ページが見つかりません: {reference.Url}");
        if (code != 200) throw new SiteException($"作品ページを取得できません (HTTP {code})");

        var page = resp.Text();

        var matches = ImgPattern().Matches(page);
        if (matches.Count == 0)
            throw new SiteException("ページ画像が見つかりません（サイト構造が変わった可能性があります）");

        var slugNumber = Regex.Match(reference.Gid, @"mo(\d+)");
        var galleryId = PickGalleryId(matches, slugNumber.Success ? slugNumber.Groups[1].Value : null);

        // 同じページ番号が複数回出るので番号で一意化する
        var byIndex = new SortedDictionary<int, (string Url, string Ext)>();
        foreach (Match m in matches)
        {
            if (m.Groups[2].Value != galleryId) continue;
            int n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (!byIndex.ContainsKey(n))
                byIndex[n] = (m.Groups[1].Value, m.Groups[4].Value.ToLowerInvariant());
        }

        if (byIndex.Count == 0) throw new SiteException("ページ画像が見つかりません");

        var headers = new Dictionary<string, string> { ["Referer"] = reference.Url };
        var images = byIndex.Select(kv => new ImageRef
        {
            Index = kv.Key,
            Url = kv.Value.Url,
            Fmt = kv.Value.Ext,
            Headers = headers,
        }).ToList();

        images = await ProbeTailAsync(images, ctx, reference.Url).ConfigureAwait(false);

        var title = "";
        var mt = TitlePattern().Match(page);
        if (mt.Success) title = Text(mt.Groups[1].Value);

        return new GalleryMeta
        {
            Site = Name,
            Gid = galleryId,
            Title = title.Length > 0 ? title : $"momonga-{galleryId}",
            PageUrl = reference.Url,
            Images = images,
            Artists = Links(page, "cartoonist"),
            Groups = Links(page, "group"),
            Series = Links(page, "parody", 4),
            Characters = Links(page, "character", 8),
            Tags = Links(page, "tag", 20),
            Language = "日本語",
            Kind = reference.Url.Contains("/fanzine/") ? "doujinshi" : "magazine",
        };
    }

    /// <summary>関連作品のサムネを排除して本編の作品IDを決める。</summary>
    private static string PickGalleryId(MatchCollection matches, string? slugNumber)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in matches)
        {
            var gid = m.Groups[2].Value;
            counts[gid] = counts.GetValueOrDefault(gid) + 1;
        }

        if (slugNumber is not null && counts.ContainsKey(slugNumber)) return slugNumber;
        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>末尾が lazy-load で欠けている場合に備えて次の番号を試す。</summary>
    private async Task<List<ImageRef>> ProbeTailAsync(
        List<ImageRef> images, SiteContext ctx, string referer)
    {
        if (images.Count == 0) return images;

        // 1..N の連番になっていなければ推測が危ういので触らない
        for (int i = 0; i < images.Count; i++)
            if (images[i].Index != i + 1) return images;

        var headers = new Dictionary<string, string> { ["Referer"] = referer };
        var last = images[^1];

        for (int step = 0; step < ProbeAhead; step++)
        {
            int next = last.Index + 1;
            var url = TrailingNumber().Replace(last.Url, $"/{next}.$1");

            HttpResponseMessage resp;
            try
            {
                resp = await Net.HeadAsync(ctx.Client, url, headers, ctx.Timeout, ctx.Cancel)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ctx.Cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                break;                              // 追試は保険なので失敗しても続けない
            }

            using (resp)
            {
                var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (resp.StatusCode != HttpStatusCode.OK || !mediaType.Contains("image"))
                    break;
            }

            last = new ImageRef
            {
                Index = next,
                Url = url,
                Fmt = last.Fmt,
                Headers = headers,
            };
            images.Add(last);
        }
        return images;
    }

    private static string Text(string fragment) =>
        WebUtility.HtmlDecode(TagStrip().Replace(fragment, "")).Trim();

    private static List<string> Links(string page, string section, int limit = 12)
    {
        var pattern = $@"<a[^>]+href=['""]https?://momon-ga\.com/{Regex.Escape(section)}/[^'""]+['""][^>]*>(.*?)</a>";
        var found = Regex.Matches(page, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (Match m in found)
        {
            var value = Text(m.Groups[1].Value);
            if (value.Length > 0 && seen.Add(value)) result.Add(value);
            if (result.Count >= limit) break;
        }
        return result;
    }
}
