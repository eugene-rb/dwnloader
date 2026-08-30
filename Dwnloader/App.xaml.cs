using System.IO;
using Dwnloader.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Dwnloader;

public partial class App : Application
{
    private SingleInstance? _guard;
    private AppSettings? _settings;
    private History? _history;
    private QueueStore? _queueStore;
    private Session? _session;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private UpdateService? _updates;
    private DispatcherQueue? _uiQueue;
    private bool _shuttingDown;

    public App()
    {
        // インストール・更新・アンインストールの後始末は、他の何よりも先に行う。
        // Velopack は本体を --veloapp-* 付きで呼び直して処理させるため、
        // 多重起動の判定より後ろに置くと「2つ目の起動」とみなされて素通りし、
        // ショートカットが作られない・消えないという形で壊れる。
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            LogCrash(ex);       // 更新まわりの失敗でアプリを起動不能にしない
        }

        InitializeComponent();

        // 想定外の例外でアプリごと消えないようにする。1件の失敗より、
        // 常駐が死ぬ方が困る。
        UnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        // WinUI3 の LaunchActivatedEventArgs はコマンドライン引数を運ばないため、
        // ここで自前に取得する（先頭はexeパスなので読み飛ばす）。
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // 自己テストは画面を出さずに走らせる（移植の突き合わせ用）
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(SelfTest.Run());
        }

        // Python 版との突き合わせ用に、同じ入力への答えを JSON で吐く
        if (args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(Compare.Run());
        }

        bool checkUpdate = args.Any(a => a.Equals("--checkupdate", StringComparison.OrdinalIgnoreCase));
        bool applyUpdate = args.Any(a => a.Equals("--applyupdate", StringComparison.OrdinalIgnoreCase));
        if (checkUpdate || applyUpdate)
        {
            Environment.Exit(Compare.RunUpdateAsync(applyUpdate).GetAwaiter().GetResult());
        }

        if (args.Any(a => a.Equals("--speedtest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(Compare.RunSpeedTest());
        }

        // yt-dlp を実際に動かして1本落とす
        int mediaAt = Array.FindIndex(args,
            a => a.Equals("--media", StringComparison.OrdinalIgnoreCase));
        if (mediaAt >= 0 && mediaAt + 1 < args.Length)
        {
            var target = args[mediaAt + 1];
            Environment.Exit(Compare.RunMediaAsync(target).GetAwaiter().GetResult());
        }

        // 実サーバ・実データで、出荷するコードそのものを通す
        if (args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(Compare.RunLiveAsync().GetAwaiter().GetResult());
        }

        // 2つ動くと同じURLを両方が拾って二重にダウンロードしてしまう。
        // 後続の起動は、動いている側を前に出して自分は終了する。
        _guard = new SingleInstance();
        if (!_guard.IsPrimary)
        {
            SingleInstance.Signal();
            Environment.Exit(0);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiQueue = DispatcherQueue.GetForCurrentThread();

        Directory.CreateDirectory(AppPaths.ConfigDir);

        _settings = new AppSettings();
        _history = new History();
        _queueStore = new QueueStore();

        _session = new Session(_uiQueue, _settings, _history, _queueStore);

        _tray = new TrayIcon(
            onShow: () => _uiQueue?.TryEnqueue(() => _window?.ShowFromTray()),
            onToggleWatch: () => _uiQueue?.TryEnqueue(() => _session?.ToggleWatch()),
            onQuit: () => _uiQueue?.TryEnqueue(QuitAll));

        _updates = new UpdateService();

        _window = new MainWindow();
        _window.Attach(_session, _tray, _updates);
        _window.Closed += (_, _) => QuitAll();

        _guard!.Listen(() => _uiQueue?.TryEnqueue(() => _window?.ShowFromTray()));

        _window.Activate();
    }

    private void QuitAll()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        _session?.Shutdown();
        _session?.Dispose();
        _tray?.Dispose();
        _settings?.Dispose();
        _history?.Dispose();
        _queueStore?.Dispose();
        _guard?.Dispose();

        Environment.Exit(0);
    }

    /// <summary>想定外の例外でアプリごと消えないようにする。1件の失敗より、常駐が死ぬ方が困る。</summary>
    private void OnDispatcherException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;
    }

    private void OnDomainException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogCrash(ex);
    }

    /// <summary>落ちた理由を残す。画面が出せない状況でも後から追える。</summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            File.AppendAllText(
                Path.Combine(AppPaths.ConfigDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
