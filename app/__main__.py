"""エントリポイント: python -m app"""

from __future__ import annotations

import sys
import threading
from pathlib import Path

import webview

from . import APP_NAME, APP_TITLE, APP_VERSION
from .bridge import Api, EventPump
from .config import History, QueueStore, Settings
from .session import Session
from .single_instance import SingleInstance
from .tray import Tray
from . import win32

WEB_DIR = Path(__file__).with_name("web")

WINDOW_TITLE = f"{APP_TITLE} v{APP_VERSION}"


class WindowOps:
    """タイトルバーを自前で持つので、ウィンドウ操作も自前で用意する。"""

    def __init__(self, app: "App"):
        self._app = app
        self._hwnd = None
        self._dragging = False

    def hwnd(self):
        """ウィンドウハンドルを得る。

        タイトル文字列での検索は、題名を変えたときや同名の窓があるときに
        壊れる。pywebview が持つ実体から直接取り、それが駄目なときだけ
        名前で探す。
        """
        if self._hwnd:
            return self._hwnd

        window = self._app.window
        native = getattr(window, "native", None) if window is not None else None
        handle = getattr(native, "Handle", None)
        if handle is not None:
            # .NET の IntPtr なので int() では変換できない
            for convert in (lambda h: int(h.ToInt64()), int):
                try:
                    self._hwnd = convert(handle)
                    break
                except Exception:
                    continue

        if not self._hwnd:
            self._hwnd = win32.find_window(WINDOW_TITLE)
        return self._hwnd

    def prepare(self) -> None:
        """枠なしのままリサイズできるようにし、角を丸める。"""
        handle = self.hwnd()
        if handle:
            win32.make_resizable(handle)
            win32.round_corners(handle)
            # 最前面固定も pywebview を通さず、ここで適用する
            if self._app.settings["always_on_top"]:
                win32.set_topmost(handle, True)

    def begin_drag(self) -> bool:
        """掴んだ位置からカーソルに追従させる。追従は別スレッドで回すので、
        呼び出した JS 側は待たされない。"""
        handle = self.hwnd()
        if not handle or self._dragging:
            return False
        if win32.is_maximized(handle):
            return False          # 最大化中は動かさない

        self._dragging = True

        def run():
            try:
                win32.drag_window(handle)
            finally:
                self._dragging = False

        threading.Thread(target=run, daemon=True, name="window-drag").start()
        return True

    def minimize(self) -> bool:
        win32.minimize(self.hwnd())
        return True

    def toggle_maximize(self) -> bool:
        return win32.toggle_maximize(self.hwnd())

    def is_maximized(self) -> bool:
        return win32.is_maximized(self.hwnd())

    def close(self) -> bool:
        window = self._app.window
        if window is None:
            return False
        # 終了確認やトレイ最小化の判断は closing ハンドラに任せる
        window.destroy()
        return True

    def set_on_top(self, enabled: bool) -> bool:
        # pywebview の window.on_top は使わない（UIスレッドと相互待ちになり固まる）
        applied = win32.set_topmost(self.hwnd(), enabled)
        self._app.settings["always_on_top"] = applied
        return applied

    def is_on_top(self) -> bool:
        return win32.is_topmost(self.hwnd())


class App:
    def __init__(self) -> None:
        self.settings = Settings()
        self.history = History()
        self.queue_store = QueueStore()

        self.pump = EventPump()
        self.session = Session(self.settings, self.history, self.queue_store,
                               self._emit)
        self.api = Api(self.session, self.pump, self.quit)

        self.window = None
        self.ops = WindowOps(self)
        self.tray: Tray | None = None
        self._visible = True
        self._quitting = False

    # ------------------------------------------------------------ 通知の振り分け

    def _emit(self, name: str, payload: dict) -> None:
        """UI へ流す。見ていない場所には出さない。"""
        if name == "notify" and not self._visible:
            # ウィンドウが隠れているときは Web のトーストが見えないので
            # トレイ通知に回す。両方に出すと過剰になる。
            if self.settings["notify"] and self.tray is not None:
                self.tray.notify(payload.get("title", APP_TITLE), payload.get("body", ""))
            return

        self.pump.emit(name, payload)

        if name in ("state", "entry", "finished", "removed"):
            self._sync_tray()

    def _sync_tray(self) -> None:
        if self.tray is not None:
            self.tray.set_status(self.session.clipboard.is_enabled(),
                                 self.session.status_text())

    # ------------------------------------------------------------ ウィンドウ

    def show_window(self) -> None:
        if self.window is None:
            return
        try:
            self.window.show()
            self.window.restore()
            self.window.on_top = True
            self.window.on_top = False
        except Exception:
            pass
        self._visible = True

    def hide_window(self) -> None:
        if self.window is not None:
            self.window.hide()
        self._visible = False

    def _on_closing(self) -> bool:
        """False を返すと閉じるのを取り消せる。"""
        if self._quitting:
            return True

        if self.settings["minimize_to_tray"] and self.tray is not None:
            self.hide_window()
            self.tray.notify(APP_TITLE, "タスクトレイで監視を続けます")
            return False

        running = self.session.running_count()
        if running and self.window is not None:
            keep = self.window.create_confirmation_dialog(
                "確認", f"{running} 件のダウンロードが進行中です。中止して終了しますか？")
            if not keep:
                return False

        self._shutdown()
        return True

    # ------------------------------------------------------------ 起動と終了

    def toggle_watch(self) -> None:
        enabled = self.session.toggle_watch()
        self._sync_tray()
        self.session.log("info", "クリップボード監視を開始しました" if enabled
                                 else "クリップボード監視を停止しました")

    def quit(self) -> None:
        self._quitting = True
        self._shutdown()
        if self.window is not None:
            try:
                self.window.destroy()
            except Exception:
                pass

    def _shutdown(self) -> None:
        self.session.shutdown()
        self.pump.stop()
        if self.tray is not None:
            self.tray.stop()

    def start(self) -> int:
        self.tray = Tray(APP_TITLE, on_show=self.show_window,
                         on_toggle_watch=self.toggle_watch, on_quit=self.quit)
        self.tray.start()

        self.window = webview.create_window(
            WINDOW_TITLE,
            url=str(WEB_DIR / "index.html"),
            js_api=self.api,
            width=1020, height=740,
            # 付箋のように小さくしても使えるところまで下げる
            min_size=(380, 300),
            frameless=True,          # タイトルバーは自前で描く
            easy_drag=False,         # 掴める範囲は data-drag で自前指定する
            shadow=True,
            # on_top は渡さない。pywebview 側の実装が固まる原因になるので、
            # 起動直後に prepare() から ctypes で適用する。
            background_color="#0f1116",
            text_select=False,
        )
        self.api.bind(self.window)
        self.api.set_window_ops(self.ops)
        self.pump.bind(self.window)

        self.window.events.closing += self._on_closing
        self.window.events.shown += lambda: setattr(self, "_visible", True)
        self.window.events.minimized += lambda: setattr(self, "_visible", False)
        self.window.events.restored += lambda: setattr(self, "_visible", True)

        webview.start(self._after_start, gui="edgechromium", private_mode=False,
                      storage_path=str(_storage_dir()))
        self._shutdown()
        return 0

    def _after_start(self) -> None:
        # UI が出てから復元などを走らせる。起動直後にイベントを送っても
        # 受け手がまだいない。
        threading.Timer(0.25, self.ops.prepare).start()
        threading.Timer(0.35, self._bootstrap).start()

    def _bootstrap(self) -> None:
        self.session.bootstrap()
        self.session.push_state()
        self._sync_tray()


def _storage_dir() -> Path:
    from .config import CONFIG_DIR
    path = CONFIG_DIR / "webview"
    path.mkdir(parents=True, exist_ok=True)
    return path


def main() -> int:
    # 2つ動くと同じURLを両方が拾って二重にダウンロードしてしまう。
    # 後続の起動は、動いている側を前に出して自分は終了する。
    # 合図が届く時点ではまだ App が無いので、後から差し込めるようにしておく。
    holder: dict[str, App] = {}

    def on_message(_payload: str) -> None:
        instance = holder.get("app")
        if instance is not None:
            instance.show_window()

    guard = SingleInstance(on_message)
    if not guard.is_primary():
        guard.send("show")
        print(f"{APP_NAME}: すでに起動しています")
        return 0

    app = App()
    holder["app"] = app
    try:
        return app.start()
    finally:
        guard.close()


if __name__ == "__main__":
    sys.exit(main())
