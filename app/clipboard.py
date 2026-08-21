"""クリップボード監視。Windows API を直接叩く（追加の依存なし）。

GetClipboardSequenceNumber は「中身が変わったか」だけを教えてくれる。
毎回クリップボードを開いて読むと他のアプリと取り合いになるので、
番号が変わったときだけ開く。
"""

from __future__ import annotations

import ctypes
import threading
from ctypes import wintypes
from typing import Callable

CF_UNICODETEXT = 13

_user32 = ctypes.WinDLL("user32", use_last_error=True)
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

_user32.GetClipboardSequenceNumber.restype = wintypes.DWORD
_user32.OpenClipboard.argtypes = [wintypes.HWND]
_user32.OpenClipboard.restype = wintypes.BOOL
_user32.CloseClipboard.restype = wintypes.BOOL
_user32.GetClipboardData.argtypes = [wintypes.UINT]
_user32.GetClipboardData.restype = wintypes.HANDLE
_kernel32.GlobalLock.argtypes = [wintypes.HGLOBAL]
_kernel32.GlobalLock.restype = ctypes.c_void_p
_kernel32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]


def sequence_number() -> int:
    return int(_user32.GetClipboardSequenceNumber())


def read_text(attempts: int = 5) -> str | None:
    """クリップボードのテキストを読む。取れなければ None。

    他のプロセスが開いている間は失敗するので、少し待って何度か試す。
    """
    for _ in range(attempts):
        if not _user32.OpenClipboard(None):
            threading.Event().wait(0.03)
            continue
        try:
            handle = _user32.GetClipboardData(CF_UNICODETEXT)
            if not handle:
                return None
            pointer = _kernel32.GlobalLock(handle)
            if not pointer:
                return None
            try:
                return ctypes.wstring_at(pointer)
            finally:
                _kernel32.GlobalUnlock(handle)
        finally:
            _user32.CloseClipboard()
    return None


class ClipboardWatcher:
    """別スレッドで変化を見張り、テキストが変わったら知らせる。"""

    def __init__(self, on_text: Callable[[str], None], interval: float = 0.5):
        self._on_text = on_text
        self._interval = interval
        self._enabled = threading.Event()
        self._stop = threading.Event()
        self._seq = sequence_number()
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._loop, daemon=True,
                                        name="clipboard")
        self._thread.start()

    def set_enabled(self, enabled: bool) -> None:
        if enabled:
            # 止めている間のコピーを再開時にまとめて拾わない
            self._seq = sequence_number()
            self._enabled.set()
        else:
            self._enabled.clear()

    def is_enabled(self) -> bool:
        return self._enabled.is_set()

    def stop(self) -> None:
        self._stop.set()

    def _loop(self) -> None:
        while not self._stop.wait(self._interval):
            if not self._enabled.is_set():
                continue
            try:
                seq = sequence_number()
            except OSError:
                continue
            if seq == self._seq:
                continue
            self._seq = seq

            text = read_text()
            if not text:
                continue
            try:
                self._on_text(text)
            except Exception:
                # 監視は止めない。1件の失敗で常駐が死ぬほうが困る。
                pass
