using System.Collections.Concurrent;

namespace Dwnloader.Jobs;

/// <summary>
/// 同時実行数を保ちながらジョブを走らせる。
///
/// レーンごとに枠を分ける。ギャラリー（画像を多数取得してPDF化）と
/// メディア（yt-dlp）では1件あたりの重さも適正な同時数も違うので、
/// 片方が枠を埋めるともう片方が待たされてしまう。
///
/// ジョブ表はレーンをまたいで1つ。中止も削除も jobId だけで引けるので、
/// 呼び出し側がどちらのレーンかを覚えておく必要はない。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    public const string LaneGallery = "gallery";
    public const string LaneMedia = "media";

    private readonly object _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _lanes = new();
    private readonly Dictionary<string, int> _limits = new();
    private readonly ConcurrentDictionary<string, JobBase> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();

    public DownloadManager(IReadOnlyDictionary<string, int> lanes)
    {
        foreach (var (name, count) in lanes)
        {
            int n = Math.Max(1, count);
            _limits[name] = n;
            _lanes[name] = new SemaphoreSlim(n, n);
        }
    }

    /// <summary>
    /// ジョブを投入する。戻り値の Task は「順番待ちを含めて終わるまで」。
    /// 待っている間もスレッドを占有しないので、何百件積んでも枯渇しない。
    /// </summary>
    public Task SubmitAsync(JobBase job, string lane)
    {
        _jobs[job.JobId] = job;
        return Task.Run(() => RunAsync(job, lane));
    }

    private async Task RunAsync(JobBase job, string lane)
    {
        SemaphoreSlim slot;
        lock (_gate)
        {
            if (!_lanes.TryGetValue(lane, out var found))
            {
                found = new SemaphoreSlim(2, 2);
                _lanes[lane] = found;
                _limits[lane] = 2;
            }
            slot = found;
        }

        try
        {
            // 枠が空くまで待つ。中止されたら待機ごと打ち切る。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                job.Token, _shutdown.Token);
            await slot.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 走り出す前に中止された。ジョブ自身に終了を知らせて片付ける。
            job.Cancel();
            await job.RunAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await job.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            // 同時実行数を変えると別のセマフォに差し替わるが、解放は
            // 自分が取った方へ返す（取った数と返す数を一致させる）。
            slot.Release();
        }
    }

    public JobBase? Job(string jobId) => _jobs.GetValueOrDefault(jobId);

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        job.Cancel();
        return true;
    }

    public void CancelAll()
    {
        foreach (var job in _jobs.Values) job.Cancel();
    }

    public void Forget(string jobId)
    {
        if (_jobs.TryRemove(jobId, out var job)) job.DisposeCts();
    }

    /// <summary>
    /// 同時実行数を変える。走行中のジョブは今の枠のまま流し切らせ、
    /// これから走る分だけ新しい枠を使う。
    /// </summary>
    public void SetWorkers(string lane, int count)
    {
        count = Math.Max(1, count);
        lock (_gate)
        {
            if (_limits.GetValueOrDefault(lane) == count) return;
            _limits[lane] = count;
            _lanes[lane] = new SemaphoreSlim(count, count);
        }
    }

    /// <summary>
    /// 走っているジョブに中止を伝える。実行中のスレッドの完了は待たない。
    /// ffmpeg の結合など中止が効かない処理の最中に終了しようとして固まるより、
    /// アプリを閉じる操作自体が即座に返る方を優先する。
    /// </summary>
    public void Shutdown()
    {
        CancelAll();
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Shutdown();
        try { _shutdown.Dispose(); } catch (ObjectDisposedException) { }
        lock (_gate)
        {
            foreach (var s in _lanes.Values) s.Dispose();
            _lanes.Clear();
        }
    }
}

public static class TempSweeper
{
    /// <summary>これより新しい作業フォルダは触らない（誰かが使っている可能性がある）。</summary>
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 強制終了などで残った作業用ディレクトリを片付ける。
    ///
    /// 多重起動は防いでいるが、それをすり抜けた場合に動作中のダウンロードを
    /// 壊さないよう、最近更新されたフォルダは残す。掃除の失敗より、
    /// 走っている処理を消す方が害が大きい。
    /// </summary>
    public static int Sweep()
    {
        int removed = 0;
        var now = DateTime.UtcNow;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(
                         Path.GetTempPath(), GalleryJob.TmpPrefix + "*"))
            {
                try
                {
                    if (now - Directory.GetLastWriteTimeUtc(path) < MinAge) continue;
                    Directory.Delete(path, recursive: true);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
