using System.Runtime.InteropServices;
using System.Text;
using Dwnloader.Auth;
using Dwnloader.Core;
using Dwnloader.Sites;

namespace Dwnloader;

/// <summary>
/// 移植の突き合わせ用の自己テスト（`Dwnloader.exe --selftest`）。
///
/// URL判定・ファイル名生成・日付解釈・gg.js 解析は Python 版と同じ答えを
/// 返さなければならない。ここが静かに壊れると、画面をいくら見ても気づけない
/// （例: 正規表現の先頭固定の違いで hitomi の全ページが落ちなくなる）。
/// </summary>
public static class SelfTest
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    private static int _passed;
    private static int _failed;

    /// <summary>親のコンソールへ書けるようにする。無ければ自分で開く。</summary>
    internal static void EnsureConsole()
    {
        if (!AttachConsole(-1)) AllocConsole();
    }

    public static int Run()
    {
        EnsureConsole();
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("=== dwnloader C#版 自己テスト ===\n");

        TestUrlDetection();
        TestSiteMatching();
        TestFilenames();
        TestDateParsing();
        TestGgParsing();
        TestMediaMatching();
        TestSourceKeys();
        TestLibraryIndex();
        TestCookieStore();

        Console.WriteLine($"\n合計: {_passed} 件成功 / {_failed} 件失敗");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ 判定

    private static void Check(string label, object? actual, object? expected)
    {
        bool ok = Equals(actual?.ToString(), expected?.ToString());
        if (ok) { _passed++; return; }
        _failed++;
        Console.WriteLine($"  [失敗] {label}\n         期待: {expected}\n         実際: {actual}");
    }

    private static void Section(string name) => Console.WriteLine($"[{name}]");

    // ------------------------------------------------------------ 各テスト

    private static void TestUrlDetection()
    {
        Section("URL の切り出しと判定");

        var text = "見て https://hitomi.la/doujinshi/aaa-123456.html と " +
                   "https://www.pixiv.net/artworks/98765 だよ";
        var urls = UrlDetect.FindUrls(text);
        Check("2本のURLを拾う", urls.Count, 2);

        Check("http はURL", UrlDetect.IsUrl("http://example.com/x"), true);
        Check("https はURL", UrlDetect.IsUrl("https://example.com"), true);
        Check("スキーム無しはURLでない", UrlDetect.IsUrl("example.com/x"), false);
        Check("空白入りはURLでない", UrlDetect.IsUrl("https://ex ample.com"), false);
        Check("ドット無しホストはURLでない", UrlDetect.IsUrl("https://nodot"), false);
        Check("localhost はURL", UrlDetect.IsUrl("http://localhost:8080/x"), true);
        Check("ftp はURLでない", UrlDetect.IsUrl("ftp://example.com"), false);

        Check("ホスト名を小文字で取る", UrlDetect.HostOf("https://WWW.Example.COM/a"),
              "www.example.com");

        // サブドメインは別ドメインとして区別する
        var hosts = UrlDetect.ParseHostList("google.com, docs.google.com");
        Check("完全一致する", UrlDetect.HostMatches("https://google.com/x", hosts), true);
        Check("サブドメインは一致しない",
              UrlDetect.HostMatches("https://mail.google.com/x", hosts), false);
        Check("登録済みサブドメインは一致する",
              UrlDetect.HostMatches("https://docs.google.com/x", hosts), true);
    }

    private static void TestSiteMatching()
    {
        Section("ギャラリーサイトの判定");

        var hitomi = SiteRegistry.Resolve("https://hitomi.la/doujinshi/title-here-1234567.html");
        Check("hitomi を判定", hitomi?.Site, "hitomi");
        Check("hitomi の作品ID", hitomi?.Gid, "1234567");
        Check("hitomi の正規化URL", hitomi?.Url, "https://hitomi.la/reader/1234567.html");

        var reader = SiteRegistry.Resolve("https://hitomi.la/reader/999.html");
        Check("hitomi の reader 形式", reader?.Gid, "999");

        var pixiv = SiteRegistry.Resolve("https://www.pixiv.net/artworks/12345678");
        Check("pixiv を判定", pixiv?.Site, "pixiv");
        Check("pixiv の作品ID", pixiv?.Gid, "12345678");

        var pixivEn = SiteRegistry.Resolve("https://www.pixiv.net/en/artworks/555");
        Check("pixiv の言語付きURL", pixivEn?.Gid, "555");

        var pixivOld = SiteRegistry.Resolve(
            "https://www.pixiv.net/member_illust.php?mode=medium&illust_id=777");
        Check("pixiv の旧形式", pixivOld?.Gid, "777");

        var momo = SiteRegistry.Resolve("https://momon-ga.com/fanzine/mo123456");
        Check("momonga を判定", momo?.Site, "momonga");
        Check("momonga のスラッグ", momo?.Gid, "mo123456");

        // 一覧ページは作品ではない
        Check("momonga のタグ一覧は拾わない",
              SiteRegistry.Resolve("https://momon-ga.com/tag/mo-abc")?.Site, null);

        Check("無関係なURLは拾わない",
              SiteRegistry.Resolve("https://example.com/whatever")?.Site, null);
    }

    private static void TestFilenames()
    {
        Section("ファイル名の生成");

        Check("使えない文字を全角にする",
              Util.SanitizeFilename("a/b\\c:d*e?f\"g<h>i|j"),
              "a／b＼c：d＊e？f”g＜h＞i｜j");
        Check("制御文字を落とす", Util.SanitizeFilename("abc"), "abc");
        Check("連続する空白を1つにする", Util.SanitizeFilename("a   b"), "a b");
        Check("末尾のドットと空白を落とす", Util.SanitizeFilename("name. . "), "name");
        Check("予約名は代替に置き換える", Util.SanitizeFilename("CON"), "untitled");
        Check("空文字は代替に置き換える", Util.SanitizeFilename("   "), "untitled");
        Check("最大長で切る", Util.SanitizeFilename(new string('あ', 200), 10).Length, 10);

        var meta = new GalleryMeta
        {
            Site = "hitomi",
            Gid = "1234",
            Title = "作品タイトル",
            Artists = new[] { "作者A", "作者B" },
            Groups = new[] { "サークル" },
            Series = new[] { "シリーズ" },
            Language = "日本語",
        };
        meta.Images = new[] { new ImageRef { Index = 1 }, new ImageRef { Index = 2 } };

        Check("既定テンプレート",
              Util.FormatFilename("{title} [{artist}] ({site}-{id})", meta),
              "作品タイトル [作者A、作者B] (hitomi-1234)");
        Check("ページ数を埋め込む",
              Util.FormatFilename("{title} {pages}p", meta), "作品タイトル 2p");

        // 値が空のときに空の括弧が残らないこと
        var noArtist = new GalleryMeta { Site = "s", Gid = "1", Title = "T" };
        Check("空の括弧を掃除する",
              Util.FormatFilename("{title} [{group}] ({site}-{id})", noArtist), "T (s-1)");
        Check("未知のトークンは空にする",
              Util.FormatFilename("{title}{nope}", noArtist), "T");

        Check("バイト数の表示 (B)", Util.HumanSize(512), "512 B");
        Check("バイト数の表示 (KB)", Util.HumanSize(2048), "2.0 KB");
        Check("バイト数の表示 (MB)", Util.HumanSize(5 * 1024 * 1024), "5.0 MB");
    }

    private static void TestDateParsing()
    {
        Section("日付の解釈");

        Check("時差なし", Util.ParseDateTime("2024-05-01 12:34:56"),
              new DateTime(2024, 5, 1, 12, 34, 56));
        Check("T 区切り", Util.ParseDateTime("2024-05-01T12:34:56"),
              new DateTime(2024, 5, 1, 12, 34, 56));
        Check("秒なし", Util.ParseDateTime("2024-05-01 12:34"),
              new DateTime(2024, 5, 1, 12, 34, 0));
        Check("日付のみ", Util.ParseDateTime("2024-05-01"), new DateTime(2024, 5, 1));

        // 時差は「その時刻が現地でいつか」を保つ形で反映する
        Check("+09:00 の時差", Util.ParseDateTime("2024-05-01T12:00:00+09:00"),
              new DateTimeOffset(new DateTime(2024, 5, 1, 12, 0, 0),
                                 TimeSpan.FromHours(9)).DateTime);
        Check("-05 の時差（分なし）", Util.ParseDateTime("2024-05-01T12:00:00-05"),
              new DateTimeOffset(new DateTime(2024, 5, 1, 12, 0, 0),
                                 TimeSpan.FromHours(-5)).DateTime);
        Check("Z は UTC", Util.ParseDateTime("2024-05-01T12:00:00Z")?.Kind, DateTimeKind.Utc);

        Check("解釈できない文字列", Util.ParseDateTime("なんでもない文字列"), null);
        Check("空文字", Util.ParseDateTime(""), null);
        Check("null", Util.ParseDateTime(null), null);
        Check("あり得ない月", Util.ParseDateTime("2024-13-01"), null);
    }

    private static void TestGgParsing()
    {
        Section("hitomi の gg.js 解析（先頭固定の確認）");

        // 実物と同じ形。`case` と `o = N; break` は行頭にある。
        var sample = string.Join("\n",
            "var gg = {",
            "b: '1712345678/',",
            "m: function(g) {",
            "var o = 0;",
            "switch (g) {",
            "case 1:",
            "case 2:",
            "o = 1; break;",
            "case 3:",
            "o = 2; break;",
            "}",
            "return o;",
            "}",
            "};");

        var (b, map, def) = ParseGgForTest(sample);
        Check("b を取り出す", b, "1712345678/");
        Check("既定値を取り出す", def, 0);
        Check("対応表の件数", map.Count, 3);
        Check("case 1 -> 1", map.GetValueOrDefault(1), 1);
        Check("case 2 -> 1", map.GetValueOrDefault(2), 1);
        Check("case 3 -> 2", map.GetValueOrDefault(3), 2);

        // 行の途中にある "case" を拾ってしまうと対応表が壊れる。
        // Python の re.match は先頭固定なので拾わない。同じ挙動になっているか。
        var tricky = string.Join("\n",
            "b: 'x/',",
            "// この行には case 99: と o = 42; break; が書いてあるがコメント",
            "case 5:",
            "o = 7; break;");
        var (_, trickyMap, _) = ParseGgForTest(tricky);
        Check("行頭でない case は拾わない", trickyMap.ContainsKey(99), false);
        Check("行頭の case は拾う", trickyMap.GetValueOrDefault(5), 7);

        // 末尾3桁の読み方（順序を入れ替えていないか）
        Check("ハッシュのバケット計算", HashBucketForTest("abcdef012"), 0x201);
    }

    private static void TestMediaMatching()
    {
        Section("動画・音声サイトの判定");

        var yt = UrlDetect.MatchKnownSite("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Check("YouTube を判定", yt?.Site, "youtube");
        Check("YouTube の動画ID", yt?.Gid, "dQw4w9WgXcQ");

        // 短縮URLと通常URLが同じ作品として重複排除されること
        var shortUrl = UrlDetect.MatchKnownSite("https://youtu.be/dQw4w9WgXcQ");
        Check("youtu.be も同じIDになる", shortUrl?.Gid, yt?.Gid);

        var shorts = UrlDetect.MatchKnownSite("https://www.youtube.com/shorts/abc123XYZ");
        Check("YouTube Shorts", shorts?.Gid, "abc123XYZ");

        var x = UrlDetect.MatchKnownSite("https://x.com/someone/status/1234567890");
        Check("X を判定", x?.Site, "twitter");
        Check("X の投稿ID", x?.Gid, "1234567890");

        var tw = UrlDetect.MatchKnownSite("https://twitter.com/someone/status/1234567890");
        Check("twitter.com も同じIDになる", tw?.Gid, x?.Gid);

        var nico = UrlDetect.MatchKnownSite("https://www.nicovideo.jp/watch/sm9");
        Check("ニコニコを判定", nico?.Site, "niconico");
        Check("ニコニコの動画ID", nico?.Gid, "sm9");

        var direct = UrlDetect.MatchKnownSite("https://example.com/movie.mp4");
        Check("直リンクの動画を判定", direct?.Site, "file");

        Check("ただのページは判定しない",
              UrlDetect.MatchKnownSite("https://example.com/article"), null);

        // ハッシュは安定していること（同じURLなら毎回同じ）
        Check("URLハッシュは安定する",
              UrlDetect.Hashed("https://a.example/x"), UrlDetect.Hashed("https://a.example/x"));
    }


    /// <summary>
    /// ログインで受け取った Cookie の変換。cookies.txt の形式は yt-dlp 側が
    /// 厳密に見ており（1行目の決まり文句、タブ区切り7項目、期限は整数）、
    /// 1文字ずれるとファイルごと読み捨てられて「ログインしているのに
    /// 年齢制限で落ちない」という分かりにくい失敗になる。
    /// </summary>
    private static void TestCookieStore()
    {
        Section("ログイン Cookie の変換");

        var cookies = new List<StoredCookie>
        {
            new() { Name = "auth_token", Value = "abc", Domain = ".x.com", Path = "/",
                    Secure = true, HttpOnly = true, Expires = 1893456000 },
            new() { Name = "ct0", Value = "def", Domain = ".x.com", Path = "/",
                    Secure = true, HttpOnly = false, Expires = 0 },
        };

        Check("ヘッダを組み立てる", CookieStore.ToHeader(cookies), "auth_token=abc; ct0=def");

        var lines = CookieStore.ToNetscape(cookies).Split('\n');
        Check("1行目は決まり文句", lines[0], "# Netscape HTTP Cookie File");
        Check("HttpOnly には目印を付ける", lines[3],
              "#HttpOnly_.x.com\tTRUE\t/\tTRUE\t1893456000\tauth_token\tabc");
        Check("セッションCookie の期限は 0", lines[4],
              ".x.com\tTRUE\t/\tTRUE\t0\tct0\tdef");

        // 先頭にドットが無いドメインは、そのホストだけに送る
        var host = new List<StoredCookie>
        {
            new() { Name = "PHPSESSID", Value = "1_x", Domain = "www.pixiv.net", Path = "/",
                    Secure = true, HttpOnly = true, Expires = 1893456000 },
        };
        Check("サブドメインへ送らない印", CookieStore.ToNetscape(host).Split('\n')[3],
              "#HttpOnly_www.pixiv.net\tFALSE\t/\tTRUE\t1893456000\tPHPSESSID\t1_x");

        // 期限切れは落とし、同じ Cookie は後から来た方を残す
        long past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        var mixed = new List<StoredCookie>
        {
            new() { Name = "old", Value = "1", Domain = ".x.com", Path = "/", Expires = past },
            new() { Name = "ct0", Value = "古い", Domain = ".x.com", Path = "/" },
            new() { Name = "ct0", Value = "新しい", Domain = ".x.com", Path = "/" },
        };
        var alive = CookieStore.Alive(mixed);
        Check("期限切れと重複を落とす", alive.Count, 1);
        Check("後から来た値を残す", alive[0].Value, "新しい");

        // pixiv は未ログインでも PHPSESSID を配る。下線の有無で見分ける。
        var pixiv = LoginTarget.For(CookieStore.Pixiv);
        Check("未ログインの PHPSESSID は弾く",
              pixiv.LooksLoggedIn(new List<StoredCookie>
              {
                  new() { Name = "PHPSESSID", Value = "1beffb6c42345695c5f3c6f86059692e" },
              }), false);
        Check("ログイン後の PHPSESSID は通す",
              pixiv.LooksLoggedIn(new List<StoredCookie>
              {
                  new() { Name = "PHPSESSID", Value = "12345678_AbCdEf" },
              }), true);

        // X は auth_token と ct0 の両方が要る（yt-dlp の判定と揃える）
        var x = LoginTarget.For(CookieStore.Twitter);
        Check("auth_token だけでは足りない",
              x.LooksLoggedIn(new List<StoredCookie>
              {
                  new() { Name = "auth_token", Value = "abc" },
              }), false);
        Check("auth_token と ct0 が揃えば通す",
              x.LooksLoggedIn(new List<StoredCookie>
              {
                  new() { Name = "auth_token", Value = "abc" },
                  new() { Name = "ct0", Value = "def" },
              }), true);
    }

    private static void TestLibraryIndex()
    {
        Section("保存済みファイルからの作品特定");

        // ファイル名の末尾に入る (サイト-ID) を取り出せるか
        static string Show((string Site, string Id)? v) =>
            v is { } p ? $"{p.Site}/{p.Id}" : "(取れず)";

        Check("PDF の既定の保存名",
              Show(LibraryIndex.ParseStem("作品タイトル [作者A、作者B] (hitomi-1234567)")),
              "hitomi/1234567");
        Check("yt-dlp の保存名",
              Show(LibraryIndex.ParseStem("動画の題名 [投稿者] (Youtube-dQw4w9WgXcQ)")),
              "Youtube/dQw4w9WgXcQ");
        // YouTube の ID には「-」が入りうる。後ろで切ると壊れる。
        Check("ID にハイフンが入る場合",
              Show(LibraryIndex.ParseStem("題名 [誰か] (Youtube-a1b2-c3d4e)")),
              "Youtube/a1b2-c3d4e");
        // 同名回避の「(2)」は飛ばして、その手前を見る
        Check("連番が付いた名前",
              Show(LibraryIndex.ParseStem("作品 [作者] (hitomi-999) (2)")),
              "hitomi/999");
        // 題名自体に括弧があっても末尾を優先する
        Check("題名に括弧がある場合",
              Show(LibraryIndex.ParseStem("(単行本) 作品 [作者] (momonga-555)")),
              "momonga/555");
        Check("手掛かりが無い名前",
              Show(LibraryIndex.ParseStem("ただのファイル名")), "(取れず)");
        Check("括弧はあるがハイフンが無い",
              Show(LibraryIndex.ParseStem("作品 (2)")), "(取れず)");

        // 実際にファイルを置いて、索引が引けるかを確かめる
        var dir = Path.Combine(Path.GetTempPath(), "dwnloader-idx-" + Guid.NewGuid().ToString("N")[..8]);
        var sub = Path.Combine(dir, "hitomi");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllText(Path.Combine(sub, "作品A [作者] (hitomi-1234567).pdf"), "x");
            File.WriteAllText(Path.Combine(dir, "動画B [投稿者] (Youtube-dQw4w9WgXcQ).mp4"), "x");
            File.WriteAllText(Path.Combine(dir, "音声C [投稿者] (Youtube-aBcDeFgHiJk).mp3"), "x");
            File.WriteAllText(Path.Combine(dir, "関係ないもの.txt"), "x");

            var index = new LibraryIndex();
            index.Rebuild(new[] { dir }, CancellationToken.None);
            Check("索引に入った件数（txt は対象外）", index.Count, 3);

            // サブフォルダの中も見つかる
            Check("PDF を見つける",
                  index.TryFind(new SourceRef("hitomi", "1234567", "u"), out _), true);

            // yt-dlp は "Youtube"、こちらは "youtube"。大文字小文字で取り違えない
            Check("動画を見つける（サイト名の綴りが違っても）",
                  index.TryFind(new SourceRef("youtube", "dQw4w9WgXcQ", "u", MediaKind.Video),
                                out _), true);

            // 同じIDでも、動画と音声は別のものとして扱う
            Check("動画を音声として探すと当たらない",
                  index.TryFind(new SourceRef("youtube", "dQw4w9WgXcQ", "u", MediaKind.Audio),
                                out _), false);
            Check("音声を見つける",
                  index.TryFind(new SourceRef("youtube", "aBcDeFgHiJk", "u", MediaKind.Audio),
                                out _), true);

            // ギャラリーの短いIDは、別サイトの同じ番号と衝突させない
            Check("別サイトの同じ番号には当たらない",
                  index.TryFind(new SourceRef("pixiv", "1234567", "u"), out _), false);

            Check("無いものは見つからない",
                  index.TryFind(new SourceRef("hitomi", "0000", "u"), out _), false);

            // 落とし終わった直後に足せるか
            var added = Path.Combine(dir, "作品D [作者] (pixiv-98765432).pdf");
            File.WriteAllText(added, "x");
            index.Add(added);
            Check("完了直後に足したものが引ける",
                  index.TryFind(new SourceRef("pixiv", "98765432", "u"), out var got), true);
            Check("引いた場所が正しい", got, added);

            // 見つけた場所がそのまま開けること（カードの「開く」で使う）
            index.TryFind(new SourceRef("hitomi", "1234567", "u"), out var pdfPath);
            Check("見つけたファイルが実在する", File.Exists(pdfPath), true);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
        // フォルダ一覧の解釈（改行でもカンマでも受ける。前後の空白と引用符は落とす）
        var raw = "C:\\A\r\nC:\\B , \"C:\\C\" ";
        var folders = LibraryIndex.ParseFolderList(raw);
        Check("フォルダ一覧の件数", folders.Count, 3);
        Check("改行区切りを読む", folders[0], @"C:\A");
        Check("カンマ区切りも読む", folders[1], @"C:\B");
        Check("引用符と空白を落とす", folders[2], @"C:\C");
        Check("空欄は0件", LibraryIndex.ParseFolderList("").Count, 0);
    }

    private static void TestSourceKeys()
    {
        Section("キューの同一性キー");

        var gallery = new SourceRef("hitomi", "123", "https://hitomi.la/reader/123.html");
        Check("ギャラリーのキー", gallery.Key, "hitomi:123");

        var video = new SourceRef("youtube", "abc", "https://y/", MediaKind.Video);
        var audio = new SourceRef("youtube", "abc", "https://y/", MediaKind.Audio);
        Check("動画のキー", video.Key, "youtube:video:abc");
        Check("音声のキー", audio.Key, "youtube:audio:abc");
        Check("動画と音声は別扱い", video.Key == audio.Key, false);

        Check("ステータスの分類 (Queued は進行中)",
              StatusText.IsActive(JobStatus.Queued), true);
        Check("ステータスの分類 (Done は進行中でない)",
              StatusText.IsActive(JobStatus.Done), false);
        Check("ステータスの分類 (Failed は未完了)",
              StatusText.IsUnfinished(JobStatus.Failed), true);
        Check("ステータスの分類 (Done は保存しない)",
              StatusText.ShouldPersist(JobStatus.Done), false);
        Check("ステータスの分類 (Partial は保存する)",
              StatusText.ShouldPersist(JobStatus.Partial), true);
    }

    // ------------------------------------------------------------ 内部の再現

    // HitomiAdapter の private 実装と同じ手順。テストから直接叩けるよう写しておく。
    // 片方だけ直すと意味を失うので、変更するときは両方を揃えること。
    private static (string, Dictionary<int, int>, int) ParseGgForTest(string text)
    {
        var b = System.Text.RegularExpressions.Regex.Match(text, @"b\s*:\s*['""](.*?)['""]");
        var def = System.Text.RegularExpressions.Regex.Match(text, @"var\s+o\s*=\s*(\d+)");

        var table = new Dictionary<int, int>();
        var pending = new List<int>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var mc = System.Text.RegularExpressions.Regex.Match(line, @"^case\s+(\d+)\s*:");
            if (mc.Success) { pending.Add(int.Parse(mc.Groups[1].Value)); continue; }

            var ms = System.Text.RegularExpressions.Regex.Match(line, @"^o\s*=\s*(\d+)\s*;\s*break");
            if (ms.Success && pending.Count > 0)
            {
                int value = int.Parse(ms.Groups[1].Value);
                foreach (var g in pending) table[g] = value;
                pending.Clear();
            }
        }
        return (b.Success ? b.Groups[1].Value : "",
                table,
                def.Success ? int.Parse(def.Groups[1].Value) : 0);
    }

    private static int HashBucketForTest(string imageHash)
    {
        var s = string.Concat(imageHash[^1], imageHash[^3..^1]);
        return int.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }
}
