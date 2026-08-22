using Dwnloader.Core;

namespace Dwnloader.Auth;

/// <summary>ログインを確かめた結果。<see cref="Ok"/> なら保存してよい。</summary>
public sealed record LoginCheck(bool Ok, string Message);

/// <summary>
/// サイトごとのログインの決まりごと。
///
/// 「ログインできたか」を URL の変化で判定する作りにはしない。pixiv は
/// ログイン後に複数回リダイレクトし、X は画面遷移そのものが JavaScript の中で
/// 起きるため、URL は当てにならない。実際に欲しいのは Cookie なので、
/// Cookie が出たかどうかを直接見る。
/// </summary>
public sealed class LoginTarget
{
    public required string Site { get; init; }
    public required string Label { get; init; }

    /// <summary>最初に開くページ。</summary>
    public required string StartUrl { get; init; }

    /// <summary>Cookie を集める対象。サブドメインごとに別々に持たれることがある。</summary>
    public required IReadOnlyList<string> CookieUrls { get; init; }

    /// <summary>画面に出す一言の案内。</summary>
    public required string Hint { get; init; }

    /// <summary>集めた Cookie がログイン済みの形をしているか。</summary>
    public required Func<IReadOnlyList<StoredCookie>, bool> LooksLoggedIn { get; init; }

    /// <summary>本当に使えるかをサイトへ聞く。通信できないときも Ok を返してよい。</summary>
    public required Func<string, CancellationToken, Task<LoginCheck>> VerifyAsync { get; init; }

    public static LoginTarget For(string site) => site switch
    {
        CookieStore.Pixiv => PixivTarget,
        CookieStore.Twitter => TwitterTarget,
        _ => throw new ArgumentException($"未知のサイトです: {site}", nameof(site)),
    };

    // ------------------------------------------------------------ pixiv

    private static readonly LoginTarget PixivTarget = new()
    {
        Site = CookieStore.Pixiv,
        Label = "pixiv",
        StartUrl = "https://accounts.pixiv.net/login?return_to=https%3A%2F%2Fwww.pixiv.net%2F",
        CookieUrls = new[] { "https://www.pixiv.net/" },
        Hint = "いつも通り pixiv にログインしてください。ログインを確認すると自動で閉じます。",

        // pixiv は未ログインでも PHPSESSID を配る（32桁の16進）。ログイン後の値は
        // `12345678_かなり長い文字列` の形になり、下線の有無で見分けられる。
        // ここを「PHPSESSID があるか」で判定すると、ログインしていなくても
        // 成功したことになってしまう。
        LooksLoggedIn = cookies => cookies.Any(
            c => c.Name == "PHPSESSID" && c.Value.Contains('_')),

        VerifyAsync = PixivVerifyAsync,
    };

    /// <summary>
    /// R-18 が実際に見えるかまで確かめる。
    ///
    /// pixiv にはアカウント側の「R-18作品を表示する」という設定があり、
    /// ログインできていてもこれが切れていると R-18 は取得できない。
    /// 年齢制限つきランキングの取得可否がそのまま判定になる（本文は捨てる）。
    /// </summary>
    private static async Task<LoginCheck> PixivVerifyAsync(string cookieHeader, CancellationToken ct)
    {
        using var client = Net.CreateClient(pool: 4);
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = cookieHeader,
            ["Referer"] = "https://www.pixiv.net/",
            ["Accept"] = "application/json",
        };

        try
        {
            var resp = await Net.GetWithRetryAsync(
                client, "https://www.pixiv.net/ranking.php?mode=daily_r18&format=json&p=1",
                headers, timeoutSeconds: 20, retries: 1, limiter: null, ct: ct)
                .ConfigureAwait(false);

            // 状態コードだけでは判断しない。HttpClient は転送を自動で追うので、
            // 弾かれてログイン画面へ飛ばされた場合も 200 で返ってくる。
            // 「ランキングの中身が JSON で入っているか」まで見て初めて、
            // R-18 が実際に見えていると言える。
            bool visible = resp.StatusCode == 200
                           && resp.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                           && resp.Text().Contains("\"contents\"", StringComparison.Ordinal);

            return visible
                ? new LoginCheck(true, "ログインしました。R-18作品も取得できます。")
                : new LoginCheck(true,
                    "ログインしました。ただし R-18作品は表示できない状態です。" +
                    "取得したい場合は pixiv の「ユーザー設定 → 表示」で R-18 の表示をオンにしてください。");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 確認できなかっただけ。ログイン自体は成立しているので保存する。
            return new LoginCheck(true, "ログインしました（R-18の確認はできませんでした）。");
        }
    }

    // ------------------------------------------------------------ X（旧Twitter）

    private static readonly LoginTarget TwitterTarget = new()
    {
        Site = CookieStore.Twitter,
        Label = "X（旧Twitter）",
        StartUrl = "https://x.com/i/flow/login",
        // 動画の取得は api.x.com を叩くため、そちらへ送られる分も集める。
        CookieUrls = new[] { "https://x.com/", "https://api.x.com/", "https://twitter.com/" },
        Hint = "いつも通り X にログインしてください。ログインを確認すると自動で閉じます。",

        // auth_token がログインの本体、ct0 は書き込み用の合言葉。
        // yt-dlp はこの2つが揃っていないとログイン状態として扱わない。
        LooksLoggedIn = cookies =>
            cookies.Any(c => c.Name == "auth_token" && c.Value.Length > 0)
            && cookies.Any(c => c.Name == "ct0" && c.Value.Length > 0),

        // X 側は問い合わせに専用の認証キーが要る。Cookie が揃っていることが
        // そのまま条件なので、余計な通信はしない。
        VerifyAsync = (_, _) => Task.FromResult(
            new LoginCheck(true, "ログインしました。センシティブな投稿も取得できます。")),
    };
}
