using System.Collections.Concurrent;
using Dwnloader.Core;
using Dwnloader.Sites;

namespace Dwnloader.Jobs;

/// <summary>作品1件を取得して PDF にするまで。</summary>
public sealed class GalleryJob : JobBase
{
    public const string TmpPrefix = "dwnloader-";

    /// <summary>URL失効を疑って作り直しを試みる回数。</summary>
    private const int UrlRebuildAttempts = 3;

    private readonly HttpClient _client;
    private readonly string _reusePath;
    private readonly int _minPages;
    private readonly int _keepMissing;

    /// <summary>
    /// 再試行で、前回の不完全な PDF を置き換えたいときの出力先。
    /// 別名で出すと、壊れた方が綺麗な名前に居座り続けてしまう。
    ///
    /// minPages: reusePath を上書きしてよい最小ページ数（0なら常に上書き）。
    /// 自動再試行が前回より悪い結果しか得られなかったとき、黙って良い方の
    /// PDFを潰さないためのガード。手動再試行は常に0で無条件に上書きする。
    ///
    /// keepMissing: フロアに引っかかって既存ファイルを保持するとき、そのまま
    /// 報告する欠落数。今回のページ総数から引き算して求めると、サイト側の
    /// 事情でページ総数自体が前回より減っていた場合に 0 まで潰れてしまい
    /// 「完全成功」として履歴に記録されてしまう。必ず前回の欠落数をそのまま渡す。
    /// </summary>
    public GalleryJob(string jobId, SourceRef reference, SettingsData settings,
                      JobEvents events, HttpClient client, string reusePath = "",
                      int minPages = 0, int keepMissing = 0)
        : base(jobId, reference, settings, events)
    {
        _client = client;
        _reusePath = reusePath;
        _minPages = minPages;
        _keepMissing = keepMissing;
    }

    public override async Task RunAsync()
    {
        string? tmpdir = null;
        try
        {
            Token.ThrowIfCancellationRequested();

            var adapter = SiteRegistry.AdapterFor(Reference.Site);
            var ctx = new SiteContext
            {
                Client = _client,
                Settings = Settings,
                Cancel = Token,
            };

            SetStatus(JobStatus.Fetching);
            var meta = await adapter.FetchAsync(Reference, ctx).ConfigureAwait(false);

            Events.Meta?.Invoke(JobId, new JobMeta
            {
                Title = meta.Title,
                Artists = meta.Artists,
                Series = meta.Series,
                Tags = meta.Tags.Take(12).ToList(),
                Pages = meta.Images.Count,
            });
            Log("info", $"[{adapter.Label}] {meta.Title} — {meta.Images.Count}ページ");

            var outPath = ResolveOutputPath(meta);

            SetStatus(JobStatus.Downloading);
            tmpdir = Path.Combine(Path.GetTempPath(), TmpPrefix + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(tmpdir);

            var (jpegs, missing) = await DownloadAllAsync(meta, adapter, ctx, tmpdir)
                .ConfigureAwait(false);

            Token.ThrowIfCancellationRequested();

            if (_reusePath.Length > 0 && jpegs.Count < _minPages)
            {
                // 自動再試行のはずが、前回より取れたページが少なかった。このまま
                // 上書きすると良い方のPDFを悪い方で潰してしまうので、既存ファイルを
                // 保持したまま「前回と同じ欠落数」として終える。
                Log("warn", $"{meta.Title}: 再試行しましたが前回より取得できたページが" +
                            $"少なかったため（{jpegs.Count}/{meta.Images.Count}）、既存のファイルを保持します");
                Finish(true, _reusePath, "", _keepMissing);
                return;
            }

            SetStatus(JobStatus.Building);
            PdfBuilder.Build(jpegs, outPath, BuildPdfMeta(meta), m => Log("warn", m));

            if (Settings.KeepImages) KeepImages(tmpdir, outPath);

            Finish(true, outPath, "", missing);
        }
        catch (OperationCanceledException)
        {
            SetStatus(JobStatus.Canceled);
            Finish(false, "", "キャンセルしました");
        }
        catch (UnsupportedException e)
        {
            // うごイラなど、待っても状況が変わらない。自動再試行の対象外にする。
            Log("warn", $"{Reference.Url}: {e.Message}");
            Finish(false, "", e.Message, permanent: true);
        }
        catch (AuthRequiredException e)
        {
            // ログインが必要、というのは待っても解決しない。ユーザーの操作が要る。
            Log("warn", $"{Reference.Url}: {e.Message}");
            Finish(false, "", e.Message, permanent: true);
        }
        catch (SiteException e)
        {
            Log("error", $"{Reference.Url}: {e.Message}");
            Finish(false, "", e.Message);
        }
        catch (Exception e)      // 想定外もUIに出す。落とさない。
        {
            Log("error", $"{Reference.Url}: {e.GetType().Name}: {e.Message}");
            Finish(false, "", $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (tmpdir is not null) TryDeleteDir(tmpdir);
        }
    }

    // ------------------------------------------------------------ 出力先

    private string ResolveOutputPath(GalleryMeta meta)
    {
        if (_reusePath.Length > 0)
        {
            // PdfBuilder は一時ファイルに書いてから置換するので、失敗しても
            // 既存ファイルは壊れない
            Directory.CreateDirectory(Path.GetDirectoryName(_reusePath)!);
            return _reusePath;
        }

        var baseDir = Settings.OutputDir;
        if (Settings.SiteSubfolder)
            baseDir = Path.Combine(baseDir, Util.SanitizeFilename(meta.Site, 40, meta.Site));
        Directory.CreateDirectory(baseDir);

        var name = Util.FormatFilename(Settings.FilenameTemplate, meta, Settings.FilenameMaxLen);
        return Util.UniquePath(Path.Combine(baseDir, name + ".pdf"));
    }

    /// <summary>
    /// 作品情報を PDF の文書情報に写す。
    /// 作者が取れないサイトではサークル名で代用する。キーワードにはタグと
    /// キャラクターに加えて出典URLを入れ、後から辿れるようにする。
    /// </summary>
    private static PdfMeta BuildPdfMeta(GalleryMeta meta)
    {
        var keywords = new List<string>();
        keywords.AddRange(meta.Tags);
        keywords.AddRange(meta.Characters);
        if (meta.PageUrl.Length > 0) keywords.Add(meta.PageUrl);

        var author = meta.Artists.Count > 0 ? meta.Artists : meta.Groups;

        return new PdfMeta
        {
            Title = meta.Title,
            Author = string.Join("、", author),
            Subject = string.Join("、", meta.Series),
            Keywords = keywords.Where(k => k.Length > 0).Take(40).ToList(),
            Creator = $"{AppInfo.Title} {AppInfo.Version}",
            Created = meta.Published,
        };
    }

    // ------------------------------------------------------------ 取得

    private async Task<(List<string> Jpegs, int Missing)> DownloadAllAsync(
        GalleryMeta meta, SiteAdapter adapter, SiteContext ctx, string workdir)
    {
        var allImages = meta.Images;
        int total = allImages.Count;
        int done = 0;

        var results = new ConcurrentDictionary<int, string>();
        var errors = new ConcurrentQueue<string>();
        var refreshGate = new SemaphoreSlim(1, 1);

        int quality = Settings.JpegQuality;
        bool keep = Settings.KeepImages;

        var origDir = Path.Combine(workdir, "original");
        if (keep) Directory.CreateDirectory(origDir);

        int workers = Math.Max(1, Math.Min(Settings.ImageWorkers, adapter.MaxImageWorkers));
        using var slots = new SemaphoreSlim(workers, workers);

        var tasks = allImages.Select(async image =>
        {
            await slots.WaitAsync(Token).ConfigureAwait(false);
            try
            {
                if (Token.IsCancellationRequested) return;

                var (data, usedFmt) = await FetchImageAsync(image, meta, adapter, ctx, refreshGate)
                    .ConfigureAwait(false);

                if (keep)
                {
                    await File.WriteAllBytesAsync(
                        Path.Combine(origDir, $"{image.Index:D5}.{usedFmt}"), data, Token)
                        .ConfigureAwait(false);
                }

                // 実際に受け取った量をそのまま伝える（速度計はこの積み上げで測る）
                Events.Bytes?.Invoke(data.Length);

                var dest = Path.Combine(workdir, $"{image.Index:D5}.jpg");
                // 復号と再圧縮は CPU を使う。UI と同じスレッドで回さないよう
                // スレッドプールへ逃がす（await 元へ戻らないので UI は止まらない）。
                await Task.Run(() => ImagePipeline.NormalizeToJpeg(data, dest, quality), Token)
                    .ConfigureAwait(false);

                if (image.Index == 1)
                {
                    // 表紙は1ページ目から作る。追加の通信はしない。
                    try
                    {
                        var thumb = await Task.Run(() => ImagePipeline.MakeThumbnail(dest), Token)
                            .ConfigureAwait(false);
                        Events.Thumbnail?.Invoke(JobId, thumb);
                    }
                    catch (Exception) { /* 表紙が無くても本体は作れる */ }
                }

                results[image.Index] = dest;
                int current = Interlocked.Increment(ref done);
                Events.Progress?.Invoke(JobId, current, total, $"{current} / {total} ページ");
            }
            catch (OperationCanceledException)
            {
                // 中止。ここでは何もしない（呼び出し側でまとめて判断する）
            }
            catch (Exception e)
            {
                errors.Enqueue($"p.{image.Index}: {e.Message}");
            }
            finally
            {
                slots.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Token.ThrowIfCancellationRequested();

        if (results.IsEmpty)
        {
            errors.TryPeek(out var first);
            throw new SiteException($"画像を1枚も取得できませんでした（{first ?? "原因不明"}）");
        }

        int missing = total - results.Count;
        if (missing > 0)
        {
            errors.TryPeek(out var head);
            Log("warn", $"{meta.Title}: {missing}/{total} ページを取得できませんでした（{head ?? "原因不明"}）");
        }

        var ordered = results.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        return (ordered, missing);
    }

    /// <summary>
    /// 1ページ分を取得。404 は URL 失効の可能性があるので作り直して再挑戦する。
    ///
    /// 並列取得中に URL が作り直された場合、古い URL を掴んだままのタスクも
    /// 最新の URL を読み直して再試行する（自分が作り直した本人かどうかは問わない）。
    /// </summary>
    private async Task<(byte[] Data, string Fmt)> FetchImageAsync(
        ImageRef image, GalleryMeta meta, SiteAdapter adapter, SiteContext ctx,
        SemaphoreSlim refreshGate)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);

        for (int attempt = 0; attempt < UrlRebuildAttempts; attempt++)
        {
            var current = CurrentRef(meta, image);
            var candidates = new List<string> { current.Url };
            candidates.AddRange(current.Fallbacks);

            foreach (var url in candidates)
            {
                Token.ThrowIfCancellationRequested();
                if (!tried.Add(url)) continue;

                HttpResult resp;
                try
                {
                    resp = await Net.GetWithRetryAsync(
                        ctx.Client, url, current.Headers, ctx.Timeout, ctx.Retries,
                        adapter.Limiter, Token).ConfigureAwait(false);
                }
                catch (TransientException)
                {
                    // この候補URLでは（内部の再試行を使い切っても）取れなかった。
                    // ここで投げ返すと、フォールバック画質もURL作り直しも試さずに
                    // ページを丸ごと失う。次の候補、無ければ次の試行に賭ける。
                    continue;
                }

                if (!resp.IsOk) continue;
                var data = resp.Body;

                if (url != current.Url)
                {
                    Log("warn", $"p.{image.Index}: 代替URLで取得しました" +
                                "（画質が下がる場合があります）");
                }

                var ext = url[(url.LastIndexOf('.') + 1)..].ToLowerInvariant();
                var fmt = ext.Length is > 0 and <= 5 && ext.All(char.IsLetterOrDigit)
                    ? ext : current.Fmt;
                return (data, fmt);
            }

            // 全滅した。URL が失効した可能性があるので作り直す。
            await refreshGate.WaitAsync(Token).ConfigureAwait(false);
            try
            {
                var latest = CurrentRef(meta, image);
                if (!tried.Contains(latest.Url))
                    continue;               // 別タスクが既に作り直していた。そちらを使う。

                if (!await adapter.RefreshAsync(meta, ctx).ConfigureAwait(false)) break;
                Log("info", "画像URLの有効期限切れを検出したため再取得します");
            }
            finally
            {
                refreshGate.Release();
            }
        }

        throw new TransientException("画像を取得できません");
    }

    /// <summary>Refresh で差し替わっている可能性があるので、常に最新を読み直す。</summary>
    private static ImageRef CurrentRef(GalleryMeta meta, ImageRef image)
    {
        foreach (var candidate in meta.Images)
            if (candidate.Index == image.Index) return candidate;
        return image;
    }

    /// <summary>変換前の元画像を PDF と同名のフォルダに残す。</summary>
    private void KeepImages(string workdir, string outPath)
    {
        var srcDir = Path.Combine(workdir, "original");
        if (!Directory.Exists(srcDir)) return;

        var dest = Path.Combine(Path.GetDirectoryName(outPath)!,
                                Path.GetFileNameWithoutExtension(outPath));
        try
        {
            Directory.CreateDirectory(dest);
            foreach (var src in Directory.EnumerateFiles(srcDir).OrderBy(p => p, StringComparer.Ordinal))
                File.Copy(src, Path.Combine(dest, Path.GetFileName(src)), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log("warn", $"元画像の保存に失敗しました: {e.Message}");
        }
    }

    internal static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
