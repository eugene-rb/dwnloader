using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks.Dataflow;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dwnloader.Core;
using Dwnloader.Jobs;
using Dwnloader.Sites;

namespace Dwnloader;

/// <summary>アプリの状態とふるまい。描画そのものは持たない。</summary>
public sealed class Session : IDisposable
{
    /// <summary>UI へ流す更新をまとめる間隔。人の目には十分速く、描画の回数は大きく減る。</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

    private readonly Dispatcher _ui;
    private readonly AppSettings _settings;
    private readonly History _history;
    private readonly QueueStore _queueStore;
    private readonly HttpClient _client;
    private readonly DownloadManager _manager;

    /// <summary>ref.Key -> Entry。重複判定を件数に関わらず一定時間で行うための索引。</summary>
    private readonly Dictionary<string, EntryVm> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntryVm> _byId = new(StringComparer.Ordinal);

    private int _nextId;

    // ---- UI へ流す更新の緩衝 ----
    private readonly ConcurrentDictionary<string, PendingProgress> _pendingProgress = new();
    private readonly DispatcherTimer _pump;
    private volatile bool _stateDirty;

    /// <summary>ダウンロード速度の計測。全ジョブの実バイト数をここへ積む。</summary>
    public SpeedMeter Speed { get; } = new();

    /// <summary>保存済みファイルの索引。履歴に無くても、実物があれば重複とみなす。</summary>
    public LibraryIndex Library { get; } = new();
    private CancellationTokenSource? _scanCts;
    private long _lastSpeedSampleTicks = Environment.TickCount64;
    private volatile bool _persistDirty;

    // ---- URL 解析を直列に流すための待ち行列 ----
    private readonly ActionBlock<ResolveRequest> _resolver;

    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public BulkObservableCollection<EntryVm> Entries { get; } = new();
    public ClipboardWatcher Clipboard { get; }

    public SettingsData Settings => _settings.Current;
    public AppSettings SettingsStore => _settings;
    public History History => _history;

    public event Action<string, string>? LogMessage;                 // level, message
    public event Action<string, string, string>? Notification;       // title, body, kind
    public event Action? StateChanged;
    /// <summary>速度のサンプルが1つ増えたとき。グラフの描き直しに使う。</summary>
    public event Action? SpeedSampled;
    public event Action<EntryVm>? FocusRequested;

    private sealed record ResolveRequest(string Text, bool AllowGeneric, bool UseBlacklist,
                                         string Source, bool NotifyWhenEmpty);

    private sealed class PendingProgress
    {
        public long Done;
        public long Total;
        public string Detail = "";
    }

    public Session(Dispatcher ui, AppSettings settings, History history, QueueStore queueStore)
    {
        _ui = ui;
        _settings = settings;
        _history = history;
        _queueStore = queueStore;

        var s = settings.Current;
        _client = Net.CreateClient(pool: Math.Max(16, s.ImageWorkers * 4));
        _manager = new DownloadManager(new Dictionary<string, int>
        {
            [DownloadManager.LaneGallery] = s.GalleryWorkers,
            [DownloadManager.LaneMedia] = s.VideoWorkers,
        });

        // URL の解釈は重いことがある（正規表現の総当り＋大量のテキスト）。
        // 呼び出し元（クリップボード監視・UI スレッド）を塞がないよう、
        // 専用の1本の流れに直列で積む。1本にすることで追加順も保たれる。
        _resolver = new ActionBlock<ResolveRequest>(ResolveAndAddAsync,
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 1,
                CancellationToken = _shutdown.Token,
                BoundedCapacity = DataflowBlockOptions.Unbounded,
            });

        Clipboard = new ClipboardWatcher(OnClipboardText);

        _pump = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = PumpInterval,
        };
        _pump.Tick += (_, _) => FlushPending();
        _pump.Start();
    }

    // ============================================================== 起動処理

    public void Bootstrap()
    {
        int stale = TempSweeper.Sweep();
        if (stale > 0) Log("info", $"前回の作業用フォルダ {stale} 件を削除しました");

        int stalePages = PageCache.Sweep();
        if (stalePages > 0) Log("info", $"長期間再試行されなかったページキャッシュ {stalePages} 件を削除しました");

        var supported = string.Join("・", SiteRegistry.Adapters.Select(a => a.Label));
        Log("info", $"{supported} に対応しています。URLをコピーすると自動で取り込みます。");

        var ytdlp = YtDlp.Locate(Settings.YtDlpPath);
        if (ytdlp is not null)
        {
            Log("info", $"動画・音声は yt-dlp で取得します（{ytdlp.Description}）。" +
                        $"現在は{MediaKind.Label(MediaKindValue)}として保存します。");
            if (YtDlp.FfmpegPath().Length == 0)
            {
                Log("warn", "ffmpeg が見つかりません。音声の取り出しと高画質動画の" +
                            "結合ができません（winget install Gyan.FFmpeg）");
            }
        }
        else
        {
            Log("warn", YtDlp.InstallHint);
        }

        RestoreQueue();
        Clipboard.SetEnabled(Settings.WatchClipboard);
        RescanLibrary();
    }

    // ============================================================== 保存済みの走査

    /// <summary>
    /// 保存先と、設定で足したフォルダを走査して索引を作り直す。
    /// ディスクを読むので裏で走らせる。走査中でも追加や取得は普通にできる
    /// （その間は履歴だけで重複を判定する）。
    /// </summary>
    public void RescanLibrary()
    {
        var previous = _scanCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _scanCts = cts;
        // 設定を続けて変えたときに、古い走査を残さない
        try { previous?.Cancel(); previous?.Dispose(); } catch (ObjectDisposedException) { }

        var roots = LibraryIndex.RootsFrom(Settings);

        _ = Task.Run(() =>
        {
            try
            {
                Library.Rebuild(roots, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Post(() => Log("warn", $"保存済みファイルの確認に失敗しました: {e.Message}"));
                return;
            }

            Post(() =>
            {
                if (Library.Truncated)
                {
                    Log("warn", $"保存済みファイルが多すぎるため、途中まで確認しました"
                                + $"（{Library.Count} 件）。確認するフォルダを絞ってください。");
                }
                else if (Library.Count > 0)
                {
                    Log("info", $"保存済み {Library.Count} 件を確認しました"
                                + $"（{Library.ScannedRoots} フォルダ）。同じものは取得しません。");
                }
            });
        }, cts.Token);
    }

    // ============================================================== 通知

    public void Log(string level, string message) => LogMessage?.Invoke(level, message);

    public void Notify(string title, string body, string kind = "info")
    {
        if (!Settings.Notify && kind is "info") return;
        Notification?.Invoke(title, body, kind);
    }

    /// <summary>
    /// 全体の状態が変わったことを知らせる。連続して呼ばれても、実際の
    /// 反映は緩衝タイマーが1回にまとめる（件数が多いときの描画を抑える）。
    /// </summary>
    public void MarkStateDirty() => _stateDirty = true;

    /// <summary>
    /// キューの保存を予約する。1件終わるたびに全件を組み直すと、件数の
    /// 2乗に比例した仕事が UI スレッドに乗る（元の Python 版が固まっていたのと
    /// 同じ形）。緩衝タイマーで1回にまとめる。
    /// </summary>
    public void Persist() => _persistDirty = true;

    /// <summary>予約を待たずに今すぐ書く（終了処理用）。</summary>
    private void PersistNow()
    {
        _persistDirty = false;
        var records = Entries
            .Where(e => StatusText.ShouldPersist(e.Status))
            .Select(e => new QueueRecord
            {
                Site = e.Reference.Site,
                Gid = e.Reference.Gid,
                Url = e.Reference.Url,
                Kind = e.Reference.Kind,
                Status = e.Status.ToString(),
                Title = e.Title,
                Subtitle = e.Subtitle,
                Done = e.Done,
                Total = e.Total,
                OutPath = e.OutPath,
                Message = e.Message,
            })
            .ToList();
        _queueStore.Replace(records);
    }

    private void FlushPending()
    {
        if (!_pendingProgress.IsEmpty)
        {
            foreach (var jobId in _pendingProgress.Keys.ToList())
            {
                if (!_pendingProgress.TryRemove(jobId, out var p)) continue;
                if (!_byId.TryGetValue(jobId, out var entry)) continue;
                entry.Done = p.Done;
                entry.Total = p.Total;
                entry.Detail = p.Detail;
            }
        }

        // 速度のサンプルは1秒刻み。緩衝タイマーに相乗りして専用のタイマーを増やさない。
        var now = Environment.TickCount64;
        if (now - _lastSpeedSampleTicks >= 1000)
        {
            _lastSpeedSampleTicks = now;
            Speed.Sample();
            SpeedSampled?.Invoke();
        }

        if (_persistDirty) PersistNow();

        if (_stateDirty)
        {
            _stateDirty = false;
            StateChanged?.Invoke();
        }
    }

    // ============================================================== 状態計算

    public bool HasStartable() => Entries.Any(e => e.Status == JobStatus.Queued && !e.Started);

    public int UnfinishedCount() => Entries.Count(e => StatusText.IsUnfinished(e.Status));

    public int RunningCount() => Entries.Count(e => e.IsActive);

    /// <summary>状況に応じて言い換える。0が並ぶだけの表示は情報量がない。</summary>
    public string StatusText_()
    {
        var running = new List<EntryVm>();
        int queued = 0, unfinished = 0;

        foreach (var e in Entries)
        {
            if (e.Status == JobStatus.Queued) queued++;
            else if (e.IsActive) running.Add(e);
            if (StatusText.IsUnfinished(e.Status)) unfinished++;
        }

        if (running.Count > 0)
        {
            var head = running[0];
            var detail = head.Detail.Length > 0 ? head.Detail : head.StatusLabel;
            var title = head.Title.Length > 28 ? head.Title[..28] : head.Title;
            var more = running.Count > 1 ? $" ほか{running.Count - 1}件" : "";
            return $"{title} — {detail}{more}";
        }
        if (queued > 0) return $"{queued}件が待機中";
        if (unfinished > 0) return $"{unfinished}件が未完了 — 再試行できます";
        if (Clipboard.IsEnabled) return $"監視中 — 保存先 {Settings.OutputDir}";
        return "監視は停止中です";
    }

    // ============================================================== 追加

    public string MediaKindValue => MediaKind.Normalize(Settings.MediaKindValue);

    private HashSet<string> BlacklistHosts() => UrlDetect.ParseHostList(Settings.ClipboardBlacklist);
    private HashSet<string> WhitelistHosts() => UrlDetect.ParseHostList(Settings.ClipboardWhitelist);

    private void OnClipboardText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // 既知サイト以外も yt-dlp に賭ける（ブラックリスト式）。除外ドメインだけは
        // 対象外にして、ただのページ巡回コピーの誤検出を減らす。
        _resolver.Post(new ResolveRequest(text, AllowGeneric: true, UseBlacklist: true,
                                          "クリップボード", NotifyWhenEmpty: false));
    }

    /// <summary>
    /// テキストからURLを拾ってキューへ足す。解析は裏で走るので、この呼び出し自体は
    /// すぐ戻る（大量に貼られてもUIが待たされない）。
    /// </summary>
    public void AddText(string text, string source = "手動")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            NotifyCannotAdd();
            return;
        }
        // 手で貼られたURLは、判定に外れても yt-dlp の汎用抽出に賭ける
        _resolver.Post(new ResolveRequest(text, AllowGeneric: true, UseBlacklist: false,
                                          source, NotifyWhenEmpty: true));
    }

    private void NotifyCannotAdd() => Notify("追加できません",
        "hitomi.la / momon-ga.com / pixiv.net の作品URL、または動画・音声のURLを入れてください",
        "warning");

    private async Task ResolveAndAddAsync(ResolveRequest request)
    {
        List<SourceRef> refs;
        try
        {
            refs = await Task.Run(() => ResolveText(request), _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            await _ui.InvokeAsync(() =>
                Log("error", $"URLの解析に失敗しました: {e.GetType().Name}: {e.Message}"),
                DispatcherPriority.Background);
            return;
        }

        if (_shutdown.IsCancellationRequested) return;

        if (refs.Count == 0)
        {
            if (request.NotifyWhenEmpty)
                await _ui.InvokeAsync(NotifyCannotAdd, DispatcherPriority.Background);
            return;
        }

        // 追加そのものは UI スレッドで一括で行う。1件ずつ流すと件数分の
        // レイアウトが走り、まさに「固まる」状態になる。
        await _ui.InvokeAsync(() => AddRefs(refs, request.Source), DispatcherPriority.Background);
    }

    /// <summary>
    /// テキストからギャラリーと動画・音声のURLを拾う。
    /// ギャラリー側を先に見る。hitomi などは yt-dlp も一応受け取るが、
    /// このアプリでの本来の扱いは PDF 化なのでそちらを優先する。
    /// </summary>
    private List<SourceRef> ResolveText(ResolveRequest request)
    {
        var kind = MediaKindValue;
        var black = request.UseBlacklist ? BlacklistHosts() : new HashSet<string>();
        var white = request.UseBlacklist ? WhitelistHosts() : new HashSet<string>();

        var refs = new List<SourceRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 同じURLが何度も貼られていると、その回数だけ判定を回すことになる。
        // 先に潰しておく。
        foreach (var candidate in UrlDetect.FindUrls(request.Text).Distinct(StringComparer.Ordinal))
        {
            if (_shutdown.IsCancellationRequested) break;

            var gallery = SiteRegistry.Resolve(candidate);
            if (gallery is not null)
            {
                if (seen.Add(gallery.Key)) refs.Add(gallery);
                continue;
            }

            if (!UrlDetect.IsUrl(candidate)) continue;

            var known = UrlDetect.MatchKnownSite(candidate);
            SourceRef? media = null;

            if (known is not null)
            {
                // 既知サイトは除外リストと無関係に拾う
                media = new SourceRef(known.Site, known.Gid, candidate, kind);
            }
            else if (request.AllowGeneric)
            {
                bool blocked = request.UseBlacklist
                               && UrlDetect.HostMatches(candidate, black)
                               && !UrlDetect.HostMatches(candidate, white);
                if (!blocked)
                    media = new SourceRef("web", UrlDetect.Hashed(candidate), candidate, kind);
            }

            if (media is not null && seen.Add(media.Key)) refs.Add(media);
        }
        return refs;
    }

    /// <summary>キューへ足す。UI スレッドから呼ぶこと。</summary>
    public int AddRefs(IReadOnlyList<SourceRef> refs, string source)
    {
        int added = 0, skipped = 0, duplicates = 0, fromFile = 0;
        EntryVm? focus = null;
        var created = new List<EntryVm>();

        foreach (var reference in refs)
        {
            if (_byKey.TryGetValue(reference.Key, out var existing))
            {
                // 行を増やさず、既にある行へ案内する。未完了なら再試行の
                // 入口がそこに出ている。
                duplicates++;
                focus ??= existing;
                continue;
            }

            // 履歴に無くても、実物がディスクにあれば取得済みとして扱う。
            // 履歴を消した・別の環境で落とした・手で整理した場合に、
            // 同じものをもう一度落とさないため。
            string savedPath = "";
            bool alreadyDone = false;
            if (Settings.SkipDownloaded)
            {
                savedPath = _history.PathFor(reference.Key) ?? "";
                alreadyDone = savedPath.Length > 0;
                // 直リンクの動画は、こちらのIDがURLのハッシュなので当たらない。
                // yt-dlp が付けるのと同じ「URL末尾のファイル名」も手掛かりに使う。
                var hint = reference.Site is "file" or "web"
                    ? LibraryIndex.NameHintFromUrl(reference.Url) : null;
                if (!alreadyDone && Library.TryFind(reference, out var found, hint))
                {
                    savedPath = found;
                    alreadyDone = true;
                    fromFile++;
                }
            }

            var entry = NewEntry(reference,
                                 alreadyDone ? JobStatus.Skipped : JobStatus.Queued);

            if (alreadyDone)
            {
                entry.OutPath = savedPath;
                entry.Subtitle = $"取得済み — 保存済みの{MediaKind.ResultLabel(reference.Kind)}を開けます";
                entry.Detail = "取得済み";
                skipped++;
            }
            else
            {
                entry.Detail = "待機中";
                added++;
            }
            created.Add(entry);
        }

        Entries.AddRange(created);

        if (Settings.AutoStart)
        {
            foreach (var entry in created)
                if (entry.Status == JobStatus.Queued) Start(entry.JobId);
        }

        if (added > 0 || skipped > 0 || duplicates > 0)
        {
            var sites = string.Join("・",
                refs.Select(r => SiteRegistry.LabelFor(r.Site)).Distinct().OrderBy(x => x));
            var parts = new List<string>();
            if (added > 0)
            {
                parts.Add(skipped == 0 && duplicates == 0
                    ? $"{added}件を追加（{sites}）" : $"{added}件を追加");
            }
            if (skipped > 0)
            {
                parts.Add(fromFile > 0 && fromFile == skipped
                    ? $"{skipped}件は保存済みのファイルが見つかりました"
                    : fromFile > 0 ? $"{skipped}件は取得済み（うち{fromFile}件はファイルを確認）"
                    : $"{skipped}件は取得済み");
            }
            if (duplicates > 0) parts.Add($"{duplicates}件はすでにキューにあります");
            Notify($"{source}から検出", string.Join("、", parts), added > 0 ? "info" : "warning");
        }

        if (focus is not null && added == 0) FocusRequested?.Invoke(focus);

        Persist();
        MarkStateDirty();
        return added;
    }

    private EntryVm NewEntry(SourceRef reference, JobStatus status)
    {
        var jobId = "job" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var entry = new EntryVm(jobId, reference) { Status = status };
        _byId[jobId] = entry;
        _byKey[reference.Key] = entry;
        return entry;
    }

    private void DropIndex(EntryVm entry)
    {
        _byId.Remove(entry.JobId);
        if (_byKey.TryGetValue(entry.Reference.Key, out var found) && ReferenceEquals(found, entry))
            _byKey.Remove(entry.Reference.Key);
    }

    // ============================================================== 実行

    /// <summary>
    /// minPages: reusePath を上書きしてよい最小ページ数。自動再試行がこれを
    /// 下回る結果しか得られなかった場合、GalleryJob は既存ファイルを残す
    /// （悪い結果で良いPDFを黙って潰さないため）。手動再試行では常に0（無条件で上書き）。
    /// keepMissing: フロアに引っかかったときにそのまま報告する欠落数（前回の値）。
    /// </summary>
    public void Start(string jobId, string reusePath = "", int minPages = 0, int keepMissing = 0)
    {
        if (!_byId.TryGetValue(jobId, out var entry)) return;
        if (entry.Status != JobStatus.Queued || entry.Started) return;
        entry.Started = true;

        var settings = Settings;
        var events = BuildEvents();

        if (MediaKind.IsMedia(entry.Reference.Kind))
        {
            _manager.SetWorkers(DownloadManager.LaneMedia, settings.VideoWorkers);
            _ = _manager.SubmitAsync(new MediaJob(jobId, entry.Reference, settings, events),
                                     DownloadManager.LaneMedia);
        }
        else
        {
            _manager.SetWorkers(DownloadManager.LaneGallery, settings.GalleryWorkers);
            _ = _manager.SubmitAsync(
                new GalleryJob(jobId, entry.Reference, settings, events, _client, reusePath,
                              minPages, keepMissing),
                DownloadManager.LaneGallery);
        }
        MarkStateDirty();
    }

    public void StartAll()
    {
        var targets = Entries.Where(e => e.Status == JobStatus.Queued && !e.Started)
                             .Select(e => e.JobId).ToList();
        foreach (var jobId in targets) Start(jobId);
        if (targets.Count > 0)
            Log("info", $"{targets.Count} 件のダウンロードを開始しました");
    }

    public void Cancel(string jobId)
    {
        if (!_byId.TryGetValue(jobId, out var entry)) return;

        if (entry.Status == JobStatus.Queued && !entry.Started)
        {
            entry.Status = JobStatus.Canceled;
            entry.Detail = "キャンセル";
            Persist();
            MarkStateDirty();
            return;
        }
        if (_manager.Cancel(jobId))
            Log("warn", $"キャンセルを要求しました: {entry.Reference.Url}");
    }

    /// <summary>再試行できる失敗のうち、自動で再試行する回数の上限。</summary>
    private const int AutoRetryLimit = 2;

    /// <summary>n回目（1始まり）の自動再試行までの待ち時間。1回ごとの通信の
    /// 再試行（数秒）とは別の時間軸で、瞬断ではなく数十秒〜のブレを想定する。</summary>
    private static TimeSpan AutoRetryDelay(int attempt) =>
        TimeSpan.FromSeconds(attempt <= 1 ? 20 : 60);

    public void Retry(string jobId, bool auto = false)
    {
        if (!_byId.TryGetValue(jobId, out var entry)) return;
        if (entry.IsActive) return;

        // 手動での再試行はユーザーの判断が入っているので、自動再試行の
        // 残り回数を仕切り直す。自動再試行からの呼び出しでは回数を保つ。
        if (!auto) entry.AutoRetryCount = 0;

        // 既存の出力ファイルがあれば、それを置き換える対象にする。Partial に
        // 限定しない：自動再試行が連鎖する途中で Failed を挟んでも（例えば
        // 一部失敗→自動再試行→今度は情報取得の段階で失敗、のような場合）
        // OutPath をここで空にしないので、次の再試行でも同じファイルを指し
        // 続けられる。空にしてしまうと、後続の試行が別名（「(2)」）で保存し、
        // 元の（実は良い方の）ファイルがどこからも参照されずに残ってしまう。
        var replace = entry.OutPath.Length > 0 && File.Exists(entry.OutPath)
            ? entry.OutPath : "";

        // 自動再試行が前回より悪い結果（ページ数が少ない）を返してきても、
        // 黙って良い方のPDFを潰さない。手動再試行はユーザーが見ているので
        // 従来どおり無条件で上書きする。
        int minPages = auto && replace.Length > 0 ? (int)entry.Done : 0;
        int keepMissing = minPages > 0 ? (int)Math.Max(0, entry.Total - entry.Done) : 0;

        entry.Status = JobStatus.Queued;
        entry.Started = false;
        entry.Done = entry.Total = 0;
        entry.Detail = "待機中";
        entry.Message = "";
        // OutPath はここでは空にしない（上のコメント参照）。GalleryJob が
        // Finish() で成功／フロア判定いずれの結果でも改めて正しい値を送ってくる。

        _manager.Forget(jobId);
        if (replace.Length > 0)
            Log("info", $"再試行: {Path.GetFileName(replace)} を上書きします");

        Start(jobId, replace, minPages, keepMissing);
        Persist();
    }

    /// <summary>
    /// 条件を満たせば自動再試行を予約して true を返す（呼び出し側は通知や
    /// 学習リストへの追加、失敗ログの出力を保留する）。満たさなければ false。
    ///
    /// カウントダウンの案内は Detail に載せる（Message ではない）。Message は
    /// queue.json に永続化されて再起動後もそのまま復元されるが、タイマーは
    /// セッション限りなので、そこにカウントダウンを書くと「もう発火しない
    /// 自動再試行の予告」が再起動後も残り続ける。Detail は RestoreOne が
    /// 状態から毎回組み立て直すので、この問題が起きない。
    /// </summary>
    private bool TryScheduleAutoRetry(string jobId, EntryVm entry, JobStatus expected,
                                      string title, string reason, bool permanent = false)
    {
        if (permanent || entry.AutoRetryCount >= AutoRetryLimit) return false;

        entry.AutoRetryCount++;
        var delay = AutoRetryDelay(entry.AutoRetryCount);
        int seconds = (int)delay.TotalSeconds;
        entry.Detail += $" · {seconds}秒後に自動再試行 {entry.AutoRetryCount}/{AutoRetryLimit}";
        Log("warn", $"{title}: {reason}。{seconds}秒後に自動で再試行します" +
                    $"（{entry.AutoRetryCount}/{AutoRetryLimit}）");
        ScheduleAutoRetry(jobId, entry, expected, delay);
        return true;
    }

    /// <summary>
    /// 失敗を自動で再試行する。既存の Retry をそのまま呼ぶので、置き換え・
    /// 状態リセットなどは手動再試行と同じ経路を通る。
    ///
    /// 発火時に、スケジュールした時点と同じ行（同じ参照）・同じ状態のままか
    /// を確かめる。ユーザーが手で再試行・削除・中止していれば何もしない。
    /// </summary>
    private void ScheduleAutoRetry(string jobId, EntryVm entry, JobStatus expected, TimeSpan delay)
    {
        _ = Task.Delay(delay, _shutdown.Token).ContinueWith(_ =>
        {
            Post(() =>
            {
                if (!_byId.TryGetValue(jobId, out var current) || !ReferenceEquals(current, entry))
                    return;
                if (current.Status != expected) return;
                Retry(jobId, auto: true);
            });
        }, _shutdown.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public void RetryAll()
    {
        var targets = Entries.Where(e => StatusText.IsUnfinished(e.Status))
                             .Select(e => e.JobId).ToList();
        foreach (var jobId in targets) Retry(jobId);
        if (targets.Count > 0)
        {
            Log("info", $"{targets.Count} 件を再試行します");
            Notify("再試行を開始しました", $"{targets.Count}件");
        }
    }

    public void Remove(string jobId)
    {
        if (!_byId.TryGetValue(jobId, out var entry)) return;
        _manager.Cancel(jobId);
        _manager.Forget(jobId);
        DropIndex(entry);
        Entries.Remove(entry);
        Persist();
        MarkStateDirty();
    }

    public void RemoveMany(IEnumerable<EntryVm> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;

        foreach (var entry in list)
        {
            _manager.Cancel(entry.JobId);
            _manager.Forget(entry.JobId);
            DropIndex(entry);
        }
        Entries.RemoveRange(list);
        Persist();
        MarkStateDirty();
    }

    public void ClearFinished()
    {
        // 「完了を消す」は完了(Done/Skipped)のみが対象。失敗・一部失敗・キャンセルは
        // 未完了(IsUnfinished)として残し、誤って消えないようにする。
        RemoveMany(Entries.Where(e => !e.IsActive && !StatusText.IsUnfinished(e.Status)).ToList());
    }

    /// <summary>進行中も含めてキューを空にする。走っているものは中止してから消す。</summary>
    public int ClearAll()
    {
        int count = Entries.Count;
        if (count == 0) return 0;

        foreach (var entry in Entries)
        {
            _manager.Cancel(entry.JobId);
            _manager.Forget(entry.JobId);
        }
        _byId.Clear();
        _byKey.Clear();
        Entries.ResetTo(Array.Empty<EntryVm>());

        Log("info", $"キューから {count} 件を削除しました");
        Persist();
        MarkStateDirty();
        return count;
    }

    // ============================================================== 種別の切り替え

    public string SetMediaKind(string kind)
    {
        if (MediaKind.IsMedia(kind))
        {
            _settings.Update(s => s.MediaKindValue = kind);
            MarkStateDirty();
        }
        return MediaKindValue;
    }

    /// <summary>
    /// 再生リスト・チャンネルのURLを全部取るか、先頭の1本だけにするか。
    /// 判断はジョブが走り出す時点の値で固定するので、走行中のものは変わらない。
    /// </summary>
    public bool SetPlaylistAll(bool enabled)
    {
        _settings.Update(s => s.PlaylistAll = enabled);
        Log("info", enabled
            ? "再生リスト・チャンネルのURLは全体を取得します"
            : "再生リスト・チャンネルのURLは先頭の1本だけ取得します");
        MarkStateDirty();
        return enabled;
    }

    /// <summary>キューにあるカードを動画↔音声で切り替える。</summary>
    public bool SetEntryKind(string jobId, string kind)
    {
        if (!MediaKind.IsMedia(kind)) return false;
        if (!_byId.TryGetValue(jobId, out var entry)) return false;
        if (!MediaKind.IsMedia(entry.Reference.Kind)) return false;
        if (entry.Reference.Kind == kind) return true;

        var label = MediaKind.Label(kind);
        if (!entry.CanSwitchKind)
        {
            if (entry.Status is JobStatus.Done or JobStatus.Skipped or JobStatus.Partial)
            {
                Notify("切り替えられません",
                       $"保存済みです。{label}でも欲しいときは、トグルを切り替えて同じURLを追加してください",
                       "warning");
            }
            else
            {
                Notify("切り替えられません", "進行中です。中止してから切り替えてください", "warning");
            }
            return false;
        }

        var twin = entry.Reference with { Kind = kind };
        if (_byKey.TryGetValue(twin.Key, out var other))
        {
            FocusRequested?.Invoke(other);
            Notify("すでにあります", $"{label}のカードがキューにあります", "warning");
            return false;
        }

        DropIndex(entry);
        entry.Reference = twin;
        _byId[entry.JobId] = entry;
        _byKey[twin.Key] = entry;

        entry.Status = JobStatus.Queued;
        entry.Started = false;
        entry.Done = entry.Total = 0;
        entry.OutPath = "";
        entry.Message = "";
        entry.Detail = "待機中";
        entry.AutoRetryCount = 0;
        entry.Subtitle = SwapKindLabel(entry.Subtitle, kind);

        _manager.Forget(jobId);
        Persist();
        Log("info", $"{entry.Title} を{label}として取得します");
        if (Settings.AutoStart) Start(jobId);
        MarkStateDirty();
        return true;
    }

    /// <summary>説明文に混じっている「動画」「音声」の表示だけを差し替える。</summary>
    private static string SwapKindLabel(string subtitle, string kind)
    {
        var label = MediaKind.Label(kind);
        var others = new[] { MediaKind.Label(MediaKind.Video), MediaKind.Label(MediaKind.Audio) };
        var parts = (subtitle ?? "").Split(" · ", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => others.Contains(p) ? label : p).ToList();
        if (!parts.Contains(label)) parts.Add(label);
        return string.Join(" · ", parts);
    }

    // ============================================================== ジョブ通知

    private JobEvents BuildEvents() => new()
    {
        Log = (level, message) => Post(() => Log(level, message)),
        Status = (jobId, status, message) => Post(() =>
        {
            if (!_byId.TryGetValue(jobId, out var e)) return;
            e.Status = status;
            e.Message = message;
            e.Detail = status switch
            {
                JobStatus.Fetching => "情報取得中",
                JobStatus.Downloading when e.Done == 0 => "取得中",
                JobStatus.Building => "PDF生成中",
                JobStatus.Processing => "変換中",
                _ => e.Detail,
            };
            MarkStateDirty();
        }),
        Meta = (jobId, meta) => Post(() =>
        {
            if (!_byId.TryGetValue(jobId, out var e)) return;
            if (meta.Title.Length > 0) e.Title = meta.Title;
            e.Tags = meta.Tags;

            // 動画側は組み立て済みの説明文を送ってくる。ページ数のような
            // ギャラリー固有の項目は持たないので、無ければ触らない。
            if (meta.Subtitle is not null)
            {
                e.Subtitle = meta.Subtitle;
            }
            else
            {
                var bits = new List<string>();
                if (meta.Artists.Count > 0) bits.Add(string.Join("、", meta.Artists.Take(2)));
                if (meta.Series.Count > 0) bits.Add(meta.Series[0]);
                bits.Add($"{meta.Pages}ページ");
                e.Subtitle = string.Join(" · ", bits);
            }
            if (meta.Pages > 0) e.Total = meta.Pages;
        }),
        // 速度計は自前で同期しているので、UI スレッドへ渡さずそのまま足す。
        // ここを Dispatcher 経由にすると、画像1枚ごとに UI へ用事が増える。
        Bytes = bytes => Speed.Add(bytes),
        // 進捗は件数が多いので、緩衝しておいてタイマーでまとめて反映する
        Progress = (jobId, done, total, detail) =>
            _pendingProgress[jobId] = new PendingProgress
            {
                Done = done, Total = total, Detail = detail,
            },
        Thumbnail = (jobId, data) => Post(() =>
        {
            if (!_byId.TryGetValue(jobId, out var e)) return;
            e.Thumb = DecodeThumb(data);
        }),
        Finished = (jobId, result) => Post(() => OnFinished(jobId, result)),
    };

    /// <summary>
    /// UI スレッドへ渡す。優先度を下げてあるので、入力（クリック・ドラッグ）が
    /// 常に先に処理される。件数が増えても操作が引っかからない。
    /// </summary>
    private void Post(Action action)
    {
        if (_disposed) return;
        try { _ui.InvokeAsync(action, DispatcherPriority.Background); }
        catch (TaskCanceledException) { }
    }

    private static BitmapSource? DecodeThumb(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();                 // 別スレッドから触れるようにする
            return image;
        }
        catch (Exception)
        {
            return null;                    // 表紙が出ないだけ。本体には影響しない。
        }
    }

    private void OnFinished(string jobId, JobResult result)
    {
        if (!_byId.TryGetValue(jobId, out var entry)) return;

        _pendingProgress.TryRemove(jobId, out _);

        var title = entry.Title.Length > 0 ? entry.Title : entry.Reference.Url;
        var label = MediaKind.ResultLabel(entry.Reference.Kind);

        if (result.Ok)
        {
            entry.OutPath = result.Path;
            long got = Math.Max(0, entry.Total - result.Missing);

            if (result.Missing > 0)
            {
                // 欠けたまま履歴に入れると二度と取り直せなくなるので記録しない
                entry.Status = JobStatus.Partial;
                entry.Detail = $"{got}/{entry.Total}ページ";
                entry.Message = $"{result.Missing}ページ取得できませんでした（再試行できます）";

                if (!TryScheduleAutoRetry(jobId, entry, JobStatus.Partial,
                        title, $"{result.Missing}ページ取得できませんでした"))
                {
                    Log("warn", $"一部欠落: {Path.GetFileName(entry.OutPath)}（{got}/{entry.Total}）");
                    Notify("一部のページを取得できませんでした",
                           $"{title}（{got}/{entry.Total}ページ）", "warning");
                }
            }
            else
            {
                entry.Status = JobStatus.Done;
                entry.Detail = "完了";
                entry.Done = entry.Total > 0 ? entry.Total : entry.Done;
                _history.Add(entry.Reference.Key, entry.OutPath);
                Library.Add(entry.OutPath);      // 次の追加からすぐ効かせる
                Log("ok", $"保存しました: {Path.GetFileName(entry.OutPath)}");
                Notify($"{label}を保存しました", title, "success");
            }

            if (entry.Reference.Site == "web") LearnWhitelist(entry.Reference.Url);
        }
        else if (entry.Status != JobStatus.Canceled)
        {
            entry.Status = JobStatus.Failed;
            entry.Detail = "失敗";
            entry.Message = result.Error.Length > 0 ? result.Error : "失敗しました";
            // 情報取得の段階で失敗すると説明文が「取得しています…」のまま残る。
            // 終わったのに進行中の文言が出ていると、状態を読み違える。
            if (entry.Subtitle.EndsWith("情報を取得しています…", StringComparison.Ordinal))
            {
                var head = MediaKind.Label(entry.Reference.Kind);
                entry.Subtitle = head.Length > 0 ? $"{head} · 取得できませんでした"
                                                 : "取得できませんでした";
            }

            // ログイン要求・非対応種別など「待っても変わらない」失敗（result.Permanent）は
            // TryScheduleAutoRetry の中で弾かれる。それ以外は瞬断やサイト側の一時的な
            // 不調を疑って自動で数回まで再試行する。
            if (!TryScheduleAutoRetry(jobId, entry, JobStatus.Failed, title, entry.Message,
                    permanent: result.Permanent))
            {
                Notify("ダウンロードに失敗しました",
                       entry.Message.Length > 0 ? entry.Message : title, "error");

                // site="web" は既知サイトではなく汎用として拾ったことの目印。
                // それすら失敗したなら、次回からクリップボードで同じドメインを
                // 拾わないよう学習する（手動貼り付けには影響しない。自動再試行の
                // 途中経過ではなく、最終的に失敗が確定した時点でのみ学習する）。
                if (entry.Reference.Site == "web") LearnBlacklist(entry.Reference.Url);
            }
        }
        else
        {
            entry.Detail = "キャンセル";
            entry.Message = "キャンセルしました";
        }

        entry.Started = false;
        _manager.Forget(jobId);
        Persist();
        MarkStateDirty();
    }

    /// <summary>汎用として拾って失敗したドメインを除外リストへ足す。</summary>
    private void LearnBlacklist(string url)
    {
        var host = UrlDetect.HostOf(url);
        if (host.Length == 0) return;
        if (WhitelistHosts().Contains(host)) return;    // 実績があるので1回の失敗では外さない
        if (BlacklistHosts().Contains(host)) return;    // 既に除外済み

        _settings.Update(s =>
        {
            var raw = (s.ClipboardBlacklist ?? "").Trim();
            s.ClipboardBlacklist = raw.Length > 0 ? $"{raw}, {host}" : host;
        });
        Log("info", $"クリップボード監視の除外リストに {host} を追加しました" +
                    "（取得できなかったため。設定から編集できます）");
        MarkStateDirty();
    }

    /// <summary>汎用として拾って実際に成功したドメインを信頼リストへ足す。</summary>
    private void LearnWhitelist(string url)
    {
        var host = UrlDetect.HostOf(url);
        if (host.Length == 0) return;

        bool alreadyWhite = WhitelistHosts().Contains(host);
        bool wasBlack = BlacklistHosts().Contains(host);
        if (alreadyWhite && !wasBlack) return;

        _settings.Update(s =>
        {
            if (wasBlack)
            {
                var kept = UrlDetect.ParseHostList(s.ClipboardBlacklist)
                    .Where(h => h != host).ToList();
                s.ClipboardBlacklist = string.Join(", ", kept);
            }
            if (!alreadyWhite)
            {
                var raw = (s.ClipboardWhitelist ?? "").Trim();
                s.ClipboardWhitelist = raw.Length > 0 ? $"{raw}, {host}" : host;
            }
        });

        if (wasBlack)
            Log("info", $"クリップボード監視の除外リストから {host} を外しました（取得に成功したため）");
        if (!alreadyWhite)
            Log("info", $"クリップボード監視の信頼リストに {host} を追加しました（取得に成功したため）");
        MarkStateDirty();
    }

    // ============================================================== 保存と復元

    private void RestoreQueue()
    {
        var records = _queueStore.Records();
        if (records.Count == 0) return;

        int restored = 0, broken = 0;
        var created = new List<EntryVm>();

        foreach (var rec in records)
        {
            // 1件でも壊れていると起動できない、という状態にはしない
            try
            {
                var entry = RestoreOne(rec);
                if (entry is not null) { created.Add(entry); restored++; }
            }
            catch (Exception)
            {
                broken++;
            }
        }

        Entries.AddRange(created);

        if (broken > 0) Log("warn", $"復元できなかった記録を {broken} 件読み飛ばしました");
        Persist();                          // 読み飛ばした分をファイルからも掃除する
        if (restored == 0) return;

        // 起動と同時に大きなダウンロードを始めない。終了時に中止を選んだ人の
        // 判断を覆さないため、再開は明示的な操作に任せる。
        Log("info", $"前回の未完了 {restored} 件を復元しました（自動では再開しません）");
        int waiting = created.Count(e => e.Status == JobStatus.Queued);
        Notify($"未完了の {restored} 件を復元しました",
               waiting > 0 ? $"{waiting}件が待機中です。「待機中を開始」で再開できます。"
                           : "カードから再試行できます。");
    }

    private EntryVm? RestoreOne(QueueRecord rec)
    {
        if (!Enum.TryParse<JobStatus>(rec.Status, out var status)) status = JobStatus.Queued;
        // 中断された作業は「待機中」に戻す。途中まで落とした一時ファイルは
        // 起動時に掃除済みなので、続きからではなく最初からやり直す。
        if (StatusText.IsActive(status)) status = JobStatus.Queued;

        // 知らない種別は PDF 扱いに倒す。中途半端な値でメディア側へ回すと
        // 復元したカードが必ず失敗する。
        var kind = MediaKind.IsMedia(rec.Kind) ? rec.Kind : MediaKind.None;
        var reference = new SourceRef(rec.Site, rec.Gid ?? "", rec.Url, kind);

        if (_byKey.ContainsKey(reference.Key)) return null;   // 記録が重複していても行は増やさない

        var entry = NewEntry(reference, status);
        entry.Done = Math.Max(0, rec.Done);
        entry.Total = Math.Max(0, rec.Total);
        entry.OutPath = rec.OutPath ?? "";
        entry.Message = rec.Message ?? "";
        var savedTitle = rec.Title ?? "";
        var savedSubtitle = rec.Subtitle ?? "";
        entry.Title = savedTitle.Length > 0 ? savedTitle : reference.Url;
        entry.Subtitle = savedSubtitle.Length > 0 ? savedSubtitle : "前回の続き";
        entry.Detail = status == JobStatus.Partial && entry.Total > 0
            ? $"{entry.Done}/{entry.Total}ページ"
            : StatusText.Label(status);
        return entry;
    }

    // ============================================================== 設定

    public void SetWatch(bool enabled)
    {
        _settings.Update(s => s.WatchClipboard = enabled);
        Clipboard.SetEnabled(enabled);
        MarkStateDirty();
    }

    public bool ToggleWatch()
    {
        bool enabled = !Clipboard.IsEnabled;
        SetWatch(enabled);
        Log("info", enabled ? "クリップボード監視を開始しました" : "クリップボード監視を停止しました");
        return enabled;
    }

    public void SaveSettings(SettingsData values)
    {
        var before = Settings;
        bool foldersChanged = before.OutputDir != values.OutputDir
                              || before.VideoDir != values.VideoDir
                              || before.AudioDir != values.AudioDir
                              || before.ScanDirs != values.ScanDirs;

        _settings.Replace(values);
        var s = Settings;
        if (foldersChanged) RescanLibrary();
        _manager.SetWorkers(DownloadManager.LaneGallery, s.GalleryWorkers);
        _manager.SetWorkers(DownloadManager.LaneMedia, s.VideoWorkers);
        Clipboard.SetEnabled(s.WatchClipboard);
        Log("info", "設定を保存しました");
        MarkStateDirty();
    }

    // ============================================================== ファイル操作

    public EntryVm? Find(string jobId) => _byId.GetValueOrDefault(jobId);

    private string ResolvePath(EntryVm entry) =>
        entry.OutPath.Length > 0 ? entry.OutPath
        : _history.PathFor(entry.Reference.Key) ?? "";

    public bool OpenResult(EntryVm entry)
    {
        var path = ResolvePath(entry);
        if (path.Length > 0 && File.Exists(path))
        {
            Shell(path);
            return true;
        }
        Notify($"{MediaKind.ResultLabel(entry.Reference.Kind)}が見つかりません",
               "移動または削除されたようです。再試行できます。", "warning");
        return false;
    }

    public bool RevealResult(EntryVm entry)
    {
        var path = ResolvePath(entry);
        if (path.Length > 0 && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { "/select,", Path.GetFullPath(path) },
                    UseShellExecute = false,
                });
                return true;
            }
            catch (Exception) { /* 開けなければ保存先までは案内する */ }
        }
        return OpenOutputDir(entry.Reference.Kind);
    }

    public bool OpenPage(EntryVm entry)
    {
        Shell(entry.Reference.Url);
        return true;
    }

    public bool OpenOutputDir(string kind = "")
    {
        var target = MediaKind.IsMedia(kind)
            ? AppSettings.MediaDir(Settings, kind)
            : Settings.OutputDir;
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notify("フォルダを開けません", e.Message, "error");
            return false;
        }
        Shell(target);
        return true;
    }

    private void Shell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Notify("開けませんでした", e.Message, "error");
        }
    }

    // ============================================================== 終了

    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;

        Clipboard.Dispose();
        _pump.Stop();

        // URL解析中のものがあれば打ち切る。大量のテキストを解析している
        // 途中でも、終了操作そのものは即座に返るようにする。
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _resolver.Complete();

        _manager.Shutdown();

        // ジョブの中止で書き換わった分もまとめて、確実にディスクへ書いてから終わる
        PersistNow();
        _queueStore.Flush();
        _settings.Flush();
        _history.Flush();
    }

    public void Dispose()
    {
        Shutdown();
        _manager.Dispose();
        _client.Dispose();
        try { _shutdown.Dispose(); } catch (ObjectDisposedException) { }
    }
}
