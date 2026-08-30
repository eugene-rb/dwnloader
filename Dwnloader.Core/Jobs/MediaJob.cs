using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dwnloader.Auth;
using Dwnloader.Core;

namespace Dwnloader.Jobs;

/// <summary>
/// 動画または音声を1本取得するまで。yt-dlp を外部プロセスとして駆動する。
///
/// 進捗は --progress-template で機械可読な1行として吐かせ、保存先は
/// --print after_move:filepath で受け取る。標準出力を行単位で読むだけなので、
/// yt-dlp が何をしていてもこちら側は詰まらない。
/// </summary>
public sealed partial class MediaJob : JobBase
{
    /// <summary>保存名。PDF 側の既定 `{title} [{artist}] ({site}-{id})` と揃えている。</summary>
    private const string OutputTemplate =
        "%(title)s [%(uploader,channel,creator|unknown)s] (%(extractor_key)s-%(id)s).%(ext)s";

    /// <summary>画質の指定。`<=?` は「その情報が無い形式を落とさない」ための書き方。</summary>
    private static readonly Dictionary<string, string> QualityFormat = new()
    {
        ["best"] = "bestvideo*+bestaudio/best",
        ["1080"] = "bestvideo[height<=?1080]+bestaudio/best[height<=?1080]/best",
        ["720"] = "bestvideo[height<=?720]+bestaudio/best[height<=?720]/best",
        ["480"] = "bestvideo[height<=?480]+bestaudio/best[height<=?480]/best",
    };

    // 行の種類を見分ける印。タイトルに現れない綴りを選ぶ。
    private const string ProgTag = "@@P@@";
    private const string DoneTag = "@@D@@";
    private const string MetaTag = "@@M@@";
    private const string Sep = "";        // Unit Separator。本文に現れない。

    private readonly bool _playlistAll;
    private bool _metaSent;
    private bool _processing;
    private long _lastProgressTicks;
    /// <summary>速度計へ渡した分の目印。yt-dlp は1本ごとに累計を0から数え直す。</summary>
    private long _reportedBytes;
    private string _path = "";
    private string _title = "";
    private readonly List<string> _errorLines = new();

    public MediaJob(string jobId, SourceRef reference, SettingsData settings, JobEvents events)
        : base(jobId, reference, settings, events)
    {
        // 走り出した後にトグルが変わっても、このジョブの動きは変えない
        _playlistAll = settings.PlaylistAll;
    }

    public override async Task RunAsync()
    {
        var resolved = YtDlp.Locate(Settings.YtDlpPath);
        if (resolved is null)
        {
            Log("error", YtDlp.InstallHint);
            // インストールされていない、というのは待っても解決しない。
            Finish(false, "", "yt-dlp が見つかりません", permanent: true);
            return;
        }

        Process? proc = null;
        try
        {
            Token.ThrowIfCancellationRequested();
            SetStatus(JobStatus.Fetching);

            var psi = BuildStartInfo(resolved);
            proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("yt-dlp を起動できませんでした");

            // 中止されたらプロセスごと畳む。ffmpeg を残さないよう木ごと落とす。
            await using var cancelReg = Token.Register(() =>
            {
                if (proc is { } p) YtDlp.KillTree(p);
            });

            var stdout = ReadLinesAsync(proc.StandardOutput, HandleStdout);
            var stderr = ReadLinesAsync(proc.StandardError, HandleStderr);

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (Token.IsCancellationRequested)
            {
                SetStatus(JobStatus.Canceled);
                Finish(false, "", "キャンセルしました");
                return;
            }

            if (proc.ExitCode != 0)
            {
                var message = _errorLines.Count > 0
                    ? _errorLines[^1]
                    : $"yt-dlp が異常終了しました (コード {proc.ExitCode})";
                Log("error", $"{Reference.Url}: {message}");
                Finish(false, "", message);
                return;
            }

            if (_path.Length == 0 || !File.Exists(_path))
            {
                const string message = "保存されたファイルを特定できませんでした";
                Log("error", $"{Reference.Url}: {message}");
                Finish(false, "", message);
                return;
            }

            Finish(true, _path, "");
        }
        catch (OperationCanceledException)
        {
            SetStatus(JobStatus.Canceled);
            Finish(false, "", "キャンセルしました");
        }
        catch (InvalidOperationException e)
        {
            // ffmpeg が無い等、環境の問題。待っても解決しない。
            Log("error", $"{Reference.Url}: {e.Message}");
            Finish(false, "", e.Message, permanent: true);
        }
        catch (Exception e)
        {
            if (Token.IsCancellationRequested)
            {
                SetStatus(JobStatus.Canceled);
                Finish(false, "", "キャンセルしました");
                return;
            }
            Log("error", $"{Reference.Url}: {e.GetType().Name}: {e.Message}");
            Finish(false, "", e.Message);
        }
        finally
        {
            if (proc is not null)
            {
                YtDlp.KillTree(proc);
                proc.Dispose();
            }
        }
    }

    // ------------------------------------------------------------ 起動引数

    private ProcessStartInfo BuildStartInfo(YtDlp.Resolved resolved)
    {
        var kind = MediaKind.IsMedia(Reference.Kind) ? Reference.Kind : MediaKind.Video;
        var outDir = AppSettings.MediaDir(Settings, kind);
        Directory.CreateDirectory(outDir);

        var ffmpeg = YtDlp.FfmpegPath();
        int retries = Settings.Retries;

        var psi = new ProcessStartInfo(resolved.Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 日本語のタイトルがそのままファイル名になる。UTF-8 を明示しないと
            // 標準出力が化け、保存先のパスを読み違える。
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = outDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        var a = psi.ArgumentList;
        foreach (var p in resolved.Prefix) a.Add(p);

        a.Add("--newline");
        a.Add("--no-colors");
        a.Add("--no-simulate");         // --print を付けても実際にダウンロードする
        a.Add("--progress");            // --print が付けた --quiet に打ち消されないように
        a.Add("--encoding"); a.Add("utf-8");

        // noplaylist は「動画とリストの両方を指すURL（watch?v=…&list=…）なら
        // 動画を採る」だけで、リスト単体のURLには効かない。全体を取るかどうかは
        // playlist-items で決める。
        a.Add("--no-playlist");
        if (!_playlistAll)
        {
            a.Add("--playlist-items"); a.Add("1");
        }
        else
        {
            // 1本が非公開・削除済みでもリスト全体を捨てない。理由はログに出る。
            a.Add("--ignore-errors");
        }

        a.Add("--retries"); a.Add(retries.ToString(CultureInfo.InvariantCulture));
        a.Add("--fragment-retries"); a.Add(retries.ToString(CultureInfo.InvariantCulture));
        a.Add("--socket-timeout");
        a.Add(Settings.Timeout.ToString("F0", CultureInfo.InvariantCulture));
        a.Add("--windows-filenames");
        a.Add("--trim-filenames");
        a.Add(Math.Max(40, Settings.FilenameMaxLen).ToString(CultureInfo.InvariantCulture));
        // 同名があれば落とし直さない。同じ動画で容量を二重に使わない。
        a.Add("--no-overwrites");
        a.Add("--continue");

        a.Add("-o"); a.Add(OutputTemplate);
        a.Add("-P"); a.Add(outDir);

        // 進捗（1行1件）。数値が取れない配信でも落ちないよう、既定値付きで書く。
        a.Add("--progress-template");
        a.Add(ProgTag +
              "%(progress.status)s" + Sep +
              "%(progress.downloaded_bytes|0)d" + Sep +
              "%(progress.total_bytes|0)d" + Sep +
              "%(progress.total_bytes_estimate|0)d" + Sep +
              "%(progress.speed|0)d" + Sep +
              "%(info.playlist_index|0)d" + Sep +
              "%(info.playlist_count|0)d" + Sep +
              "%(info.n_entries|0)d" + Sep +
              "%(info.vcodec|)s");

        // 作品情報（1本につき1回）。タイトルは最後に置き、区切り以降を全部題名として読む。
        a.Add("--print");
        a.Add("before_dl:" + MetaTag +
              "%(uploader,channel,creator|)s" + Sep +
              "%(duration|0)s" + Sep +
              "%(playlist_count|0)d" + Sep +
              "%(playlist_title,playlist|)s" + Sep +
              "%(title|)s");

        // 実際に保存されたファイル。後処理で拡張子が変わるので移動後の値を採る。
        a.Add("--print"); a.Add("after_move:" + DoneTag + "%(filepath)s");

        if (ffmpeg.Length > 0)
        {
            a.Add("--ffmpeg-location"); a.Add(ffmpeg);
        }

        // 設定で明示されたファイルが最優先。無ければアプリ内ログインの分を
        // cookies.txt に書き出して渡す（yt-dlp はファイル以外から読めない）。
        var cookies = (Settings.MediaCookiesFile ?? "").Trim();
        if (cookies.Length > 0 && !File.Exists(cookies))
        {
            Log("warn", $"Cookie ファイルが見つかりません: {cookies}");
            cookies = "";
        }
        if (cookies.Length == 0) cookies = CookieStore.CookiesTxtPath();

        if (cookies.Length > 0 && File.Exists(cookies))
        {
            a.Add("--cookies"); a.Add(cookies);
        }

        if (kind == MediaKind.Audio)
        {
            if (ffmpeg.Length == 0)
                throw new InvalidOperationException(
                    "音声の取り出しには ffmpeg が必要です（winget install Gyan.FFmpeg）");
            a.Add("-f"); a.Add("bestaudio/best");
            a.Add("--extract-audio");
            a.Add("--audio-format"); a.Add(Settings.AudioFormat);
            a.Add("--audio-quality");
            a.Add(Settings.AudioBitrate.ToString(CultureInfo.InvariantCulture) + "K");
        }
        else if (ffmpeg.Length > 0)
        {
            a.Add("-f"); a.Add(QualityFormat.GetValueOrDefault(Settings.VideoQuality,
                                                               QualityFormat["best"]));
            // 組み合わせが mp4 に入らないときは yt-dlp が mkv に切り替える
            a.Add("--merge-output-format"); a.Add("mp4");
        }
        else
        {
            // 映像と音声を結合できないので、最初から1つになっている形式に限る
            a.Add("-f"); a.Add("best[ext=mp4]/best");
            Log("warn", "ffmpeg が見つからないため、画質は結合不要の形式に限られます");
        }

        if (ffmpeg.Length > 0)
        {
            // タイトルや投稿者をファイル自身に書き込む（PDF の文書情報と同じ考え）
            a.Add("--embed-metadata");
        }

        a.Add("--");
        a.Add(Reference.Url);
        return psi;
    }

    // ------------------------------------------------------------ 出力の読み取り

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> handle)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try { handle(line); }
            catch (Exception) { /* 1行の解釈失敗でダウンロードは止めない */ }
        }
    }

    private void HandleStdout(string line)
    {
        if (line.StartsWith(ProgTag, StringComparison.Ordinal))
            HandleProgress(line[ProgTag.Length..]);
        else if (line.StartsWith(DoneTag, StringComparison.Ordinal))
            HandleDone(line[DoneTag.Length..]);
        else if (line.StartsWith(MetaTag, StringComparison.Ordinal))
            HandleMeta(line[MetaTag.Length..]);
    }

    private void HandleDone(string payload)
    {
        var path = payload.Trim();
        if (path.Length > 0 && path != "NA") _path = path;
    }

    private void HandleMeta(string payload)
    {
        var parts = payload.Split(Sep);
        if (parts.Length < 5) return;

        var uploader = parts[0].Trim();
        var duration = parts[1].Trim();
        int playlistCount = ParseInt(parts[2]);
        var playlistTitle = parts[3].Trim();
        // タイトルに区切り文字は現れないが、念のため残り全部を題名として扱う
        var title = string.Join(Sep, parts.Skip(4)).Trim();

        if (_metaSent) return;
        _metaSent = true;

        // リストの1本目の題名を出すと、以降ずっと嘘になる
        _title = playlistCount > 1 && playlistTitle.Length > 0 ? playlistTitle
               : title.Length > 0 ? title
               : Reference.Url;

        var bits = new List<string>();
        if (uploader.Length > 0) bits.Add(uploader);
        if (playlistCount > 1) bits.Add($"{playlistCount}本");
        else if (DurationText(duration) is { Length: > 0 } d) bits.Add(d);
        var kindLabel = MediaKind.Label(Reference.Kind);
        if (kindLabel.Length > 0) bits.Add(kindLabel);

        Events.Meta?.Invoke(JobId, new JobMeta
        {
            Title = _title,
            Subtitle = string.Join(" · ", bits),
        });
        SetStatus(JobStatus.Downloading);
    }

    private void HandleProgress(string payload)
    {
        var parts = payload.Split(Sep);
        if (parts.Length < 9) return;

        var status = parts[0].Trim();
        if (status is not ("downloading" or "finished")) return;

        long bytesDone = ParseLong(parts[1]);
        long bytesTotal = ParseLong(parts[2]);
        if (bytesTotal <= 0) bytesTotal = ParseLong(parts[3]);   // total_bytes_estimate

        // yt-dlp が報告するのは「そのファイルの累計」。次のファイルへ移ると
        // 0 から数え直すので、減っていたら新しいファイルが始まったとみなす。
        long delta = bytesDone - _reportedBytes;
        if (delta < 0) delta = bytesDone;
        if (delta > 0) Events.Bytes?.Invoke(delta);
        _reportedBytes = bytesDone;
        double speed = ParseLong(parts[4]);
        int playlistIndex = ParseInt(parts[5]);
        int playlistCount = Math.Max(ParseInt(parts[6]), ParseInt(parts[7]));
        var vcodec = parts[8].Trim();

        if (status == "finished")
        {
            bytesDone = bytesDone > 0 ? bytesDone : bytesTotal;
            _lastProgressTicks = 0;         // 次のファイルの1回目は間引かない
        }
        else
        {
            // yt-dlp は細かく吐く。UI へ流す間隔はこちらで間引く。
            var now = Environment.TickCount64;
            if (now - _lastProgressTicks < 200) return;
            _lastProgressTicks = now;
        }

        long done, total;
        if (playlistCount > 1)
        {
            // リストではバーを「何本目まで終わったか」で進める。1本ごとに
            // 巻き戻るバーは、止まったのか進んだのかが分からない。
            int index = Math.Max(1, playlistIndex);
            done = status == "finished" ? index : index - 1;
            total = playlistCount;
        }
        else
        {
            done = bytesDone;
            total = status == "finished" ? bytesDone : bytesTotal;
        }

        Events.Progress?.Invoke(JobId, done, total,
            Detail(playlistIndex, playlistCount, vcodec, bytesDone, bytesTotal, speed));
    }

    private string Detail(int index, int count, string vcodec,
                          long done, long total, double speed)
    {
        string head;
        if (count > 1) head = $"{Math.Max(1, index)}/{count}本目 ";
        // 映像と音声が別配信のとき、2本目だと分かるように
        else if (Reference.Kind == MediaKind.Video && vcodec == "none") head = "音声 ";
        else head = "";

        var size = total > 0
            ? $"{Util.HumanSize(done)} / {Util.HumanSize(total)}"
            : Util.HumanSize(done);
        return speed > 0 ? $"{head}{size} · {Util.HumanSize(speed)}/s" : $"{head}{size}";
    }

    private void HandleStderr(string line)
    {
        var text = CleanMessage(line);
        if (text.Length == 0) return;

        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            _errorLines.Add(text);
            Log("error", text);
        }
        else if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            Log("warn", text);
        }
        else if (IsPostProcessing(line))
        {
            MarkProcessing();
        }
    }

    private void MarkProcessing()
    {
        if (_processing) return;
        _processing = true;
        SetStatus(JobStatus.Processing);
        // ffmpeg の処理には割り込めない。押しても止まらない理由を先に出す。
        Log("info", "変換・結合中です（この間は中止できません）");
    }

    private static bool IsPostProcessing(string line) =>
        line.Contains("[Merger]", StringComparison.Ordinal)
        || line.Contains("[ExtractAudio]", StringComparison.Ordinal)
        || line.Contains("[VideoConvertor]", StringComparison.Ordinal)
        || line.Contains("[VideoRemuxer]", StringComparison.Ordinal);

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiCodes();

    [GeneratedRegex(@"^\s*(ERROR|WARNING):\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LevelPrefix();

    /// <summary>yt-dlp のメッセージから色指定と重複した見出しを落とす。</summary>
    private static string CleanMessage(string text)
    {
        var stripped = AnsiCodes().Replace(text ?? "", "").Trim();
        return LevelPrefix().Replace(stripped, "");
    }

    private static long ParseLong(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Max(0, v) : 0;

    private static int ParseInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Max(0, v) : 0;

    private static string DurationText(string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return "";
        int total = (int)secs;
        if (total <= 0) return "";
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
