using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>
/// gofile.io の公開共有フォルダーを、Webクライアントと同じ認証で展開する。
/// 通常のAPIトークンだけでは一覧APIが Premium 限定扱いになるため、
/// ゲストアカウントと時間窓付き X-Website-Token を組み合わせる。
/// </summary>
public static partial class GoFileResolver
{
    private const string ApiOrigin = "https://api.gofile.io";
    private const string Origin = "https://gofile.io";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private const string Language = "en-US";
    private const string WebsiteSalt = "12af056dacea0b";
    private const long WindowSeconds = 14400;
    private const int PageSize = 100;
    private const int MaxItems = 5000;

    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string _guestToken = "";
    private static long _tokenTicks;

    public sealed record ShareRef(string Code, string PageUrl);
    public sealed record Item(string Id, string Name, string Link);

    public static ShareRef? Match(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (uri.Host is not ("gofile.io" or "www.gofile.io")) return null;
        var match = SharePathRegex().Match(uri.AbsolutePath);
        if (!match.Success) return null;
        var code = match.Groups["id"].Value;
        return new ShareRef(code, $"https://gofile.io/d/{code}");
    }

    public static async Task<IReadOnlyList<Item>> ExpandAsync(
        HttpClient client, string url, bool all, SettingsData settings, CancellationToken ct)
    {
        var share = Match(url) ?? throw new SiteException("対応していないGoFile URLです");
        var token = await GuestTokenAsync(client, settings, ct).ConfigureAwait(false);
        var result = new List<Item>();
        var folders = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        folders.Enqueue(share.Code);

        while (folders.Count > 0 && result.Count < MaxItems)
        {
            var folder = folders.Dequeue();
            if (!visited.Add(folder)) continue;

            int page = 1;
            while (result.Count < MaxItems)
            {
                using var response = await GetContentsAsync(
                    client, folder, page, token, settings, ct).ConfigureAwait(false);
                foreach (var child in Children(response.Data))
                {
                    var type = Text(child, "type");
                    var id = Text(child, "id");
                    if (type == "folder")
                    {
                        if (id.Length > 0) folders.Enqueue(id);
                        continue;
                    }

                    var link = Text(child, "link");
                    if (id.Length == 0 || link.Length == 0) continue;
                    result.Add(new Item(id, Text(child, "name", fallback: id), link));
                    if (!all) return result;
                    if (result.Count >= MaxItems) break;
                }

                if (!response.HasNextPage) break;
                page++;
            }
        }

        if (result.Count == 0) throw new SiteException("GoFile共有フォルダーにファイルがありません");
        return result;
    }

    /// <summary>保存URLへ送る accountToken Cookie の値を返す。</summary>
    public static Task<string> DownloadTokenAsync(
        HttpClient client, SettingsData settings, CancellationToken ct) =>
        GuestTokenAsync(client, settings, ct);

    private sealed record ContentsResponse(JsonElement Data, bool HasNextPage, JsonDocument Document)
        : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private static async Task<ContentsResponse> GetContentsAsync(
        HttpClient client, string contentId, int page, string token,
        SettingsData settings, CancellationToken ct)
    {
        Exception? last = null;
        // 4時間の境界直後はサーバー側の時計との差を考慮して前の時間窓も試す。
        foreach (var windowOffset in new[] { 0, -1 })
        {
            var url = $"{ApiOrigin}/contents/{Uri.EscapeDataString(contentId)}" +
                      $"?contentFilter=&page={page}&pageSize={PageSize}" +
                      "&sortField=createTime&sortDirection=-1";
            var headers = ApiHeaders(token, windowOffset);
            var result = await Net.GetWithRetryAsync(client, url, headers,
                timeoutSeconds: Math.Max(45, settings.Timeout),
                retries: settings.Retries, ct: ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                last = new TransientException($"GoFile API: HTTP {result.StatusCode}");
                continue;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(result.Body); }
            catch (JsonException e) { throw new SiteException($"GoFile APIの応答を解析できません: {e.Message}"); }

            var root = doc.RootElement;
            var status = Text(root, "status");
            if (status == "ok")
            {
                var data = Property(root, "data");
                var metadata = Property(root, "metadata");
                bool hasNext = Bool(metadata, "hasNextPage");
                return new ContentsResponse(data, hasNext, doc);
            }

            doc.Dispose();
            if (status == "error-notPremium")
            {
                last = new SiteException(
                    "GoFileのWebサイトトークンが拒否されました。サイト側の更新後はアプリの更新が必要です");
                continue;
            }
            if (status == "error-passwordRequired")
                throw new AuthRequiredException("パスワード付きGoFileフォルダーにはまだ対応していません");
            if (status == "error-notFound")
                throw new SiteException("GoFileの共有フォルダーが見つかりません（削除済みの可能性があります）");
            if (status == "error-rateLimit")
                throw new TransientException("GoFileのアクセス制限に達しました。しばらく待って再試行してください");
            throw new SiteException($"GoFile APIエラー: {status}");
        }

        if (last is TransientException transient) throw transient;
        throw last as SiteException ?? new SiteException("GoFileの一覧を取得できませんでした");
    }

    private static async Task<string> GuestTokenAsync(
        HttpClient client, SettingsData settings, CancellationToken ct)
    {
        if (_guestToken.Length > 0 && Environment.TickCount64 - _tokenTicks < 30 * 60 * 1000)
            return _guestToken;

        await TokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_guestToken.Length > 0 && Environment.TickCount64 - _tokenTicks < 30 * 60 * 1000)
                return _guestToken;

            Exception? last = null;
            int attempts = Math.Max(1, settings.Retries);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.Timeout)));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, ApiOrigin + "/accounts");
                    request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                    request.Headers.TryAddWithoutValidation("Origin", Origin);
                    request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    if (Text(doc.RootElement, "status") == "ok")
                    {
                        var token = Text(Property(doc.RootElement, "data"), "token");
                        if (token.Length > 0)
                        {
                            _guestToken = token;
                            _tokenTicks = Environment.TickCount64;
                            return token;
                        }
                    }
                    last = new SiteException("GoFileのゲストトークンを取得できませんでした");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
                {
                    last = e;
                }
            }
            throw new TransientException(
                last is OperationCanceledException
                    ? "GoFile APIへの接続がタイムアウトしました"
                    : $"GoFile APIへ接続できません: {last?.Message}");
        }
        finally
        {
            TokenGate.Release();
        }
    }

    internal static string WebsiteTokenForTest(
        string accountToken, long unixSeconds, int windowOffset = 0)
    {
        long window = unixSeconds / WindowSeconds + windowOffset;
        var raw = $"{UserAgent}::{Language}::{accountToken}::{window}::{WebsiteSalt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static Dictionary<string, string> ApiHeaders(string token, int windowOffset)
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token,
            ["X-Website-Token"] = WebsiteTokenForTest(token, unix, windowOffset),
            ["X-BL"] = Language,
            ["User-Agent"] = UserAgent,
            ["Accept"] = "*/*",
            ["Origin"] = Origin,
            ["Referer"] = Origin + "/",
        };
    }

    private static IEnumerable<JsonElement> Children(JsonElement data)
    {
        var children = Property(data, "children", "contents");
        if (children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) yield return child;
        else if (children.ValueKind == JsonValueKind.Object)
            foreach (var child in children.EnumerateObject()) yield return child.Value;
    }

    private static JsonElement Property(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var found)) return found;
        return default;
    }

    private static string Text(JsonElement value, string name, string fallback = "")
    {
        var found = Property(value, name);
        if (found.ValueKind == JsonValueKind.String) return found.GetString()?.Trim() ?? fallback;
        return found.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? found.ToString() : fallback;
    }

    private static bool Bool(JsonElement value, string name)
    {
        var found = Property(value, name);
        return found.ValueKind == JsonValueKind.True ||
               found.ValueKind == JsonValueKind.String &&
               bool.TryParse(found.GetString(), out var parsed) && parsed;
    }

    [GeneratedRegex(@"^/d/(?<id>[A-Za-z0-9-]{4,80})/?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SharePathRegex();
}
