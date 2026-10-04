using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// Vilolo 系の短縮動画ページを実体の HLS URL へ解決する。
/// gofile.party と cdn.twimg-media.com は、同じ公開 API とデータ形式を使っている。
/// </summary>
public static partial class ViloloResolver
{
    private const string FallbackApiOrigin = "https://rwzugqnp.fun800.click";
    private const int PageSize = 20;
    private const int MaxItems = 5000;

    private static readonly HashSet<string> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "gofile.party",
        "gofile.run",
        "gofile.host",
        "cdn.twimg-media.com",
    };

    private static readonly ConcurrentDictionary<string, string> ApiOrigins =
        new(StringComparer.OrdinalIgnoreCase);

    public sealed record ShareRef(string Host, string ShortLink, string PageUrl);

    public sealed record Item(string Id, string Name, string MediaUrl,
                              string LandingPage, bool IsFolder);

    public static ShareRef? Match(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !KnownHosts.Contains(uri.Host)) return null;

        var shortLink = uri.AbsolutePath.Trim('/');
        if (!ShortLinkRegex().IsMatch(shortLink)) return null;
        return new ShareRef(uri.Host.ToLowerInvariant(), shortLink,
                            $"https://{uri.Host.ToLowerInvariant()}/{shortLink}");
    }

    /// <summary>
    /// 単体ページなら1件、フォルダーなら中の動画を再帰的に返す。
    /// all=false では最初に見つかった動画だけを返す。
    /// </summary>
    public static async Task<IReadOnlyList<Item>> ExpandAsync(
        HttpClient client, string url, bool all, SettingsData settings, CancellationToken ct)
    {
        var root = Match(url) ?? throw new SiteException("対応していない共有URLです");
        var apiOrigin = await ApiOriginAsync(client, root, settings, ct).ConfigureAwait(false);
        var first = await GetInfoAsync(client, apiOrigin, root, settings, ct).ConfigureAwait(false);
        if (!first.IsFolder && (!all || first.ListShortLink.Length == 0 ||
                                first.ListShortLink == root.ShortLink))
            return new[] { first.Item };

        var result = new List<Item>();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        // 単体動画ページにも、画面下部の一覧を指すチャンネル用IDがある。
        // ページ自身の短縮IDで一覧APIを呼ぶと0件になるため、extraInfo.externalLinksを使う。
        pending.Enqueue(first.IsFolder ? root.ShortLink : first.ListShortLink);

        while (pending.Count > 0 && result.Count < MaxItems)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Dequeue();
            if (!visited.Add(folder)) continue;

            int page = 1;
            int total = int.MaxValue;
            int loadedFromFolder = 0;
            while (loadedFromFolder < total && result.Count < MaxItems)
            {
                var batch = await ListPageAsync(client, apiOrigin, root.Host, folder,
                    page, first.SortOrder, settings, ct).ConfigureAwait(false);
                total = batch.Total;
                if (batch.Items.Count == 0) break;
                loadedFromFolder += batch.Items.Count;

                foreach (var item in batch.Items)
                {
                    if (item.IsFolder)
                    {
                        if (item.LandingPage.Length > 0) pending.Enqueue(item.LandingPage);
                        continue;
                    }
                    if (item.MediaUrl.Length == 0) continue;
                    result.Add(item);
                    if (!all) return result;
                    if (result.Count >= MaxItems) break;
                }
                page++;
            }
        }

        // チャンネル一覧が一時的に空でも、開いた単体動画そのものは取得できる。
        if (result.Count == 0 && !first.IsFolder) return new[] { first.Item };
        if (result.Count == 0) throw new SiteException("共有フォルダーに動画がありません");
        return result;
    }

    public static async Task<Item> ResolveAsync(
        HttpClient client, string url, SettingsData settings, CancellationToken ct)
    {
        var reference = Match(url) ?? throw new SiteException("対応していない共有URLです");
        var apiOrigin = await ApiOriginAsync(client, reference, settings, ct).ConfigureAwait(false);
        var item = await GetInfoAsync(client, apiOrigin, reference, settings, ct).ConfigureAwait(false);
        if (item.IsFolder) throw new SiteException("共有フォルダーは追加時に展開してください");
        if (item.Item.MediaUrl.Length == 0) throw new SiteException("動画の実体URLが見つかりません");
        return item.Item;
    }

    private sealed record InfoItem(Item Item, string SortOrder, string ListShortLink)
    {
        public bool IsFolder => Item.IsFolder;
        public static implicit operator Item(InfoItem value) => value.Item;
    }

    private sealed record ListPage(IReadOnlyList<Item> Items, int Total);

    private static async Task<InfoItem> GetInfoAsync(
        HttpClient client, string apiOrigin, ShareRef reference,
        SettingsData settings, CancellationToken ct)
    {
        var endpoint = ApiUrl(apiOrigin, "/flow/land-page/getInfo", new()
        {
            ["externalLinks"] = reference.ShortLink,
            ["domain"] = reference.Host,
        });
        using var doc = await GetJsonAsync(client, endpoint, settings, ct).ConfigureAwait(false);
        var data = Data(doc.RootElement);
        var info = Property(data, "info");
        var disk = Property(info, "netDiskInfo", "netdiskInfo", "fileInfo");
        var extra = Property(info, "extraInfo", "extra");

        var item = new Item(
            Text(disk, "guid", "id", "fileId", "landingPage", fallback: reference.ShortLink),
            Text(disk, "name", "title", "fileName", fallback: reference.ShortLink),
            Text(disk, "fileUrl", "originUrl", "m3u8Url"),
            Text(disk, "landingPage", fallback: reference.ShortLink),
            Bool(disk, "isFolder"));
        return new InfoItem(item,
            Text(extra, "sortOrder", fallback: "2"),
            Text(extra, "externalLinks", "shortLink"));
    }

    private static async Task<ListPage> ListPageAsync(
        HttpClient client, string apiOrigin, string host, string shortLink,
        int page, string sortOrder, SettingsData settings, CancellationToken ct)
    {
        var endpoint = ApiUrl(apiOrigin, "/flow/land-page/list_by_links_page", new()
        {
            ["externalLinks"] = shortLink,
            ["domain"] = host,
            ["pageNo"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pageSize"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sortOrder"] = sortOrder,
        });
        using var doc = await GetJsonAsync(client, endpoint, settings, ct).ConfigureAwait(false);
        var data = Data(doc.RootElement);
        var list = Property(data, "list", "records", "rows");
        var items = new List<Item>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in list.EnumerateArray())
            {
                var disk = Property(value, "netDiskInfo", "fileInfo");
                if (disk.ValueKind != JsonValueKind.Object) disk = value;
                var landing = Text(disk, "landingPage", "path", "shortLink", "externalLinks");
                items.Add(new Item(
                    Text(disk, "guid", "id", "fileId", fallback: landing),
                    Text(disk, "name", "title", "fileName", fallback: landing),
                    Text(disk, "m3u8Url", "fileUrl", "originUrl"),
                    landing,
                    Bool(disk, "isFolder")));
            }
        }
        return new ListPage(items, Number(data, "total", "totalCount", "count"));
    }

    private static async Task<string> ApiOriginAsync(
        HttpClient client, ShareRef reference, SettingsData settings, CancellationToken ct)
    {
        if (ApiOrigins.TryGetValue(reference.Host, out var cached)) return cached;

        // 配信先はリリースごとに変わり得るため、ページが読み込む JS から取得する。
        var page = await GetTextAsync(client, reference.PageUrl, settings, ct).ConfigureAwait(false);
        var script = ModuleScriptRegex().Match(page).Groups["src"].Value;
        if (script.Length > 0)
        {
            var scriptUrl = new Uri(new Uri(reference.PageUrl), script).AbsoluteUri;
            var javascript = await GetTextAsync(client, scriptUrl, settings, ct).ConfigureAwait(false);
            var match = ApiOriginRegex().Match(javascript);
            if (match.Success)
            {
                var found = match.Groups["origin"].Value;
                ApiOrigins[reference.Host] = found;
                return found;
            }
        }

        ApiOrigins[reference.Host] = FallbackApiOrigin;
        return FallbackApiOrigin;
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string url, SettingsData settings, CancellationToken ct)
    {
        var result = await Net.GetWithRetryAsync(client, url, timeoutSeconds: settings.Timeout,
            retries: settings.Retries, ct: ct).ConfigureAwait(false);
        if (!result.IsOk) throw new TransientException($"共有動画API: HTTP {result.StatusCode}");
        try
        {
            var doc = JsonDocument.Parse(result.Body);
            if (Number(doc.RootElement, "code") != 0)
            {
                doc.Dispose();
                throw new SiteException("共有動画APIがエラーを返しました");
            }
            return doc;
        }
        catch (JsonException e)
        {
            throw new SiteException($"共有動画APIの応答を解析できません: {e.Message}");
        }
    }

    private static async Task<string> GetTextAsync(
        HttpClient client, string url, SettingsData settings, CancellationToken ct)
    {
        var result = await Net.GetWithRetryAsync(client, url, timeoutSeconds: settings.Timeout,
            retries: settings.Retries, ct: ct).ConfigureAwait(false);
        if (!result.IsOk) throw new TransientException($"共有ページ: HTTP {result.StatusCode}");
        return result.Text();
    }

    private static string ApiUrl(string origin, string path, Dictionary<string, string> query)
    {
        var parts = query.Select(x =>
            $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}");
        return $"{origin.TrimEnd('/')}/app-api{path}?{string.Join("&", parts)}";
    }

    private static JsonElement Data(JsonElement root)
    {
        var data = Property(root, "data");
        var nested = Property(data, "data");
        return nested.ValueKind == JsonValueKind.Undefined ? data : nested;
    }

    private static JsonElement Property(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var found)) return found;
        return default;
    }

    private static string Text(JsonElement value, string name1, string? name2 = null,
                               string? name3 = null, string? name4 = null, string fallback = "")
    {
        foreach (var name in new[] { name1, name2, name3, name4 })
        {
            if (name is null || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(name, out var found)) continue;
            var text = found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return fallback;
    }

    private static bool Bool(JsonElement value, string name)
    {
        var found = Property(value, name);
        return found.ValueKind == JsonValueKind.True ||
               found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var n) && n == 1 ||
               found.ValueKind == JsonValueKind.String &&
               (found.GetString() == "1" || bool.TryParse(found.GetString(), out var b) && b);
    }

    private static int Number(JsonElement value, params string[] names)
    {
        var found = Property(value, names);
        if (found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var number)) return number;
        return found.ValueKind == JsonValueKind.String && int.TryParse(found.GetString(), out number)
            ? number : 0;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{2,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortLinkRegex();

    [GeneratedRegex("<script[^>]+type=[\\\"']module[\\\"'][^>]+src=[\\\"'](?<src>[^\\\"']+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModuleScriptRegex();

    [GeneratedRegex(@"return\s+\w+\(`(?<origin>https://[A-Za-z0-9.-]+)`\s*,globalThis\.window",
                    RegexOptions.CultureInvariant)]
    private static partial Regex ApiOriginRegex();
}
