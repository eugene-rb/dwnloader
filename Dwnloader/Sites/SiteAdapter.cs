using Dwnloader.Core;

namespace Dwnloader.Sites;

/// <summary>このサイトから取得できなかった。</summary>
public class SiteException : Exception
{
    public SiteException(string message) : base(message) { }
}

/// <summary>ログイン情報（Cookie）が必要。</summary>
public sealed class AuthRequiredException : SiteException
{
    public AuthRequiredException(string message) : base(message) { }
}

/// <summary>PDF 化の対象外（うごイラ・動画など）。</summary>
public sealed class UnsupportedException : SiteException
{
    public UnsupportedException(string message) : base(message) { }
}

/// <summary>アダプタに渡す実行環境。</summary>
public sealed class SiteContext
{
    public required HttpClient Client { get; init; }
    public required SettingsData Settings { get; init; }
    public CancellationToken Cancel { get; init; }

    public double Timeout => Settings.Timeout;
    public int Retries => Settings.Retries;
}

/// <summary>URL の判定と作品情報の取得を担う。</summary>
public abstract class SiteAdapter
{
    public abstract string Name { get; }
    public abstract string Label { get; }

    /// <summary>画像取得の同時実行数の上限（設定値をこの値で頭打ちにする）。</summary>
    public virtual int MaxImageWorkers => 8;

    /// <summary>リクエスト間隔の下限（秒）。</summary>
    protected virtual double MinInterval => 0.0;

    private RateLimiter? _limiter;
    public RateLimiter Limiter => _limiter ??= new RateLimiter(MinInterval);

    /// <summary>URL が自サイトのものなら SourceRef を返す。</summary>
    public abstract SourceRef? Match(string url);

    /// <summary>メタ情報と全ページの取得先を組み立てる。</summary>
    public abstract Task<GalleryMeta> FetchAsync(SourceRef reference, SiteContext ctx);

    /// <summary>
    /// 画像URLが失効していた場合に作り直す。作り直したら true。
    /// hitomi のように URL の一部が定期的に更新されるサイト向け。
    /// </summary>
    public virtual Task<bool> RefreshAsync(GalleryMeta meta, SiteContext ctx) =>
        Task.FromResult(false);
}

/// <summary>サイトアダプタのレジストリ。</summary>
public static class SiteRegistry
{
    public static readonly IReadOnlyList<SiteAdapter> Adapters = new SiteAdapter[]
    {
        new HitomiAdapter(),
        new MomongaAdapter(),
        new PixivAdapter(),
    };

    public static SiteAdapter AdapterFor(string site)
    {
        foreach (var a in Adapters)
            if (a.Name == site) return a;
        throw new SiteException($"未知のサイトです: {site}");
    }

    /// <summary>1本のURLを SourceRef に解決する。対応外なら null。</summary>
    public static SourceRef? Resolve(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return null;
        foreach (var adapter in Adapters)
        {
            var reference = adapter.Match(url);
            if (reference is not null) return reference;
        }
        return null;
    }

    /// <summary>表示用のサイト名。</summary>
    public static string LabelFor(string site)
    {
        foreach (var a in Adapters)
            if (a.Name == site) return a.Label;
        return site;
    }
}
