using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// hitomi.la アダプタ。
///
/// 画像URLは ltn ホストが配る gg.js のパラメータから組み立てる。この値は
/// 定期的に更新されるため、TTL 付きでキャッシュし、404 を踏んだら再取得する。
/// </summary>
public sealed partial class HitomiAdapter : SiteAdapter
{
    private const string Domain = "gold-usergeneratedcontent.net";
    private const string Ltn = "https://ltn." + Domain;

    public override string Name => "hitomi";
    public override string Label => "hitomi.la";
    public override int MaxImageWorkers => 8;

    [GeneratedRegex(@"hitomi\.la/(?:[a-z]+/)?[^/\s?#]*?(\d+)\.html", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // Python 版は re.match（先頭固定）で行を判定していた。.NET の Match は
    // 先頭固定ではないので ^ を明示する。これを落とすと行の途中の "case 12:"
    // のような文字列まで拾い、対応表が壊れる（＝全ページが取得できなくなる）。
    [GeneratedRegex(@"b\s*:\s*['""](.*?)['""]")]
    private static partial Regex GgB();
    [GeneratedRegex(@"var\s+o\s*=\s*(\d+)")]
    private static partial Regex GgDefault();
    [GeneratedRegex(@"^case\s+(\d+)\s*:")]
    private static partial Regex GgCase();
    [GeneratedRegex(@"^o\s*=\s*(\d+)\s*;\s*break")]
    private static partial Regex GgSet();

    private static readonly Gg SharedGg = new();

    /// <summary>gg.js のパラメータ（b と g→サブドメイン番号の対応表）のキャッシュ。</summary>
    private sealed class Gg
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private string _b = "";
        private Dictionary<int, int> _map = new();
        private int _default;
        private long _atTicks;

        public async Task<(string B, Dictionary<int, int> Map, int Default)> GetAsync(
            SiteContext ctx, bool force)
        {
            await _gate.WaitAsync(ctx.Cancel).ConfigureAwait(false);
            try
            {
                bool fresh = _b.Length > 0 &&
                             Environment.TickCount64 - _atTicks < Ttl.TotalMilliseconds;
                if (force || !fresh) await LoadAsync(ctx).ConfigureAwait(false);
                return (_b, _map, _default);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task LoadAsync(SiteContext ctx)
        {
            var resp = await Net.GetWithRetryAsync(
                ctx.Client, $"{Ltn}/gg.js",
                new Dictionary<string, string> { ["Referer"] = "https://hitomi.la/" },
                ctx.Timeout, ctx.Retries, null, ctx.Cancel).ConfigureAwait(false);

            if (resp.StatusCode != 200)
                throw new SiteException($"gg.js を取得できません (HTTP {resp.StatusCode})");

            var text = resp.Text();

            var mb = GgB().Match(text);
            if (!mb.Success)
                throw new SiteException("gg.js の形式が変わっています（b が見つかりません）");
            var b = mb.Groups[1].Value;

            var md = GgDefault().Match(text);
            int def = md.Success ? int.Parse(md.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

            var table = new Dictionary<int, int>();
            var pending = new List<int>();
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();

                var mc = GgCase().Match(line);
                if (mc.Success)
                {
                    pending.Add(int.Parse(mc.Groups[1].Value, CultureInfo.InvariantCulture));
                    continue;
                }

                var ms = GgSet().Match(line);
                if (ms.Success && pending.Count > 0)
                {
                    int value = int.Parse(ms.Groups[1].Value, CultureInfo.InvariantCulture);
                    foreach (var g in pending) table[g] = value;
                    pending.Clear();
                }
            }

            if (table.Count == 0)
                throw new SiteException("gg.js の形式が変わっています（対応表が空です）");

            _b = b;
            _map = table;
            _default = def;
            _atTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// ハッシュ末尾3桁から配信バケット番号を求める。
    /// 「最後の1文字」＋「その手前の2文字」を16進として読む（順序を入れ替えない）。
    /// </summary>
    private static int HashBucket(string imageHash)
    {
        var s = string.Concat(imageHash[^1], imageHash[^3..^1]);
        return int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 実物の gg.js を使って画像URLを1本組み立てる（検証用）。
    /// FetchAsync と同じ経路（Gg の読み込み → HashBucket → BuildUrl）を通るので、
    /// gg.js の書式が変わったらここで落ちる。
    /// </summary>
    internal async Task<string> BuildProbeUrlAsync(SiteContext ctx)
    {
        var (b, table, def) = await SharedGg.GetAsync(ctx, force: true).ConfigureAwait(false);
        // 実際のハッシュと同じ形（16進64桁）。末尾3桁だけが URL 組み立てに効く。
        const string sampleHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abc201";
        return BuildUrl(sampleHash, "avif", b, table, def);
    }

    public override SourceRef? Match(string url)
    {
        var m = UrlPattern().Match(url);
        if (!m.Success) return null;
        var gid = m.Groups[1].Value;
        return new SourceRef(Name, gid, $"https://hitomi.la/reader/{gid}.html");
    }

    public override async Task<GalleryMeta> FetchAsync(SourceRef reference, SiteContext ctx)
    {
        var resp = await Net.GetWithRetryAsync(
            ctx.Client, $"{Ltn}/galleries/{reference.Gid}.js",
            new Dictionary<string, string> { ["Referer"] = "https://hitomi.la/" },
            ctx.Timeout, ctx.Retries, Limiter, ctx.Cancel).ConfigureAwait(false);

        int code = resp.StatusCode;
        if (code == 404)
            throw new SiteException($"作品 {reference.Gid} は存在しません（削除された可能性があります）");
        if (code != 200)
            throw new SiteException($"作品情報を取得できません (HTTP {code})");

        var body = resp.Text();

        // `var galleryinfo = {...}` の形で返ってくる
        int eq = body.IndexOf('=');
        var payload = (eq >= 0 ? body[(eq + 1)..] : body).Trim().TrimEnd(';');

        JsonElement info;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            info = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new SiteException($"作品情報を解釈できません: {e.Message}");
        }

        var files = info.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().ToList()
            : new List<JsonElement>();
        if (files.Count == 0) throw new SiteException("ページが1枚もありません");

        var title = (Str(info, "japanese_title") is { Length: > 0 } jt ? jt
                     : Str(info, "title") is { Length: > 0 } t ? t
                     : $"hitomi-{reference.Gid}").Trim();

        var gid = Str(info, "id") is { Length: > 0 } gi ? gi : reference.Gid;

        var meta = new GalleryMeta
        {
            Site = Name,
            Gid = gid,
            Title = title,
            PageUrl = $"https://hitomi.la/reader/{reference.Gid}.html",
            Artists = Join(info, "artists", "artist"),
            Groups = Join(info, "groups", "group"),
            Series = Join(info, "parodys", "parody"),
            Characters = Join(info, "characters", "character"),
            Tags = Join(info, "tags", "tag"),
            Language = Str(info, "language_localname") is { Length: > 0 } ln ? ln : Str(info, "language"),
            Kind = Str(info, "type"),
            Published = Util.ParseDateTime(
                Str(info, "date") is { Length: > 0 } d ? d : Str(info, "datepublished")),
            Raw = files,
        };

        meta.Images = await BuildImagesAsync(files, meta.Gid, ctx, forceGg: false)
            .ConfigureAwait(false);
        return meta;
    }

    /// <summary>gg.js を強制再取得して画像URLを作り直す。</summary>
    public override async Task<bool> RefreshAsync(GalleryMeta meta, SiteContext ctx)
    {
        if (meta.Raw is not List<JsonElement> files || files.Count == 0) return false;
        try
        {
            meta.Images = await BuildImagesAsync(files, meta.Gid, ctx, forceGg: true)
                .ConfigureAwait(false);
            return true;
        }
        catch (SiteException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<ImageRef>> BuildImagesAsync(
        List<JsonElement> files, string gid, SiteContext ctx, bool forceGg)
    {
        var (b, table, def) = await SharedGg.GetAsync(ctx, forceGg).ConfigureAwait(false);

        var order = ctx.Settings.PreferFormat == "avif"
            ? new[] { "avif", "webp" }
            : new[] { "webp", "avif" };
        var referer = $"https://hitomi.la/reader/{gid}.html";

        var images = new List<ImageRef>();
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var imageHash = Str(file, "hash");
            if (imageHash.Length < 3) continue;

            // 形式は作品単位ではなくページ単位で決まる
            var available = order.Where(fmt => Truthy(file, "has" + fmt)).ToList();
            if (available.Count == 0) continue;

            var urls = available.Select(fmt => BuildUrl(imageHash, fmt, b, table, def)).ToList();
            images.Add(new ImageRef
            {
                Index = i + 1,
                Url = urls[0],
                Fmt = available[0],
                Headers = new Dictionary<string, string> { ["Referer"] = referer },
                Fallbacks = urls.Skip(1).ToList(),
            });
        }

        if (images.Count == 0) throw new SiteException("対応する形式の画像がありません");
        return images;
    }

    private static string BuildUrl(string imageHash, string fmt, string b,
                                   Dictionary<int, int> table, int def)
    {
        int bucket = HashBucket(imageHash);
        int index = table.TryGetValue(bucket, out var v) ? v : def;
        var sub = (fmt == "avif" ? "a" : "w") + (1 + index).ToString(CultureInfo.InvariantCulture);
        // gg.s(hash): 末尾3桁を16進で読んで10進表記にしたもの（bucket と同じ値）
        var shard = bucket.ToString(CultureInfo.InvariantCulture);
        return $"https://{sub}.{Domain}/{b}{shard}/{imageHash}.{fmt}";
    }

    // ------------------------------------------------------------ JSON の小道具

    private static string Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    /// <summary>hasavif などは 1 / "1" / true のいずれでも来うる。</summary>
    private static bool Truthy(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => v.GetString() is { Length: > 0 } s && s != "0",
            _ => false,
        };
    }

    /// <summary>[{"artist": "..."}] または ["..."] のどちらでも受ける。</summary>
    private static List<string> Join(JsonElement el, string arrayName, string key)
    {
        var result = new List<string>();
        if (!el.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.Object
                ? Str(item, key)
                : item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            value = value.Trim();
            if (value.Length > 0) result.Add(value);
        }
        return result;
    }
}
