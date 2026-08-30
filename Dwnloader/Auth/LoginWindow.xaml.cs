using Dwnloader.Core;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Dwnloader.Auth;

/// <summary>
/// サイトのログイン画面をアプリの中に出し、済んだら Cookie を受け取る窓。
///
/// ユーザーがすることは「いつも通りログインする」だけで済むようにする。
/// DevTools を開いて PHPSESSID を写したり、拡張機能で cookies.txt を
/// 書き出したりという手順は、ここが肩代わりする。
///
/// ログイン状態は <see cref="AppPaths.WebViewDir"/> に残るので、次に開いたときは
/// 入力済みで開く（＝取り直しはボタン1つで終わる）。
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>Cookie を見に行く間隔。ログインは人の操作待ちなので、細かく見ても意味がない。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly LoginTarget _target;
    private readonly DispatcherTimer _poll;
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<LoginCheck?> _tcs = new();

    private bool _busy;
    private bool _finished;

    /// <summary>ログインを保存できたか。</summary>
    public bool Succeeded { get; private set; }

    /// <summary>画面に出す結果の一言。</summary>
    public string ResultMessage { get; private set; } = "";

    private LoginWindow(LoginTarget target)
    {
        InitializeComponent();
        _target = target;

        var appWindow = WindowInterop.GetAppWindow(this);
        appWindow.Resize(new Windows.Graphics.SizeInt32(900, 760));

        Title = $"{target.Label} にログイン";
        TitleText.Text = $"{target.Label} にログイン";
        HintText.Text = target.Hint;
        StatusText.Text = "読み込んでいます…";

        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += async (_, _) => await CheckAsync(auto: true);

        Closed += (_, _) =>
        {
            _poll.Stop();
            // Cancel だけにして Dispose はしない。確認の通信がまだ動いている
            // 最中に捨てると、そちらが例外で転ぶ（閉じただけなのに
            // 「確認できませんでした」が出る）。
            try { _closing.Cancel(); } catch (ObjectDisposedException) { }

            var result = Succeeded ? new LoginCheck(true, ResultMessage)
                : ResultMessage.Length > 0 ? new LoginCheck(false, ResultMessage) : null;
            _tcs.TrySetResult(result);
        };

        _ = InitializeAsync();
    }

    /// <summary>
    /// ログイン窓を開いて、閉じるまで待つ。
    ///
    /// 戻り値が null なら「ユーザーが自分で閉じた」。WebView2 が使えない環境では
    /// 例外にせず Ok=false を返すので、呼び出し側は手入力へ案内できる。
    /// </summary>
    public static Task<LoginCheck?> ShowAsync(LoginTarget target)
    {
        var window = new LoginWindow(target);
        window.Activate();
        return window._tcs.Task;
    }

    // ------------------------------------------------------------ 起動

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.WebViewDir);
            // WinUI3向けはC#/WinRTプロジェクション経由になり、3引数版は
            // CreateAsync ではなく CreateWithOptionsAsync という別名になる
            // （メタデータリーダーで実DLLを直接確認して判明）。
            var env = await CoreWebView2Environment
                .CreateWithOptionsAsync(null, AppPaths.WebViewDir, null);
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ResultMessage = "ログイン画面を開けませんでした（WebView2 ランタイムが必要です）: "
                          + ex.Message;
            Close();
            return;
        }

        Browser.CoreWebView2.Navigate(_target.StartUrl);
        StatusText.Text = "ログインをお待ちしています…";
        _poll.Start();
    }

    // ------------------------------------------------------------ 判定

    /// <summary>
    /// Cookie を集めて、ログイン済みの形になっていれば保存して閉じる。
    /// <paramref name="auto"/> が false なら「完了」ボタンからの手動確認。
    /// </summary>
    private async Task CheckAsync(bool auto)
    {
        if (_busy || _finished) return;
        _busy = true;
        try
        {
            var cookies = await HarvestAsync().ConfigureAwait(true);

            if (!_target.LooksLoggedIn(cookies))
            {
                if (!auto)
                {
                    StatusText.Text = "まだログインが確認できません。"
                                    + "ログインを済ませてから、もう一度「完了」を押してください。";
                }
                return;
            }

            _poll.Stop();
            StatusText.Text = "確認しています…";

            var check = await _target
                .VerifyAsync(CookieStore.ToHeader(cookies), _closing.Token)
                .ConfigureAwait(true);

            if (!check.Ok)
            {
                StatusText.Text = check.Message;
                _poll.Start();          // まだ望みがあるので見張りを続ける
                return;
            }

            CookieStore.Save(_target.Site, cookies);
            _finished = true;
            Succeeded = true;
            ResultMessage = check.Message;
            StatusText.Text = check.Message;

            // 成功した表示を一瞬見せてから閉じる（黙って消えると何が起きたか分からない）
            await Task.Delay(900, CancellationToken.None).ConfigureAwait(true);
            Close();
        }
        catch (OperationCanceledException)
        {
            // 窓が閉じられた
        }
        catch (Exception ex)
        {
            StatusText.Text = $"確認できませんでした: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>対象サイトの Cookie を集める。HttpOnly も含めて取れる。</summary>
    private async Task<List<StoredCookie>> HarvestAsync()
    {
        var core = Browser.CoreWebView2;
        var found = new List<StoredCookie>();
        if (core is null) return found;

        foreach (var url in _target.CookieUrls)
        {
            List<CoreWebView2Cookie> list;
            try
            {
                var got = await core.CookieManager.GetCookiesAsync(url);
                list = got.ToList();
            }
            catch (Exception e) when (e is InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                continue;               // 遷移中に呼ぶと失敗することがある。次の周期で拾う。
            }

            foreach (var c in list)
            {
                found.Add(new StoredCookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    Secure = c.IsSecure,
                    HttpOnly = c.IsHttpOnly,
                    Expires = ToUnix(c.Expires),
                });
            }
        }

        return CookieStore.Alive(found);
    }

    /// <summary>
    /// WebView2 の期限を Unix 秒に直す。WinUI3 向けの CoreWebView2Cookie.Expires は
    /// WPF版と違い、素の Unix 秒（double、-1 はセッションCookie）で返ってくる。
    /// </summary>
    private static long ToUnix(double expires)
    {
        if (expires <= 0) return 0;
        try
        {
            return checked((long)expires);
        }
        catch (OverflowException)
        {
            return 0;                   // 極端な値。セッション扱いで困らない。
        }
    }

    // ------------------------------------------------------------ ボタン

    private async void Done_Click(object sender, RoutedEventArgs e)
        => await CheckAsync(auto: false).ConfigureAwait(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 前のログインが残っていて先に進めないときの逃げ道。
    /// この窓の中の Cookie を消して、ログイン画面から出し直す。
    /// </summary>
    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        if (core is null) return;

        try
        {
            core.CookieManager.DeleteAllCookies();
            core.Navigate(_target.StartUrl);
            StatusText.Text = "ログインをお待ちしています…";
            if (!_finished) _poll.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"やり直せませんでした: {ex.Message}";
        }
    }
}
