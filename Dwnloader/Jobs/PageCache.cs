using Dwnloader.Core;

namespace Dwnloader.Jobs;

/// <summary>
/// 一部失敗したギャラリーの再試行を「失敗したページだけ」にするためのキャッシュ。
///
/// 正規化済みJPEG（PDFに埋め込むのとまったく同じバイト列）をページ番号ごとに
/// 保存しておく。次の再試行では、ここにある分は通信も再エンコードもせずそのまま
/// 使い、無い分（＝前回失敗したページ）だけを取りに行く。全ページ揃った時点で
/// このギャラリーの分は消す（不要なうえ、残すと肥大化するだけ）。
///
/// %TEMP% ではなく %APPDATA% に置く。%TEMP% は起動時の TempSweeper が5分以上
/// 前のものを消してしまい、日をまたいだ手動再試行に耐えられないため。
/// </summary>
internal static class PageCache
{
    private static readonly TimeSpan StaleAge = TimeSpan.FromDays(14);

    private static string RootDir => Path.Combine(AppPaths.ConfigDir, "pagecache");

    private static string DirFor(SourceRef reference) => Path.Combine(RootDir,
        Util.SanitizeFilename(reference.Site, 40, "site"),
        Util.SanitizeFilename(reference.Gid, 80, "gid"));

    private static string PathFor(SourceRef reference, int index) =>
        Path.Combine(DirFor(reference), $"{index:D5}.jpg");

    /// <summary>
    /// キャッシュ済みのページがあれば dest へコピーして true を返す。存在しない・
    /// 壊れている・コピーに失敗した場合は false（＝呼び出し側は普通に取りに行く）。
    ///
    /// 壊れたキャッシュを「取得できたページ」として数えてしまうと、実際には
    /// 欠けたPDFが「完了」として履歴に記録され、二度と取り直せなくなる
    /// （GalleryJob 側が守っている不変条件）。ここでは例外を投げず、
    /// 少しでも疑わしければ false を返して通常の取得に倒す。
    /// </summary>
    public static bool TryUse(SourceRef reference, int index, string dest)
    {
        var path = PathFor(reference, index);
        try
        {
            var info = new FileInfo(path);
            // JPEG の SOI マーカー（FF D8）。書き込み途中や0バイトを弾く。
            if (!info.Exists || info.Length < 2) return false;

            Span<byte> head = stackalloc byte[2];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(head) != 2) return false;
            }
            if (head[0] != 0xFF || head[1] != 0xD8) return false;

            File.Copy(path, dest, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 取得できたページを次回の再試行のために覚えておく。書き込み途中のものを
    /// TryUse に拾わせないよう、一時名で書いてから置き換える（アプリが途中で
    /// 落ちても、拾われるのは完成した方だけ）。
    /// </summary>
    public static void Save(SourceRef reference, int index, string normalizedJpegPath)
    {
        try
        {
            var dir = DirFor(reference);
            Directory.CreateDirectory(dir);
            var final = PathFor(reference, index);
            var tmp = final + ".part";
            File.Copy(normalizedJpegPath, tmp, overwrite: true);
            File.Move(tmp, final, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // キャッシュできなくても本体には影響しない。次回また普通に取り直すだけ。
        }
    }

    /// <summary>全ページ揃ったら不要になる。残しておくと肥大化するだけ。</summary>
    public static void Clear(SourceRef reference)
    {
        try
        {
            var dir = DirFor(reference);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 二度と再試行されなかった分（サイト側の恒久的な不調・作品削除など）を
    /// 長期間放置しない。起動時に1回呼ぶ。
    /// </summary>
    public static int Sweep()
    {
        int removed = 0;
        var now = DateTime.UtcNow;
        try
        {
            if (!Directory.Exists(RootDir)) return 0;
            foreach (var siteDir in Directory.EnumerateDirectories(RootDir))
            {
                foreach (var galleryDir in Directory.EnumerateDirectories(siteDir))
                {
                    try
                    {
                        if (now - Directory.GetLastWriteTimeUtc(galleryDir) < StaleAge) continue;
                        Directory.Delete(galleryDir, recursive: true);
                        removed++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
