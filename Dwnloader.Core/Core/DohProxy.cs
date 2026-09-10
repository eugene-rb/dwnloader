using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dwnloader.Core;

/// <summary>
/// ループバックに立てる小さな HTTP プロキシ。名前解決だけを
/// <see cref="DohResolver"/> に肩代わりさせ、中身はそのまま素通しする。
///
/// yt-dlp は別プロセスなので、アプリ側の HttpClient に DoH を入れても
/// 効かない（yt-dlp は OS のリゾルバで自分で引く）。yt-dlp には curl の
/// --resolve に当たる指定が無いため、URL を IP に書き換える手も使えない
/// （SNI が IP になって Cloudflare に弾かれる）。
///
/// CONNECT で素のトンネルを張れば、TLS は yt-dlp と相手が端から端まで
/// 直接張る。こちらはバイト列を運ぶだけなので SNI も Host も元のまま残る。
/// </summary>
public sealed class DohProxy : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly DohResolver _resolver;
    private readonly CancellationTokenSource _stop = new();

    public int Port { get; }

    /// <summary>yt-dlp の --proxy へ渡す値。</summary>
    public string Url => $"http://127.0.0.1:{Port}";

    public DohProxy(DohResolver resolver)
    {
        _resolver = resolver;

        // 認証の無い素通しプロキシなので、必ずループバックだけに束ねる。
        // 0.0.0.0 で待つと同じ LAN の誰でも踏み台にできてしまう。
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            // 1本が失敗しても受付は続ける
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();

            var (head, leftover) = await ReadHeadAsync(stream, _stop.Token).ConfigureAwait(false);
            if (head.Length == 0) return;

            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) { await WriteStatusAsync(stream, "400 Bad Request").ConfigureAwait(false); return; }

            if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                await TunnelAsync(stream, parts[1], leftover).ConfigureAwait(false);
            else
                await ForwardAsync(stream, parts, lines, leftover).ConfigureAwait(false);
        }
        catch
        {
            // 相手が切った・名前が引けない等。接続を閉じて終わる。
        }
    }

    /// <summary>CONNECT host:port。トンネルを張って以降は素通し。</summary>
    private async Task TunnelAsync(NetworkStream client, string authority, byte[] leftover)
    {
        var (host, port) = SplitAuthority(authority, 443);

        using var upstream = await DialAsync(host, port).ConfigureAwait(false);
        if (upstream is null)
        {
            await WriteStatusAsync(client, "502 Bad Gateway").ConfigureAwait(false);
            return;
        }

        await WriteStatusAsync(client, "200 Connection Established").ConfigureAwait(false);

        var upstreamStream = upstream.GetStream();
        // ヘッダと一緒に読んでしまった分は TLS の始まり。捨てると握手が壊れる。
        if (leftover.Length > 0)
            await upstreamStream.WriteAsync(leftover, _stop.Token).ConfigureAwait(false);

        await RelayAsync(client, upstreamStream).ConfigureAwait(false);
    }

    /// <summary>
    /// 絶対形式の通常リクエスト（GET http://host/path HTTP/1.1）。
    /// http のページや http→https の入口で来る。宛先へ繋いで origin 形式へ
    /// 書き直して流す。
    /// </summary>
    private async Task ForwardAsync(NetworkStream client, string[] parts, string[] lines,
                                    byte[] leftover)
    {
        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await WriteStatusAsync(client, "400 Bad Request").ConfigureAwait(false);
            return;
        }

        using var upstream = await DialAsync(uri.Host, uri.Port).ConfigureAwait(false);
        if (upstream is null)
        {
            await WriteStatusAsync(client, "502 Bad Gateway").ConfigureAwait(false);
            return;
        }

        var version = parts.Length >= 3 ? parts[2] : "HTTP/1.1";
        var sb = new StringBuilder();
        sb.Append(parts[0]).Append(' ').Append(uri.PathAndQuery).Append(' ')
          .Append(version).Append("\r\n");

        bool hasHost = false;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();

            // 中継の都合でしか意味を持たないヘッダは落とす。keep-alive を
            // そのまま通すと、こちらは応答の終わりを判定できずに固まる。
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            sb.Append(line).Append("\r\n");
        }
        if (!hasHost) sb.Append("Host: ").Append(uri.Authority).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var upstreamStream = upstream.GetStream();
        await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _stop.Token)
                            .ConfigureAwait(false);
        if (leftover.Length > 0)
            await upstreamStream.WriteAsync(leftover, _stop.Token).ConfigureAwait(false);

        await RelayAsync(client, upstreamStream).ConfigureAwait(false);
    }

    /// <summary>DoH で引いたアドレスへ繋ぐ。引けなければ OS のリゾルバに任せる。</summary>
    private async Task<TcpClient?> DialAsync(string host, int port)
    {
        var upstream = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var addresses = await _resolver.ResolveAsync(host, timeout.Token).ConfigureAwait(false);
            if (addresses.Length > 0)
                await upstream.ConnectAsync(addresses, port, timeout.Token).ConfigureAwait(false);
            else
                await upstream.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return upstream;
        }
        catch
        {
            upstream.Dispose();
            return null;
        }
    }

    /// <summary>両方向を相手が閉じるまで運ぶ。</summary>
    private async Task RelayAsync(NetworkStream a, NetworkStream b)
    {
        var toUpstream = PumpAsync(a, b);
        var toClient = PumpAsync(b, a);
        await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read <= 0) break;
                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 相手が先に切った。もう片方も畳む。
        }
        finally
        {
            // 送る側が終わったことを伝える。これをしないと相手が応答の
            // 終わりを待ち続ける。
            try { to.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    /// <summary>
    /// ヘッダ部だけを読む。読み過ぎた分は呼び出し側へ返す。CONNECT では
    /// この読み過ぎが TLS の ClientHello の頭になるので捨ててはいけない。
    /// </summary>
    private static async Task<(string Head, byte[] Leftover)> ReadHeadAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accumulated = new List<byte>(8192);

        while (accumulated.Count < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            accumulated.AddRange(buffer.AsSpan(0, read).ToArray());

            int end = IndexOfTerminator(accumulated);
            if (end >= 0)
            {
                var head = Encoding.ASCII.GetString(CollectionsMarshalSpan(accumulated, 0, end));
                var leftover = accumulated.Skip(end + 4).ToArray();
                return (head, leftover);
            }
        }
        return ("", Array.Empty<byte>());
    }

    private static int IndexOfTerminator(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' &&
                data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static byte[] CollectionsMarshalSpan(List<byte> data, int start, int length) =>
        data.GetRange(start, length).ToArray();

    private static (string Host, int Port) SplitAuthority(string authority, int defaultPort)
    {
        int colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var port))
            return (authority[..colon].Trim('[', ']'), port);
        return (authority.Trim('[', ']'), defaultPort);
    }

    private Task WriteStatusAsync(NetworkStream stream, string status) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), _stop.Token)
              .AsTask();

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _stop.Dispose();
    }
}
