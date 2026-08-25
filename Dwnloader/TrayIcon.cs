using System.Drawing;
using System.Windows.Forms;
using Dwnloader.Core;

namespace Dwnloader;

/// <summary>
/// タスクトレイ常駐。ウィンドウを閉じても監視を続けられるようにする。
/// アイコンは app.ico（WPF リソースとして埋め込み）から読み込む。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _watchItem;
    private readonly Action _onShow;
    private readonly Action _onToggleWatch;
    private readonly Action _onQuit;

    private bool _watching = true;
    private string _status = "";
    private readonly Icon _appIcon;

    public TrayIcon(Action onShow, Action onToggleWatch, Action onQuit)
    {
        _onShow = onShow;
        _onToggleWatch = onToggleWatch;
        _onQuit = onQuit;

        _appIcon = MakeIcon();

        _watchItem = new ToolStripMenuItem("クリップボード監視を停止", null, (_, _) => _onToggleWatch());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("ウィンドウを表示", null, (_, _) => _onShow()));
        menu.Items.Add(_watchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => _onQuit()));

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = AppInfo.Title,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _onShow();
    }

    /// <summary>カーソルを乗せたときに状況が分かるようにする。</summary>
    public void SetStatus(bool watching, string status)
    {
        _watching = watching;
        _watchItem.Text = watching ? "クリップボード監視を停止" : "クリップボード監視を再開";

        if (status == _status) return;
        _status = status;
        // NotifyIcon.Text は 63 文字までしか受け付けない
        var text = $"{AppInfo.Title}\n{status}";
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception)
        {
            // 通知に対応しない環境でも常駐は続ける
        }
    }

    /// <summary>app.ico から、トレイの実サイズ（16/32px 等）に合う面を選んで読み込む。</summary>
    private static Icon MakeIcon()
    {
        var uri = new Uri("app.ico", UriKind.Relative);
        var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        using (stream)
        {
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
