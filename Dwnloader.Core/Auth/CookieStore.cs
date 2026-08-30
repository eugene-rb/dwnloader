using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dwnloader.Core;

namespace Dwnloader.Auth;

/// <summary>ログイン画面から受け取った Cookie 1つ分。</summary>
public sealed class StoredCookie
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "/";
    [JsonPropertyName("secure")] public bool Secure { get; set; }
    [JsonPropertyName("http_only")] public bool HttpOnly { get; set; }

    /// <summary>Unix 秒。0 はセッションCookie（期限なし扱い）。</summary>
    [JsonPropertyName("expires")] public long Expires { get; set; }

    public bool IsExpired(long nowUnix) => Expires > 0 && Expires <= nowUnix;

    /// <summary>同じ Cookie とみなす単位。サイトは名前・ドメイン・パスで1つを決める。</summary>
    public string Key => $"{Domain}\0{Path}\0{Name}";
}

/// <summary>1サイト分のログイン状態。</summary>
public sealed class StoredAccount
{
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("saved_at")] public DateTimeOffset SavedAt { get; set; }
    [JsonPropertyName("cookies")] public List<StoredCookie> Cookies { get; set; } = new();
}

internal sealed class AccountFile
{
    [JsonPropertyName("accounts")] public List<StoredAccount> Accounts { get; set; } = new();
}

/// <summary>
/// ログインで受け取った Cookie の置き場。
///
/// 保存先は DPAPI（現在の Windows ユーザーの鍵）で暗号化する。X の
/// <c>auth_token</c> は事実上アカウントそのもので、平文で置くと、設定ファイルを
/// 覗いた誰でも、あるいは同期フォルダへ紛れ込んだだけで乗っ取られる。
/// 別ユーザー・別PCへ持ち込まれた場合は復号に失敗するが、そのときは
/// 「未ログイン」として扱えばよく、ログインし直せば済む。
///
/// yt-dlp だけはファイルからしか Cookie を読めないため、そこへ渡す
/// cookies.txt は平文で書き出す（<see cref="CookiesTxtPath"/>）。
/// </summary>
public static class CookieStore
{
    public const string Pixiv = "pixiv";
    public const string Twitter = "twitter";

    /// <summary>DPAPI に渡す追加のエントロピー。他アプリの暗号文と取り違えない。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("dwnloader2/accounts/v1");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly object Gate = new();
    private static AccountFile? _cache;

    // ------------------------------------------------------------ 問い合わせ

    /// <summary>そのサイトのログインが残っているか。</summary>
    public static bool IsLoggedIn(string site)
    {
        lock (Gate)
        {
            var account = Find(Load(), site);
            return account is not null && Alive(account.Cookies).Count > 0;
        }
    }

    /// <summary>ログインした日時。無ければ null。</summary>
    public static DateTimeOffset? SavedAt(string site)
    {
        lock (Gate)
        {
            var account = Find(Load(), site);
            return account is null || account.Cookies.Count == 0 ? null : account.SavedAt;
        }
    }

    /// <summary>
    /// HTTP ヘッダに載せる形（`a=b; c=d`）。未ログインなら空文字。
    /// 期限切れは黙って外す（1つ切れただけで全部を捨てると、まだ使える
    /// セッションまで失うため）。
    ///
    /// <paramref name="onlyNames"/> を渡すと、その名前だけに絞る。
    /// ログイン時にはサイトの Cookie が丸ごと手に入るが、全部を送り返すのが
    /// 正しいとは限らない。Cloudflare の <c>cf_clearance</c> は取得したときの
    /// User-Agent と結び付いており、アプリ側の User-Agent で送ると
    /// かえって弾かれる。必要なものだけ選べるようにしておく。
    /// </summary>
    public static string Header(string site, params string[] onlyNames)
    {
        lock (Gate)
        {
            var account = Find(Load(), site);
            if (account is null) return "";

            var alive = Alive(account.Cookies);
            if (onlyNames.Length > 0)
            {
                var wanted = new HashSet<string>(onlyNames, StringComparer.Ordinal);
                alive = alive.Where(c => wanted.Contains(c.Name)).ToList();
            }
            return ToHeader(alive);
        }
    }

    // ------------------------------------------------------------ 更新

    /// <summary>ログイン結果を保存する。そのサイトの前回分は置き換える。</summary>
    public static void Save(string site, IEnumerable<StoredCookie> cookies)
    {
        var kept = Alive(cookies).ToList();

        lock (Gate)
        {
            var data = Load();
            data.Accounts.RemoveAll(a => a.Site == site);
            if (kept.Count > 0)
            {
                data.Accounts.Add(new StoredAccount
                {
                    Site = site,
                    SavedAt = DateTimeOffset.Now,
                    Cookies = kept,
                });
            }
            Write(data);
        }
    }

    /// <summary>そのサイトのログインを消す。</summary>
    public static void Clear(string site)
    {
        lock (Gate)
        {
            var data = Load();
            if (data.Accounts.RemoveAll(a => a.Site == site) > 0) Write(data);
        }
    }

    // ------------------------------------------------------------ yt-dlp 用

    /// <summary>
    /// 保存済みの Cookie から cookies.txt を書き出してパスを返す。
    /// ログインが1つも無ければ空文字（yt-dlp には何も渡さない）。
    ///
    /// 呼ばれるたびに書き直す。yt-dlp は `--cookies` に渡したファイルを
    /// 実行後に書き戻すので、こちらの保存内容と食い違ったまま古い方を
    /// 使い続けないようにする。
    /// </summary>
    public static string CookiesTxtPath()
    {
        string text;
        lock (Gate)
        {
            var all = Load().Accounts.SelectMany(a => Alive(a.Cookies)).ToList();
            if (all.Count == 0) return "";
            text = ToNetscape(all);
        }

        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            // BOM を付けない。1行目の "# Netscape HTTP Cookie File" は
            // yt-dlp 側が正規表現で見ており、BOM が挟まると読めなくなる。
            File.WriteAllText(AppPaths.CookiesTxtPath, text, new UTF8Encoding(false));
            return AppPaths.CookiesTxtPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------ 変換（テスト対象）

    /// <summary>HTTP の Cookie ヘッダの値を組み立てる。</summary>
    public static string ToHeader(IEnumerable<StoredCookie> cookies) =>
        string.Join("; ", cookies
            .Where(c => c.Name.Length > 0)
            .Select(c => $"{c.Name}={c.Value}"));

    /// <summary>
    /// Netscape 形式（cookies.txt）に直す。yt-dlp が読む形式で、
    /// タブ区切りの7項目、HttpOnly は行頭の目印で表す。
    /// </summary>
    internal static string ToNetscape(IEnumerable<StoredCookie> cookies)
    {
        // 1行目は決まり文句。ここが違うと yt-dlp は読み込みを拒否する。
        // 注記は ASCII に留める（読む側の文字コードの想定に依存しないため）。
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        sb.Append("# Generated by Dwnloader. Do not edit.\n\n");

        foreach (var c in cookies)
        {
            if (c.Name.Length == 0 || c.Domain.Length == 0) continue;

            var path = c.Path.Length > 0 ? c.Path : "/";
            // 先頭のドットは「サブドメインにも送る」印。ブラウザが返す形をそのまま使う。
            var includeSubdomains = c.Domain.StartsWith('.') ? "TRUE" : "FALSE";

            if (c.HttpOnly) sb.Append("#HttpOnly_");
            sb.Append(c.Domain).Append('\t')
              .Append(includeSubdomains).Append('\t')
              .Append(path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
              .Append(c.Expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>期限切れを外し、同じ Cookie は後から来た方を残す。</summary>
    public static List<StoredCookie> Alive(IEnumerable<StoredCookie> cookies)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var byKey = new Dictionary<string, StoredCookie>(StringComparer.Ordinal);
        foreach (var c in cookies)
        {
            if (c.Name.Length == 0 || c.IsExpired(now)) continue;
            byKey[c.Key] = c;
        }
        return byKey.Values.ToList();
    }

    // ------------------------------------------------------------ 読み書き

    private static StoredAccount? Find(AccountFile data, string site)
    {
        foreach (var a in data.Accounts)
            if (a.Site == site) return a;
        return null;
    }

    /// <summary>Gate を持った状態で呼ぶこと。</summary>
    private static AccountFile Load() => _cache ??= Read() ?? new AccountFile();

    private static AccountFile? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.AccountsPath)) return null;
            var blob = File.ReadAllBytes(AppPaths.AccountsPath);
            var json = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AccountFile>(json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or CryptographicException or JsonException
                                       or NotSupportedException)
        {
            // 復号できない＝別ユーザー/別PCの暗号文。未ログイン扱いにしておけば
            // ログインし直すだけで復帰できる。
            return null;
        }
    }

    private static void Write(AccountFile data)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
            var blob = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

            // 書き途中で落ちても前回分を壊さない
            var tmp = AppPaths.AccountsPath + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, AppPaths.AccountsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or CryptographicException)
        {
        }

        // yt-dlp 側の写しも作り直す。消えたログインを使い続けないようにする。
        try
        {
            if (data.Accounts.Count == 0 && File.Exists(AppPaths.CookiesTxtPath))
                File.Delete(AppPaths.CookiesTxtPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>テスト用。ディスクを読まずに状態を捨てる。</summary>
    internal static void ResetCacheForTest()
    {
        lock (Gate) _cache = null;
    }
}
