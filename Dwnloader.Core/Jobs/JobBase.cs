using Dwnloader.Core;

namespace Dwnloader.Jobs;

/// <summary>ジョブから外へ出す通知。UI の作りに依存しない形にしておく。</summary>
public sealed class JobEvents
{
    public Action<string, JobStatus, string>? Status { get; init; }         // id, status, message
    public Action<string, string>? Log { get; init; }                       // level, message
    public Action<string, JobMeta>? Meta { get; init; }
    public Action<string, long, long, string>? Progress { get; init; }      // id, done, total, detail
    /// <summary>実際に受け取ったバイト数（増分）。速度の計測に使う。</summary>
    public Action<long>? Bytes { get; init; }
    public Action<string, byte[]>? Thumbnail { get; init; }
    public Action<string, JobResult>? Finished { get; init; }
}

public sealed class JobMeta
{
    public string Title { get; init; } = "";
    /// <summary>動画側は組み立て済みの説明文を送る。null ならギャラリー流に組み立てる。</summary>
    public string? Subtitle { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Series { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public int Pages { get; init; }
}

public sealed class JobResult
{
    public bool Ok { get; init; }
    public string Path { get; init; } = "";
    public string Error { get; init; } = "";
    public int Missing { get; init; }
    /// <summary>
    /// 待っても状況が変わらない失敗（ログイン要求・非対応種別など）。
    /// true なら自動再試行の対象から外す。
    /// </summary>
    public bool Permanent { get; init; }
}

/// <summary>ダウンロード1件。ギャラリーもメディアも同じ形にして、管理側から区別せず扱う。</summary>
public abstract class JobBase
{
    protected JobBase(string jobId, SourceRef reference, SettingsData settings, JobEvents events)
    {
        JobId = jobId;
        Reference = reference;
        Settings = settings;
        Events = events;
    }

    public string JobId { get; }
    public SourceRef Reference { get; }
    protected SettingsData Settings { get; }
    protected JobEvents Events { get; }

    private readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;
    public bool IsCanceled => _cts.IsCancellationRequested;

    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public abstract Task RunAsync();

    // ------------------------------------------------------------ 通知の小道具

    protected void SetStatus(JobStatus status, string message = "")
        => Events.Status?.Invoke(JobId, status, message);

    protected void Log(string level, string message)
        => Events.Log?.Invoke(level, message);

    protected void Finish(bool ok, string path, string error, int missing = 0, bool permanent = false)
        => Events.Finished?.Invoke(JobId, new JobResult
        {
            Ok = ok, Path = path, Error = error, Missing = missing, Permanent = permanent,
        });

    public void DisposeCts()
    {
        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { }
    }
}
