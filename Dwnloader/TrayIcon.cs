using System.IO;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Dwnloader.Core;

namespace Dwnloader;

/// <summary>
/// タスクトレイ常駐。ウィンドウを閉じても監視を続けられるようにする。
///
/// WinUI3には標準のトレイアイコンが無い（<UseWindowsForms>をWinUI3プロジェクトに
/// 追加するとXAMLコンパイラとの既知の競合(MC6000系)がある）ため H.NotifyIcon.WinUI
/// を使う。.NET8対応は2.3.2まで — 2.4.1以降はnet10.0限定でビルドできない
/// （使い捨てプロトタイプ scratchpad/winui3proto の NU1202 で確認済み）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _watchItem;
    private readonly Action _onShow;
    private readonly Action _onToggleWatch;
    private readonly Action _onQuit;

    private bool _watching = true;
    private string _status = "";

    public TrayIcon(Action onShow, Action onToggleWatch, Action onQuit)
    {
        _onShow = onShow;
        _onToggleWatch = onToggleWatch;
        _onQuit = onQuit;

        _watchItem = new MenuFlyoutItem { Text = "クリップボード監視を停止" };
        _watchItem.Click += (_, _) => _onToggleWatch();

        var showItem = new MenuFlyoutItem { Text = "ウィンドウを表示" };
        showItem.Click += (_, _) => _onShow();

        var quitItem = new MenuFlyoutItem { Text = "終了" };
        quitItem.Click += (_, _) => _onQuit();

        var menu = new MenuFlyout();
        menu.Items.Add(showItem);
        menu.Items.Add(_watchItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quitItem);

        // app.ico は Content としてビルド出力にコピーされている
        // （WinUI3にはApplication.GetResourceStream相当が無いため）。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri(iconPath)),
            ToolTipText = AppInfo.Title,
            ContextFlyout = menu,
            DoubleClickCommand = new RelayCommand(_onShow),
        };
        _icon.ForceCreate();
    }

    /// <summary>カーソルを乗せたときに状況が分かるようにする。</summary>
    public void SetStatus(bool watching, string status)
    {
        _watching = watching;
        _watchItem.Text = watching ? "クリップボード監視を停止" : "クリップボード監視を再開";

        if (status == _status) return;
        _status = status;
        // ツールチップは 63 文字までしか受け付けない
        var text = $"{AppInfo.Title}\n{status}";
        _icon.ToolTipText = text.Length > 62 ? text[..62] : text;
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message);
        }
        catch (Exception)
        {
            // 通知に対応しない環境でも常駐は続ける
        }
    }

    public void Dispose()
    {
        _icon.Dispose();
    }

    /// <summary>
    /// H.NotifyIcon.WinUI のダブルクリックは Command 経由でしか確実に配線できない
    /// （バージョン間でイベント名の生成規則が変わりうるため、DependencyProperty
    /// として公開されている DoubleClickCommand を使う）。
    /// </summary>
    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
