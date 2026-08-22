using System.IO;
using System.IO.Pipes;

namespace Dwnloader;

/// <summary>
/// 多重起動の防止。
///
/// クリップボードを監視する常駐ツールなので、2つ動くと同じURLを両方が拾って
/// 同じ作品を二重にダウンロードする。さらに起動時の一時フォルダ掃除が、
/// 先に動いている側の作業中フォルダまで消してしまう。
/// 2つ目は起動せず、動いている側のウィンドウを前に出す。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\dwnloader2-single-instance";
    private const string PipeName = "dwnloader2-show";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private Action? _onShow;

    public bool IsPrimary { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        IsPrimary = created;

        if (!IsPrimary)
        {
            // 自分は二番手。掴んでいない Mutex を持ち続けない。
            try { _mutex.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>先に動いている側で、合図を受ける口を開く。</summary>
    public void Listen(Action onShow)
    {
        _onShow = onShow;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var payload = await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false);
                if (payload.Contains("show", StringComparison.Ordinal)) _onShow?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // パイプの一時的な失敗で常駐を終わらせない
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    /// <summary>先行インスタンスへ「窓を出せ」と伝える。</summary>
    public static bool Signal()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.Write("show");
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch (Exception) { }
            try { _mutex.Dispose(); } catch (Exception) { }
        }
        try { _stop.Dispose(); } catch (ObjectDisposedException) { }
    }
}
