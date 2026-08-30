using System.Text.RegularExpressions;

namespace Dwnloader.Core;

/// <summary>
/// 保存済みファイルの索引。
///
/// 履歴（history.json）だけを見ていると、記録を消した・別の環境で落とした・
/// 手で整理した、といった場合に同じものをもう一度落としてしまう。実際に
/// ディスクにあるファイルからも作品を特定して、重複を防ぐ。
///
/// 手掛かりはファイル名の末尾に入る `(サイト-ID)`。保存名のテンプレートにも
/// yt-dlp の出力名にも同じ形で入っている。
/// </summary>
public sealed partial class LibraryIndex
{
    /// <summary>1回の走査で見るファイル数の上限。巨大なフォルダで固まらないようにする。</summary>
    private const int MaxFiles = 200_000;

    /// <summary>ID だけで同一とみなす最短の長さ。短いIDは別サイトと衝突しうる。</summary>
    private const int LooseIdMinLength = 8;

    private readonly object _gate = new();
    // "pdf|hitomi|1234567" → パス（サイトまで一致）
    private Dictionary<string, string> _strict = new(StringComparer.Ordinal);
    // "video|dqw4w9wgxcq" → パス（IDだけ一致。長いIDに限る）
    private Dictionary<string, string> _loose = new(StringComparer.Ordinal);

    public int Count { get { lock (_gate) return _strict.Count; } }

    /// <summary>直近の走査で見たフォルダ数と、打ち切ったかどうか。</summary>
    public int ScannedRoots { get; private set; }
    public bool Truncated { get; private set; }

    // ファイル名の末尾に置かれる (サイト-ID)。題名にも括弧が来うるので、
    // 後ろから見て最初に「-」を含むものを採る。
    [GeneratedRegex(@"\(([^()]+)\)")]
    private static partial Regex ParenGroup();

    private static readonly HashSet<string> PdfExt = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".flv", ".ts", ".m4v", ".mpg", ".mpeg" };
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".opus", ".aac", ".flac", ".ogg", ".wav", ".weba", ".oga" };

    /// <summary>
    /// 拡張子から種別を決める。動画として落としたものと音声として落としたものは
    /// ファイル名が同じになりうるので、ここで区別しないと取り違える。
    /// </summary>
    private static string? CategoryOf(string extension)
    {
        if (PdfExt.Contains(extension)) return "pdf";
        if (VideoExt.Contains(extension)) return "video";
        if (AudioExt.Contains(extension)) return "audio";
        return null;                    // 対象外の拡張子は索引に入れない
    }

    private static string CategoryFor(string kind) => kind switch
    {
        MediaKind.Video => "video",
        MediaKind.Audio => "audio",
        _ => "pdf",
    };

    /// <summary>
    /// ファイル名から (サイト, ID) を取り出す。見つからなければ null。
    ///
    /// 「(2)」のような連番の括弧は飛ばし、後ろから見て最初に「-」を含む
    /// 括弧を採る。サイト名に「-」は使っていないので、最初の「-」で切る
    /// （YouTube の ID には「-」が入りうるため、後ろで切ってはいけない）。
    /// </summary>
    internal static (string Site, string Id)? ParseStem(string stem)
    {
        var groups = ParenGroup().Matches(stem);
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            var inner = groups[i].Groups[1].Value;
            int dash = inner.IndexOf('-');
            if (dash <= 0 || dash >= inner.Length - 1) continue;

            var site = inner[..dash].Trim();
            var id = inner[(dash + 1)..].Trim();
            if (site.Length == 0 || id.Length == 0) continue;
            return (site, id);
        }
        return null;
    }

    private static string StrictKey(string category, string site, string id) =>
        $"{category}|{site.ToLowerInvariant()}|{id.ToLowerInvariant()}";

    private static string LooseKey(string category, string id) =>
        $"{category}|{id.ToLowerInvariant()}";

    /// <summary>
    /// この作品のファイルが既にあれば、その場所を返す。
    ///
    /// nameHint は、こちらの付けた ID と保存名の ID が一致しない場合の逃げ道。
    /// 直リンクの動画では、こちらはURLのハッシュを ID にするが、yt-dlp は
    /// URL 末尾のファイル名を ID にするため、そのままでは突き合わせられない。
    /// </summary>
    public bool TryFind(SourceRef reference, out string path, string? nameHint = null)
    {
        var category = CategoryFor(reference.Kind);
        lock (_gate)
        {
            if (_strict.TryGetValue(StrictKey(category, reference.Site, reference.Gid), out path!))
                return true;

            // サイト名の呼び方が違っても（yt-dlp は "Youtube"、こちらは "youtube"
            // など）、IDが十分に長ければ同じ作品とみなしてよい。
            if (reference.Gid.Length >= LooseIdMinLength
                && _loose.TryGetValue(LooseKey(category, reference.Gid), out path!))
                return true;

            // 短い手掛かりで当てにいくと、別物を「取得済み」と誤判定する
            // （どこにでもある video.mp4 など）。長いものだけ使う。
            if (nameHint is { Length: >= LooseIdMinLength }
                && _loose.TryGetValue(LooseKey(category, nameHint), out path!))
                return true;
        }
        path = "";
        return false;
    }

    /// <summary>
    /// URL 末尾のファイル名（拡張子なし）。直リンクのとき、yt-dlp が付ける
    /// ID と同じ形になるので、突き合わせの手掛かりに使える。
    /// </summary>
    public static string NameHintFromUrl(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return "";
        var last = uri.AbsolutePath.TrimEnd('/');
        int slash = last.LastIndexOf('/');
        if (slash >= 0) last = last[(slash + 1)..];

        int dot = last.LastIndexOf('.');
        if (dot > 0) last = last[..dot];

        try { last = Uri.UnescapeDataString(last); }
        catch (UriFormatException) { }
        return last.Trim();
    }

    /// <summary>落とし終わったファイルを索引へ足す。次の追加からすぐ効く。</summary>
    public void Add(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var entry = Describe(filePath);
        if (entry is null) return;

        lock (_gate)
        {
            _strict[entry.Value.Strict] = filePath;
            if (entry.Value.Loose is { } loose) _loose[loose] = filePath;
        }
    }

    private static (string Strict, string? Loose)? Describe(string filePath)
    {
        var category = CategoryOf(Path.GetExtension(filePath));
        if (category is null) return null;

        var parsed = ParseStem(Path.GetFileNameWithoutExtension(filePath));
        if (parsed is not { } p) return null;

        var loose = p.Id.Length >= LooseIdMinLength ? LooseKey(category, p.Id) : null;
        return (StrictKey(category, p.Site, p.Id), loose);
    }

    /// <summary>
    /// 指定したフォルダ群を走査して索引を作り直す。
    /// 走査はディスクを読むので、UI スレッドから直接呼ばないこと。
    /// </summary>
    public void Rebuild(IEnumerable<string> roots, CancellationToken ct)
    {
        var strict = new Dictionary<string, string>(StringComparer.Ordinal);
        var loose = new Dictionary<string, string>(StringComparer.Ordinal);
        int seen = 0;
        int rootCount = 0;
        bool truncated = false;

        // 同じ場所を2度読まない（保存先が入れ子や同一のことがある）
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // 読めないフォルダで例外にせず、読める範囲だけ拾う
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root)) continue;

            string full;
            try { full = Path.GetFullPath(root); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException
                                           or PathTooLongException) { continue; }

            if (!visited.Add(full)) continue;
            if (!Directory.Exists(full)) continue;
            rootCount++;

            try
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", options))
                {
                    if (++seen > MaxFiles) { truncated = true; break; }
                    if ((seen & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

                    var entry = Describe(file);
                    if (entry is null) continue;
                    strict[entry.Value.Strict] = file;
                    if (entry.Value.Loose is { } l) loose[l] = file;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            if (truncated) break;
        }

        lock (_gate)
        {
            _strict = strict;
            _loose = loose;
        }
        ScannedRoots = rootCount;
        Truncated = truncated;
    }

    /// <summary>走査するフォルダを設定から組み立てる。</summary>
    public static List<string> RootsFrom(SettingsData s)
    {
        var roots = new List<string> { s.OutputDir, s.VideoDir, s.AudioDir };
        roots.AddRange(ParseFolderList(s.ScanDirs));
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
    }

    /// <summary>1行1フォルダ。カンマ区切りも受ける（パスにカンマは滅多に無い）。</summary>
    public static List<string> ParseFolderList(string? raw) =>
        (raw ?? "")
            .Replace("\r", "")
            .Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .ToList();
}
