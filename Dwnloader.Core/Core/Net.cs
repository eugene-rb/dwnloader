using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Dwnloader.Core;

/// <summary>呼び出し間隔の下限を保証する。サイトごとに1つ持つ。</summary>
public sealed class RateLimiter
{
    private readonly double _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextTicks;

    public RateLimiter(double minIntervalSeconds) => _minInterval = minIntervalSeconds;

    public async Task WaitAsync(CancellationToken ct)
    {
        if (_minInterval <= 0) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Environment.TickCount64;
            var delay = _nextTicks - now;
            if (delay > 0)
            {
                // Task.Delay なのでスレッドを占有しない（Python 版の time.sleep と違う）
                await Task.Delay((int)delay, ct).ConfigureAwait(false);
                now = Environment.TickCount64;
            }
            _nextTicks = now + (long)(_minInterval * 1000);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>リトライして良いエラー。</summary>
public sealed class TransientException : Exception
{
    public TransientException(string message) : base(message) { }
}

/// <summary>1回の GET の結果。本文は読み終わっている。</summary>
public sealed record HttpResult(int StatusCode, byte[] Body,
                                System.Net.Http.Headers.MediaTypeHeaderValue? ContentType)
{
    public bool IsOk => StatusCode == 200 && Body.Length > 0;

    /// <summary>本文を文字列として読む。charset は Content-Type に従う。</summary>
    public string Text() => Net.DecodeText(Body, ContentType);

    public string MediaType => ContentType?.MediaType ?? "";
}

public static class Net
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // ---------------------------------------------------------------- 名前解決

    private static readonly object _dnsGate = new();
    private static DohResolver? _resolver;
    private static DohProxy? _proxy;
    private static string _dnsKey = "";

    /// <summary>DoH が有効なときの解決器。無効なら null（OS のリゾルバに任せる）。</summary>
    public static DohResolver? Resolver { get { lock (_dnsGate) return _resolver; } }

    /// <summary>
    /// yt-dlp に渡すループバックプロキシの URL。DoH が無効なら空。
    /// yt-dlp は別プロセスで自分で名前を引くため、これを経由させないと
    /// 塞がれたサイトはアプリ側だけ直っても落とせない。
    /// </summary>
    public static string DohProxyUrl { get { lock (_dnsGate) return _proxy?.Url ?? ""; } }

    /// <summary>
    /// 設定に合わせて DoH の準備をし直す。起動時と設定変更時に呼ぶ。
    /// 設定が変わっていなければ何もしない（プロキシのポートを保つ）。
    /// </summary>
    public static void ConfigureDns(SettingsData? settings)
    {
        bool enabled = settings?.UseDoh ?? false;
        var endpoint = (settings?.DohEndpoint ?? "").Trim();
        var key = enabled ? endpoint : "";

        lock (_dnsGate)
        {
            if (key == _dnsKey && (_resolver is not null) == enabled) return;
            _dnsKey = key;

            _proxy?.Dispose();
            _resolver?.Dispose();
            _proxy = null;
            _resolver = null;

            if (!enabled) return;

            _resolver = new DohResolver(endpoint);
            try
            {
                _proxy = new DohProxy(_resolver);
            }
            catch
            {
                // ループバックで待てない環境。アプリ側の解決だけ生かす。
                _proxy = null;
            }
        }
    }

    /// <summary>
    /// アプリ全体で1つの HttpClient を使い回す。作り直すとソケットを使い潰す。
    /// 個別のヘッダ（Referer など）はリクエストごとに付けるので、ここには入れない。
    /// </summary>
    public static HttpClient CreateClient(int pool = 16, SettingsData? settings = null)
    {
        var proxy = settings is null ? "" : (settings.ProxyUrl ?? "").Trim();
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(4, pool),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            UseCookies = false,             // Cookie はヘッダで明示的に渡す
        };
        if (proxy.Length > 0)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        // 接続先の IP だけを差し替える。TLS はこの後 .NET が元のホスト名で
        // 張るので、SNI と Host は正しいまま残る（curl の --resolve と同じ）。
        var resolver = Resolver;
        if (resolver is not null)
            handler.ConnectCallback = (ctx, ct) => ConnectViaDohAsync(resolver, ctx, ct);

        var client = new HttpClient(handler, disposeHandler: true);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en-US;q=0.8,en;q=0.6");
        // タイムアウトは呼び出しごとに CancellationToken で制御する
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    /// <summary>
    /// DoH で引いたアドレスへ TCP で繋ぎ、そのままのストリームを返す。
    /// 引けなければホスト名で繋ぎ直し、今まで通り OS のリゾルバに委ねる。
    /// </summary>
    private static async ValueTask<Stream> ConnectViaDohAsync(
        DohResolver resolver, SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var host = ctx.DnsEndPoint.Host;
            var port = ctx.DnsEndPoint.Port;

            var addresses = await resolver.ResolveAsync(host, ct).ConfigureAwait(false);
            if (addresses.Length > 0)
                await socket.ConnectAsync(addresses, port, ct).ConfigureAwait(false);
            else
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 一時的な失敗を指数バックオフで再試行する GET。本文まで読み切って返す。
    ///
    /// 4xx（429 を除く）は再試行せず即座に返す。呼び出し側で 404 を見て
    /// URL を作り直したいケースがあるため、例外にせず結果を返す。
    ///
    /// 本文の読み取りをこの中で済ませるのが要点。ヘッダだけ返して本文を
    /// 送り続けない相手に当たったとき、呼び出し側で読むと制限時間が
    /// 掛からず永遠に終わらない（画像取得を5並列でやると、その5本が
    /// 埋まったまま作品全体が進まなくなる）。
    /// </summary>
    public static async Task<HttpResult> GetWithRetryAsync(
        HttpClient client,
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        double timeoutSeconds = 30,
        int retries = 3,
        RateLimiter? limiter = null,
        CancellationToken ct = default)
    {
        Exception? last = null;
        int attempts = Math.Max(1, retries);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (limiter is not null) await limiter.WaitAsync(ct).ConfigureAwait(false);

            using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perRequest.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            double? retryAfter = null;

            try
            {
                // Referer などは共通ヘッダにできない（サイトごと・画像ごとに違う）ので
                // リクエスト単位で組み立てる。ここを共通化すると 403 が返る。
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(req, headers);

                using var resp = await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perRequest.Token)
                    .ConfigureAwait(false);

                int code = (int)resp.StatusCode;
                if (code == 429 || code >= 500)
                {
                    last = new TransientException($"HTTP {code}");
                    // 429 はサーバーが待ち時間を示してくることがある。無視して
                    // 短い間隔で叩き直すと、レート制限中のサイトをさらに待たせる。
                    if (code == 429 && resp.Headers.RetryAfter is { } ra)
                        retryAfter = RetryAfterSeconds(ra);
                }
                else
                {
                    // 本文にも制限時間を与える。大きな画像でも終わる長さにしつつ、
                    // 止まったままの接続はここで打ち切る。
                    perRequest.CancelAfter(TimeSpan.FromSeconds(Math.Max(timeoutSeconds * 2, 60)));
                    var body = await resp.Content.ReadAsByteArrayAsync(perRequest.Token)
                        .ConfigureAwait(false);
                    return new HttpResult(code, body, resp.Content.Headers.ContentType);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;                              // 利用者による中止は伝播させる
            }
            catch (OperationCanceledException)
            {
                last = new TransientException($"タイムアウト（{timeoutSeconds:F0}秒）");
            }
            catch (HttpRequestException e)
            {
                last = e;
            }
            // ヘッダ受信後の本文読み取り中に接続が切れると、.NET は
            // HttpRequestException ではなく IOException（の派生の HttpIOException）
            // を投げる。ここを捕まえないと、1回の瞬断で再試行もフォールバックも
            // 試さずページを丸ごと失う。
            catch (IOException e)
            {
                last = e;
            }

            if (attempt < attempts - 1)
            {
                var backoff = retryAfter ?? Math.Min(Math.Pow(2, attempt), 8);
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false);
            }
        }

        throw new TransientException($"{url} の取得に失敗しました: {last?.Message ?? "原因不明"}");
    }

    /// <summary>Retry-After ヘッダを秒数に直す。長すぎる指定は上限で切る。</summary>
    private static double? RetryAfterSeconds(System.Net.Http.Headers.RetryConditionHeaderValue value)
    {
        if (value.Delta is { } delta) return Math.Clamp(delta.TotalSeconds, 0, 30);
        if (value.Date is { } date)
        {
            var secs = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return secs > 0 ? Math.Clamp(secs, 0, 30) : null;
        }
        return null;
    }

    public static async Task<HttpResponseMessage> HeadAsync(
        HttpClient client, string url,
        IReadOnlyDictionary<string, string>? headers,
        double timeoutSeconds, CancellationToken ct)
    {
        using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perRequest.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        ApplyHeaders(req, headers);
        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead,
                                      perRequest.Token).ConfigureAwait(false);
    }

    private static void ApplyHeaders(HttpRequestMessage req,
                                     IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            if (!req.Headers.TryAddWithoutValidation(key, value))
                req.Content?.Headers.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>
    /// 本文をテキストとして読む。Content-Type に charset があればそれを使い、
    /// 無ければ UTF-8 とみなす。momon-ga のように charset を返さないサイトが
    /// あるため、既定を Latin-1 にすると日本語のタイトルが化ける。
    /// </summary>
    internal static string DecodeText(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(charset))
        {
            try
            {
                return System.Text.Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // 知らない charset 名。UTF-8 として読み直す
            }
        }
        return new UTF8Encoding_NoThrow().GetString(bytes);
    }

    /// <summary>不正なバイト列で例外にせず、置換文字で読み進める UTF-8。</summary>
    private sealed class UTF8Encoding_NoThrow : System.Text.UTF8Encoding
    {
        public UTF8Encoding_NoThrow() : base(encoderShouldEmitUTF8Identifier: false,
                                             throwOnInvalidBytes: false) { }
    }
}
