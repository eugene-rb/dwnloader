namespace Dwnloader.Core;

/// <summary>ジョブの状態。表示名は <see cref="StatusText.Label"/> で引く。</summary>
public enum JobStatus
{
    Queued,
    Fetching,
    Downloading,
    Building,       // PDF 生成中
    Processing,     // yt-dlp の後処理（結合・音声抽出）
    Done,
    Partial,        // 一部ページが取得できなかった
    Failed,
    Canceled,
    Skipped,        // 履歴にあるので取得しなかった
}

public static class StatusText
{
    public static string Label(JobStatus s) => s switch
    {
        JobStatus.Queued => "待機中",
        JobStatus.Fetching => "情報取得中",
        JobStatus.Downloading => "取得中",
        JobStatus.Building => "PDF生成中",
        JobStatus.Processing => "処理中",
        JobStatus.Done => "完了",
        JobStatus.Partial => "一部失敗",
        JobStatus.Failed => "失敗",
        JobStatus.Canceled => "キャンセル",
        JobStatus.Skipped => "取得済み",
        _ => s.ToString(),
    };

    /// <summary>進行中とみなす状態。Python 版の ACTIVE と同じ。</summary>
    public static bool IsActive(JobStatus s) =>
        s is JobStatus.Queued or JobStatus.Fetching or JobStatus.Downloading
          or JobStatus.Building or JobStatus.Processing;

    /// <summary>「まだ片付いていない」状態。再試行の対象。</summary>
    public static bool IsUnfinished(JobStatus s) =>
        s is JobStatus.Failed or JobStatus.Partial or JobStatus.Canceled;

    /// <summary>復元対象として保存する状態。完了済みは履歴が持つので保存しない。</summary>
    public static bool ShouldPersist(JobStatus s) => IsActive(s) || IsUnfinished(s);
}

/// <summary>取得する種別。空文字はギャラリー（PDF化）。</summary>
public static class MediaKind
{
    public const string None = "";
    public const string Video = "video";
    public const string Audio = "audio";

    public static bool IsMedia(string kind) => kind is Video or Audio;

    public static string Label(string kind) => kind switch
    {
        Video => "動画",
        Audio => "音声",
        _ => "",
    };

    /// <summary>保存されるものの呼び名。ギャラリーは PDF。</summary>
    public static string ResultLabel(string kind) => IsMedia(kind) ? Label(kind) : "PDF";

    public static string Normalize(string? kind) =>
        kind is Video or Audio ? kind : Video;
}

/// <summary>URL から解決した「どのサイトのどの作品か」。キューの同一性判定に使う。</summary>
public sealed record SourceRef(string Site, string Gid, string Url, string Kind = MediaKind.None)
{
    /// <summary>
    /// 同じ動画を「動画として」と「音声として」別々に持てるよう Kind を含める。
    /// Python 版の SourceRef.key と同じ書式（履歴・キューの互換性のため変えない）。
    /// </summary>
    public string Key => string.IsNullOrEmpty(Kind)
        ? $"{Site}:{Gid}"
        : $"{Site}:{Kind}:{Gid}";
}

/// <summary>1ページ分の画像の取得先。</summary>
public sealed class ImageRef
{
    public int Index { get; init; }              // 1 始まりのページ番号
    public string Url { get; init; } = "";
    public string Fmt { get; init; } = "";       // "avif" / "webp" / "jpg" / "png"
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();
    public IReadOnlyList<string> Fallbacks { get; init; } = Array.Empty<string>();
}

/// <summary>作品1件のメタ情報と全ページの取得先。</summary>
public sealed class GalleryMeta
{
    public string Site { get; init; } = "";
    public string Gid { get; init; } = "";
    public string Title { get; init; } = "";
    public string PageUrl { get; init; } = "";

    /// <summary>
    /// アダプタが URL を作り直す（refresh）と差し替わる。並列取得中の
    /// スレッドが常に最新を読めるよう、参照ごと差し替える運用にする。
    /// </summary>
    public volatile IReadOnlyList<ImageRef> Images = Array.Empty<ImageRef>();

    public IReadOnlyList<string> Artists { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Series { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string Language { get; init; } = "";
    public string Kind { get; init; } = "";      // doujinshi / manga / illust など

    /// <summary>作品の公開日。PDF の作成日時に使う。取れないサイトもある。</summary>
    public DateTime? Published { get; init; }

    /// <summary>アダプタが refresh() で画像URLを組み直すために保持する生データ。</summary>
    public object? Raw { get; set; }
}
