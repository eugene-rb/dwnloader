using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dwnloader.Core;
using Dwnloader.Sites;
using Dwnloader.Jobs;

namespace Dwnloader;

/// <summary>
/// Python 版の出力と機械的に突き合わせるための出力を作る（`Dwnloader.exe --dump`）。
/// 移植で答えがずれていないことを、思い込みではなく実データで確かめるために使う。
/// </summary>
public static class Compare
{
    public static int Run()
    {
        SelfTest.EnsureConsole();
        Console.OutputEncoding = Encoding.UTF8;

        var isUrl = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var u in new[] { "http://example.com/x", "https://example.com",
                                  "example.com/x", "https://ex ample.com", "https://nodot",
                                  "http://localhost:8080/x", "ftp://example.com" })
            isUrl[u] = UrlDetect.IsUrl(u);

        var resolve = new SortedDictionary<string, string[]?>(StringComparer.Ordinal);
        foreach (var u in new[] {
            "https://hitomi.la/doujinshi/title-here-1234567.html",
            "https://hitomi.la/reader/999.html",
            "https://www.pixiv.net/artworks/12345678",
            "https://www.pixiv.net/en/artworks/555",
            "https://www.pixiv.net/member_illust.php?mode=medium&illust_id=777",
            "https://momon-ga.com/fanzine/mo123456",
            "https://momon-ga.com/tag/mo-abc",
            "https://example.com/whatever" })
        {
            var r = SiteRegistry.Resolve(u);
            resolve[u] = r is null ? null : new[] { r.Site, r.Gid, r.Url };
        }

        var meta = new GalleryMeta
        {
            Site = "hitomi", Gid = "1234", Title = "作品タイトル",
            Artists = new[] { "作者A", "作者B" }, Groups = new[] { "サークル" },
            Series = new[] { "シリーズ" }, Language = "日本語",
        };
        meta.Images = new[] { new ImageRef { Index = 1 }, new ImageRef { Index = 2 } };
        var noArtist = new GalleryMeta { Site = "s", Gid = "1", Title = "T" };

        var dates = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var v in new[] { "2024-05-01 12:34:56", "2024-05-01T12:34:56",
                                  "2024-05-01 12:34", "2024-05-01",
                                  "2024-05-01T12:00:00+09:00", "2024-05-01T12:00:00-05",
                                  "2024-05-01T12:00:00Z", "なんでもない文字列", "",
                                  "2024-13-01" })
            dates[v] = Iso(Util.ParseDateTime(v), v);

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["find_urls_count"] = UrlDetect.FindUrls(
                "見て https://hitomi.la/doujinshi/aaa-123456.html と " +
                "https://www.pixiv.net/artworks/98765 だよ").Count,
            ["is_url"] = isUrl,
            ["resolve"] = resolve,
            ["sanitize"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["chars"] = Util.SanitizeFilename("a/b\\c:d*e?f\"g<h>i|j"),
                ["control"] = Util.SanitizeFilename("abc"),
                ["spaces"] = Util.SanitizeFilename("a   b"),
                ["trailing"] = Util.SanitizeFilename("name. . "),
                ["reserved"] = Util.SanitizeFilename("CON"),
                ["empty"] = Util.SanitizeFilename("   "),
                ["maxlen"] = Util.SanitizeFilename(new string('あ', 200), 10).Length,
            },
            ["format"] = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["default"] = Util.FormatFilename("{title} [{artist}] ({site}-{id})", meta),
                ["pages"] = Util.FormatFilename("{title} {pages}p", meta),
            },
            ["format_empty"] = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["brackets"] = Util.FormatFilename("{title} [{group}] ({site}-{id})", noArtist),
            },
            ["human"] = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["512"] = Util.HumanSize(512),
                ["2048"] = Util.HumanSize(2048),
                ["5mb"] = Util.HumanSize(5 * 1024 * 1024),
            },
            ["dates"] = dates,
            ["keys"] = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["gallery"] = new SourceRef("hitomi", "123", "u").Key,
                ["video"] = new SourceRef("youtube", "abc", "u", MediaKind.Video).Key,
                ["audio"] = new SourceRef("youtube", "abc", "u", MediaKind.Audio).Key,
            },
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        }));
        return 0;
    }


    /// <summary>
    /// 実際のサーバとデータで、出荷するコードそのものを通す（`--live`）。
    /// 自己テストは純粋な関数しか触れないので、gg.js の実物・AVIF の実復号・
    /// PdfSharp の実出力はここでしか確かめられない。
    /// </summary>
    public static async Task<int> RunLiveAsync()
    {
        SelfTest.EnsureConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 実データでの検証 ===");
        Console.WriteLine();

        int failed = 0;
        using var client = Net.CreateClient();
        var settings = new SettingsData();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ctx = new SiteContext { Client = client, Settings = settings, Cancel = cts.Token };

        // --- 1. 実際の gg.js を、出荷するパーサで解析する ---
        Console.WriteLine("[1] hitomi の gg.js を実サーバから取得して解析");
        try
        {
            var adapter = new HitomiAdapter();
            var url = "https://ltn.gold-usergeneratedcontent.net/gg.js";
            var resp = await Net.GetWithRetryAsync(client, url,
                new Dictionary<string, string> { ["Referer"] = "https://hitomi.la/" },
                settings.Timeout, settings.Retries, null, cts.Token);

            Console.WriteLine($"    HTTP {resp.StatusCode}, {resp.Body.Length} バイト");
            if (resp.StatusCode != 200)
            {
                Console.WriteLine("    [失敗] gg.js を取得できなかった");
                failed++;
            }
            else
            {
                // 出荷するアダプタの経路で URL を組み立てさせる
                var built = await adapter.BuildProbeUrlAsync(ctx);
                Console.WriteLine($"    組み立てた画像URL: {built}");
                if (!built.StartsWith("https://a", StringComparison.Ordinal)
                    && !built.StartsWith("https://w", StringComparison.Ordinal))
                {
                    Console.WriteLine("    [失敗] URL の形が想定と違う");
                    failed++;
                }
                else
                {
                    Console.WriteLine("    [成功] 実物の gg.js から URL を組み立てられた");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"    [失敗] {e.GetType().Name}: {e.Message}");
            failed++;
        }

        // --- 2. AVIF/WebP を出荷する経路で復号し、PDF まで作る ---
        Console.WriteLine();
        Console.WriteLine("[2] AVIF/WebP → JPEG → PDF（出荷する ImagePipeline / PdfBuilder）");
        var tmp = Path.Combine(Path.GetTempPath(), "dwnloader-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var jpegs = new List<string>();
            foreach (var (fmt, i) in new[] { ("avif", 1), ("webp", 2), ("png", 3) })
            {
                var src = MakeSample(fmt);
                Console.WriteLine($"    {fmt}: {src.Length} バイトを生成");
                var dest = Path.Combine(tmp, $"{i:D5}.jpg");
                ImagePipeline.NormalizeToJpeg(src, dest, settings.JpegQuality);
                var len = new FileInfo(dest).Length;
                Console.WriteLine($"      → JPEG {len} バイト");
                if (len == 0) { failed++; Console.WriteLine("      [失敗] 空になった"); }
                jpegs.Add(dest);
            }

            var thumb = ImagePipeline.MakeThumbnail(jpegs[0]);
            Console.WriteLine($"    サムネイル: {thumb.Length} バイト");
            if (thumb.Length == 0) { failed++; Console.WriteLine("    [失敗] サムネイルが空"); }

            var pdf = Path.Combine(tmp, "out.pdf");
            PdfBuilder.Build(jpegs, pdf, new PdfMeta
            {
                Title = "実データ検証 テスト作品",
                Author = "作者名",
                Keywords = new[] { "タグ1", "タグ2" },
                Creator = $"{AppInfo.Title} {AppInfo.Version}",
                Created = new DateTime(2024, 5, 1, 12, 0, 0),
            }, m => Console.WriteLine($"      警告: {m}"));

            var raw = File.ReadAllBytes(pdf);
            var ascii = Encoding.ASCII.GetString(raw);
            int pages = Regex.Matches(ascii, @"/Type\s*/Page[^s]").Count;
            bool dct = ascii.Contains("/DCTDecode");
            Console.WriteLine($"    PDF: {raw.Length} バイト, ページ数 {pages}, JPEG無再エンコード={dct}");

            if (pages != 3) { Console.WriteLine("    [失敗] ページ数が違う"); failed++; }
            if (!dct) { Console.WriteLine("    [失敗] JPEG が再エンコードされている"); failed++; }
            if (pages == 3 && dct) Console.WriteLine("    [成功] 3ページのPDFを無再エンコードで生成");
        }
        catch (Exception e)
        {
            Console.WriteLine($"    [失敗] {e.GetType().Name}: {e.Message}");
            failed++;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "実データ検証: すべて成功"
                                      : $"実データ検証: {failed} 件失敗");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>指定形式の実データを作る（合成だが、符号化・復号は本物を通る）。</summary>
    private static byte[] MakeSample(string fmt)
    {
        ImagePipeline.EnsureConfigured();
        var settings = new ImageMagick.MagickReadSettings
        {
            Width = 600, Height = 850,
        };
        using var img = new ImageMagick.MagickImage("xc:gray", settings);
        img.AddNoise(ImageMagick.NoiseType.Uniform);   // 単色だと圧縮の判定が効かない
        img.Format = fmt switch
        {
            "avif" => ImageMagick.MagickFormat.Avif,
            "webp" => ImageMagick.MagickFormat.WebP,
            _ => ImageMagick.MagickFormat.Png,
        };
        return img.ToByteArray();
    }


    /// <summary>
    /// 実際に yt-dlp を動かして1本落とす（`--media &lt;URL&gt;`）。
    ///
    /// 進捗と作品情報は区切り文字つきの1行として受け取っている。その区切りが
    /// コマンドラインを往復して無事に届くか、題名・進捗・保存先が実際に
    /// 取れるかは、本物を1本落とすまで確かめられない。
    /// </summary>
    public static async Task<int> RunMediaAsync(string url)
    {
        SelfTest.EnsureConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== yt-dlp 経路の実動作確認 ===");
        Console.WriteLine($"対象: {url}");
        Console.WriteLine();

        var found = YtDlp.Locate("");
        if (found is null)
        {
            Console.WriteLine("[失敗] yt-dlp が見つからない");
            return 1;
        }
        Console.WriteLine($"yt-dlp: {found.Description}");

        var tmp = Path.Combine(Path.GetTempPath(), "dwnloader-media-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);

        var settings = new SettingsData { VideoDir = tmp, AudioDir = tmp, Timeout = 60 };
        string title = "", subtitle = "", savedPath = "", lastDetail = "";
        int progressCount = 0, metaCount = 0;
        var statuses = new List<string>();

        var events = new JobEvents
        {
            Log = (lvl, msg) => Console.WriteLine($"    [{lvl}] {msg}"),
            Status = (_, st, _) => statuses.Add(st.ToString()),
            Meta = (_, m) => { metaCount++; title = m.Title; subtitle = m.Subtitle ?? ""; },
            Progress = (_, d, t, detail) => { progressCount++; lastDetail = detail; },
            Finished = (_, r) => { savedPath = r.Path; },
        };

        var reference = new SourceRef("test", "probe", url, MediaKind.Video);
        var job = new MediaJob("probe", reference, settings, events);
        await job.RunAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  状態の遷移      : {string.Join(" → ", statuses)}");
        Console.WriteLine($"  作品情報の受信  : {metaCount} 回");
        Console.WriteLine($"  題名            : {(title.Length > 0 ? title : "(取れず)")}");
        Console.WriteLine($"  説明            : {(subtitle.Length > 0 ? subtitle : "(取れず)")}");
        Console.WriteLine($"  進捗の受信      : {progressCount} 回  最後: {lastDetail}");
        Console.WriteLine($"  保存先          : {(savedPath.Length > 0 ? savedPath : "(取れず)")}");

        int failed = 0;
        if (savedPath.Length == 0 || !File.Exists(savedPath))
        {
            Console.WriteLine("  [失敗] ファイルが保存されなかった");
            failed++;
        }
        else
        {
            Console.WriteLine($"  ファイルの大きさ: {new FileInfo(savedPath).Length} バイト");
        }
        // ここが 0 なら区切り文字が届いていない（＝解析が全部捨てられている）
        if (metaCount == 0) { Console.WriteLine("  [失敗] 作品情報を1度も解析できなかった（区切り文字が届いていない疑い）"); failed++; }
        if (progressCount == 0) { Console.WriteLine("  [失敗] 進捗を1度も解析できなかった"); failed++; }
        if (title.Length == 0) { Console.WriteLine("  [失敗] 題名が取れなかった"); failed++; }
        if (!statuses.Contains("Downloading")) { Console.WriteLine("  [失敗] 取得中の状態に遷移しなかった"); failed++; }

        try { Directory.Delete(tmp, true); } catch (Exception) { }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "yt-dlp 経路: すべて成功" : $"yt-dlp 経路: {failed} 件失敗");
        return failed == 0 ? 0 : 1;
    }


    /// <summary>速度計の計算が合っているかを確かめる（`--speedtest`）。</summary>
    public static int RunSpeedTest()
    {
        SelfTest.EnsureConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 速度計の検証 ===");
        Console.WriteLine();

        int failed = 0;
        var meter = new SpeedMeter();

        // 1秒に 1MB ずつ入れたら 1MB/s と出るか
        for (int i = 0; i < 3; i++)
        {
            meter.Add(1024 * 1024);
            Thread.Sleep(1000);
            meter.Sample();
        }
        double mb = 1024.0 * 1024.0;
        Console.WriteLine($"  1MB/秒 を3回 → 現在 {Util.HumanSize(meter.Current)}/s");
        if (Math.Abs(meter.Current - mb) > mb * 0.15)
        {
            Console.WriteLine("  [失敗] 想定した速度と大きく違う");
            failed++;
        }
        Console.WriteLine($"  累計: {Util.HumanSize(meter.TotalBytes)}（期待 3.0 MB）");
        if (meter.TotalBytes != 3 * 1024 * 1024) { Console.WriteLine("  [失敗] 累計が合わない"); failed++; }

        // 何も入れなければ 0 に落ちるか
        Thread.Sleep(1000);
        meter.Sample();
        Console.WriteLine($"  何も入れない1秒後 → {Util.HumanSize(meter.Current)}/s");
        if (meter.Current > 1) { Console.WriteLine("  [失敗] 0 に戻らない"); failed++; }

        // 最大値は保持されるか
        Console.WriteLine($"  最大: {Util.HumanSize(meter.Peak)}/s");
        if (Math.Abs(meter.Peak - mb) > mb * 0.15) { Console.WriteLine("  [失敗] 最大値が合わない"); failed++; }

        // 保持数を超えても配列は伸びず、古い方から捨てられるか
        for (int i = 0; i < SpeedMeter.Capacity + 50; i++) meter.Sample();
        var buf = new double[SpeedMeter.Capacity];
        meter.CopyTo(buf, out int len);
        Console.WriteLine($"  {SpeedMeter.Capacity + 50} 回サンプル後の保持数: {len}（上限 {SpeedMeter.Capacity}）");
        if (len != SpeedMeter.Capacity) { Console.WriteLine("  [失敗] 保持数が上限と違う"); failed++; }
        if (meter.Peak > 1) { Console.WriteLine("  [失敗] 古い最大値が残り続けている"); failed++; }

        // 複数スレッドから同時に足しても数が合うか（実際の使われ方）
        meter.Reset();
        var threads = new List<Thread>();
        for (int t = 0; t < 8; t++)
        {
            var th = new Thread(() => { for (int i = 0; i < 10000; i++) meter.Add(100); });
            threads.Add(th); th.Start();
        }
        foreach (var th in threads) th.Join();
        long expected = 8L * 10000 * 100;
        Console.WriteLine($"  8スレッド同時加算: {meter.TotalBytes} バイト（期待 {expected}）");
        if (meter.TotalBytes != expected) { Console.WriteLine("  [失敗] 同時加算で数が合わない"); failed++; }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "速度計: すべて成功" : $"速度計: {failed} 件失敗");
        return failed == 0 ? 0 : 1;
    }


    /// <summary>
    /// 更新の確認・適用をコマンドラインから行う（`--checkupdate` / `--applyupdate`）。
    /// 画面を操作せずに一連の流れを確かめられるようにしてある。
    /// </summary>
    public static async Task<int> RunUpdateAsync(bool apply)
    {
        SelfTest.EnsureConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 更新の確認 ===");
        Console.WriteLine($"現在の版: v{AppInfo.Version}");

        var source = Environment.GetEnvironmentVariable("DWNLOADER_UPDATE_SOURCE");
        Console.WriteLine($"入手元  : {(string.IsNullOrWhiteSpace(source) ? AppInfo.RepositoryUrl : source)}");

        var service = new UpdateService();
        if (!service.IsSupported)
        {
            Console.WriteLine("インストーラ経由ではないため更新できません。");
            Console.WriteLine(UpdateService.UnsupportedReason);
            return 2;
        }

        string? found;
        try
        {
            found = await service.CheckAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.WriteLine($"確認できませんでした: {e.GetType().Name}: {e.Message}");
            return 1;
        }

        if (found is null)
        {
            Console.WriteLine("最新です。");
            return 0;
        }

        Console.WriteLine($"新しい版: v{found}");
        if (!apply) return 0;

        Console.WriteLine("取得しています…");
        int last = -1;
        await service.DownloadAsync(p =>
        {
            if (p / 10 != last / 10) { last = p; Console.WriteLine($"  {p}%"); }
        }).ConfigureAwait(false);

        Console.WriteLine("適用して再起動します。");
        service.ApplyAndRestart();      // ここから戻ってこない
        return 0;
    }

    /// <summary>
    /// Python の datetime.isoformat() と同じ書き方に揃える。
    /// 元の文字列に時差が書かれていたときだけ、その時差を付けて出す
    /// （Python 側は tzinfo 付きの datetime を返しているため）。
    /// </summary>
    private static string? Iso(DateTime? value, string source)
    {
        if (value is not { } dt) return null;

        // 時刻の直後に来る時差だけを見る。日付だけの "2024-05-01" の末尾 "-01" を
        // 時差と読み違えないよう、時:分が前にあることを条件にする。
        var m = Regex.Match(source, @"\d{2}:\d{2}(?::\d{2})?\s*(?:(Z)|([+-])(\d{2}):?(\d{2})?)\s*$");
        if (!m.Success)
            return dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        TimeSpan offset;
        if (m.Groups[1].Success)
        {
            offset = TimeSpan.Zero;
        }
        else
        {
            int h = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int mi = m.Groups[4].Success
                ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            offset = new TimeSpan(h, mi, 0);
            if (m.Groups[2].Value == "-") offset = -offset;
        }

        var naive = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        return new DateTimeOffset(naive, offset)
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
