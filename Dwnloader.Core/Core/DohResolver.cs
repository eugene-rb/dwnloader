using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Dwnloader.Core;

/// <summary>
/// DNS-over-HTTPS で名前を引く。
///
/// 一部のサイト（85po.com / pornhub.com / xvideos.com など）は、ISP の
/// リゾルバがシンクホールのアドレスを返したり NXDOMAIN を返したりして
/// 繋がらない。サイト側が拒んでいるわけではないので、素性の確かな
/// リゾルバに直接聞けばそのまま取得できる。
///
/// 引くのは A レコードだけにしている。シンクホールは AAAA も返してくる
/// ため、IPv6 を併用すると汚染されたアドレスを掴む経路が残る。
/// </summary>
public sealed class DohResolver : IDisposable
{
    /// <summary>
    /// 既定の問い合わせ先。ホスト名ではなく IP リテラルであることが要点で、
    /// ホスト名にすると問い合わせ先自身の名前解決を汚染されたリゾルバに
    /// 頼ることになり、塞がれている環境では最初の1回から失敗する。
    /// </summary>
    public const string DefaultEndpoint = "https://1.1.1.1/dns-query";

    private sealed record Entry(IPAddress[] Addresses, long ExpiresTicks);

    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DohResolver(string? endpoint = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();

        // 問い合わせ用のクライアントは ConnectCallback を持たない素のものにする。
        // DoH を通す側と同じ設定を使い回すと、名前解決のために自分自身を呼ぶ。
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseProxy = false,
            UseCookies = false,
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(Net.UserAgent);
    }

    /// <summary>
    /// ホスト名を IPv4 アドレスへ直す。引けなければ空を返す（呼び出し側は
    /// OS のリゾルバへ落とす）。例外は投げない。名前解決の失敗で通信全体を
    /// 止めるより、汚染されていないサイトが今まで通り通るほうが良い。
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return Array.Empty<IPAddress>();

        // 既に IP ならそのまま使う（問い合わせても意味がない）
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

        if (_cache.TryGetValue(host, out var hit) && hit.ExpiresTicks > Environment.TickCount64)
            return hit.Addresses;

        try
        {
            var url = $"{_endpoint}?name={Uri.EscapeDataString(host)}&type=A";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("accept", "application/dns-json");

            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<IPAddress>();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                                              .ConfigureAwait(false);

            var root = doc.RootElement;
            if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0)
                return Array.Empty<IPAddress>();
            if (!root.TryGetProperty("Answer", out var answer) ||
                answer.ValueKind != JsonValueKind.Array)
                return Array.Empty<IPAddress>();

            var addresses = new List<IPAddress>();
            int ttl = 300;
            foreach (var item in answer.EnumerateArray())
            {
                // 同じ応答に CNAME（type 5）が混ざる。A（type 1）だけを採る。
                if (!item.TryGetProperty("type", out var type) || type.GetInt32() != 1) continue;
                if (!item.TryGetProperty("data", out var data)) continue;
                if (IPAddress.TryParse(data.GetString(), out var ip)) addresses.Add(ip);
                if (item.TryGetProperty("TTL", out var t) && t.TryGetInt32(out var v))
                    ttl = Math.Min(ttl, v);
            }
            if (addresses.Count == 0) return Array.Empty<IPAddress>();

            // TTL をそのまま信じると数秒で失効して毎回問い合わせに行く。
            // 短すぎ・長すぎを常識的な幅へ収める。
            var lifetime = Math.Clamp(ttl, 60, 3600);
            var result = addresses.ToArray();
            _cache[host] = new Entry(result, Environment.TickCount64 + lifetime * 1000L);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 問い合わせ先に届かない・応答が壊れている。OS のリゾルバに任せる。
            return Array.Empty<IPAddress>();
        }
    }

    public void Dispose() => _client.Dispose();
}
