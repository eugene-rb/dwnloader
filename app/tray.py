"""タスクトレイ常駐。

ウィンドウを閉じても監視を続けられるようにする。アイコンは外部ファイルを
持たず、その場で描く。
"""

from __future__ import annotations

import threading
from typing import Callable

from PIL import Image, ImageDraw

try:
    import pystray
except ImportError:  # pragma: no cover
    pystray = None


def make_icon_image(size: int = 64) -> Image.Image:
    """角丸の四角に "PDF" を描いたアイコンを作る。"""
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    pad = size // 16
    draw.rounded_rectangle(
        [pad, pad, size - pad, size - pad],
        radius=size // 4.5, fill=(109, 140, 255, 255),
    )
    text = "PDF"
    try:
        box = draw.textbbox((0, 0), text)
        draw.text(((size - (box[2] - box[0])) / 2, (size - (box[3] - box[1])) / 2 - 2),
                  text, fill=(11, 14, 21, 255))
    except Exception:
        pass
    return image


class Tray:
    """pystray を薄く包む。導入されていなければ何もしない。"""

    def __init__(self, title: str, on_show: Callable[[], None],
                 on_toggle_watch: Callable[[], None], on_quit: Callable[[], None]):
        self.title = title
        self._on_show = on_show
        self._on_toggle_watch = on_toggle_watch
        self._on_quit = on_quit
        self._watching = True
        self._status = ""
        self._icon = None

        if pystray is None:
            return

        self._icon = pystray.Icon(
            "dwnloader", make_icon_image(), title,
            menu=pystray.Menu(
                pystray.MenuItem("ウィンドウを表示", self._show, default=True),
                pystray.MenuItem(
                    lambda _item: ("クリップボード監視を停止" if self._watching
                                   else "クリップボード監視を再開"),
                    self._toggle),
                pystray.Menu.SEPARATOR,
                pystray.MenuItem("終了", self._quit),
            ),
        )

    # ---------------------------------------------------------- 操作

    def start(self) -> None:
        if self._icon is not None:
            self._icon.run_detached()

    def stop(self) -> None:
        if self._icon is not None:
            try:
                self._icon.stop()
            except Exception:
                pass

    def set_status(self, watching: bool, status: str) -> None:
        """カーソルを乗せたときに状況が分かるようにする。"""
        self._watching = watching
        if status == self._status:
            return
        self._status = status
        if self._icon is not None:
            try:
                self._icon.title = f"{self.title}\n{status}"[:127]
                self._icon.update_menu()
            except Exception:
                pass

    def notify(self, title: str, message: str) -> None:
        if self._icon is None:
            return
        try:
            self._icon.notify(message, title)
        except Exception:
            # 通知に対応しない環境でも常駐は続ける
            pass

    # ------------------------------------------------- メニューの受け口

    def _show(self, *_args) -> None:
        threading.Thread(target=self._on_show, daemon=True).start()

    def _toggle(self, *_args) -> None:
        threading.Thread(target=self._on_toggle_watch, daemon=True).start()

    def _quit(self, *_args) -> None:
        threading.Thread(target=self._on_quit, daemon=True).start()
