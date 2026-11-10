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
    private sealed class Lane(int limit)
    {
        public int Limit = limit;
        public int Running;
        public LinkedList<PendingJob> Waiting { get; } = new();
    }

    private sealed class PendingJob(JobBase job)
    {
        public JobBase Job { get; } = job;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<PendingJob>? QueueNode;
    }

    private readonly Dictionary<string, Lane> _lanes = new();
    private readonly Dictionary<string, PendingJob> _pending = new();
    private readonly Dictionary<string, PendingJob> _nodes = new();
    private readonly ConcurrentDictionary<string, JobBase> _jobs = new();
    private bool _stopping;

    public DownloadManager(IReadOnlyDictionary<string, int> lanes)
    {
        foreach (var (name, count) in lanes)
        {
            _lanes[name] = new Lane(Math.Max(1, count));
        }
    }

    /// <summary>
    /// ジョブを投入する。戻り値の Task は「順番待ちを含めて終わるまで」。
    /// 待機中はキューの要素だけを保持し、実行枠の数だけ Task を起動する。
    /// </summary>
    public Task SubmitAsync(JobBase job, string lane)
    {
        var node = new PendingJob(job);
        lock (_gate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(DownloadManager));
            _jobs[job.JobId] = job;
            _nodes[job.JobId] = node;
            if (!_lanes.TryGetValue(lane, out var state))
                _lanes[lane] = state = new Lane(2);
            node.QueueNode = state.Waiting.AddLast(node);
            _pending[job.JobId] = node;
            StartReady(state);
        }
        return node.Completion.Task;
    }

    /// <summary>呼び出し元は _gate を保持する。</summary>
    private void StartReady(Lane lane)
    {
        while (!_stopping && lane.Running < lane.Limit && lane.Waiting.Count > 0)
        {
            var node = lane.Waiting.First!.Value;
            lane.Waiting.RemoveFirst();
            node.QueueNode = null;
            _pending.Remove(node.Job.JobId);
            lane.Running++;
            _ = Task.Run(() => RunAsync(node, lane));
        }
    }

    private async Task RunAsync(PendingJob node, Lane lane)
    {
        try
        {
            await node.Job.RunAsync().ConfigureAwait(false);
            node.Completion.TrySetResult();
        }
        catch (Exception e)
        {
            node.Completion.TrySetException(e);
        }
        finally
        {
            lock (_gate)
            {
                lane.Running--;
                StartReady(lane);
            }
        }
    }

    public JobBase? Job(string jobId) => _jobs.GetValueOrDefault(jobId);

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        PendingJob? queued = null;
        lock (_gate)
        {
            if (_pending.Remove(jobId, out var node))
            {
                if (node.QueueNode?.List is { } list)
                    list.Remove(node.QueueNode);
                node.QueueNode = null;
                queued = node;
            }
        }
        job.Cancel();
        if (queued is not null)
        {
            queued.Job.CancelBeforeStart();
            queued.Completion.TrySetResult();
        }
        return true;
    }

    public void CancelAll()
    {
        foreach (var id in _jobs.Keys) Cancel(id);
    }

    public void Forget(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out var job)) return;
        PendingJob? node;
        lock (_gate) _nodes.Remove(jobId, out node);
        if (node is null || node.Completion.Task.IsCompleted)
            job.DisposeCts();
        else
            _ = node.Completion.Task.ContinueWith(_ => job.DisposeCts(),
                TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// 同時実行数を変える。縮小時は実行中のジョブを止めず、空き枠が
    /// 生じるまで次のジョブを待たせる。
    /// </summary>
    public void SetWorkers(string lane, int count)
    {
        count = Math.Max(1, count);
        lock (_gate)
        {
            if (!_lanes.TryGetValue(lane, out var state))
                _lanes[lane] = state = new Lane(count);
            state.Limit = count;
            StartReady(state);
        }
    }

    /// <summary>
    /// 走っているジョブに中止を伝える。実行中のスレッドの完了は待たない。
    /// ffmpeg の結合など中止が効かない処理の最中に終了しようとして固まるより、
    /// アプリを閉じる操作自体が即座に返る方を優先する。
    /// </summary>
    public void Shutdown()
    {
        lock (_gate) _stopping = true;
        CancelAll();
    }

    public void Dispose()
    {
        Shutdown();
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
