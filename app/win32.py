"""ボーダーレスウィンドウのための Win32 まわりの調整。

pywebview は frameless にすると枠のスタイルを None にするため、縁を掴んだ
リサイズができなくなる。タイトルバーは出さないまま、リサイズ用の枠だけを
スタイルに戻す。
"""

from __future__ import annotations

import ctypes
import time
from ctypes import wintypes

_user32 = ctypes.WinDLL("user32", use_last_error=True)
_dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

THREAD_PRIORITY_TIME_CRITICAL = 15
_kernel32.SetThreadPriority.argtypes = [wintypes.HANDLE, ctypes.c_int]
_kernel32.SetThreadPriority.restype = wintypes.BOOL
# GetCurrentThread() は常にこの疑似ハンドル(-2)を返す定数。restype を素の
# HANDLE にして呼ぶと、64bit環境では符号無しの巨大な値として丸められ、
# それを SetThreadPriority へ渡し直す際に OverflowError になる
# （ctypes の HANDLE 往復で実際に踏んだ）。呼ばずに定数を直接使えば安全。
_CURRENT_THREAD = ctypes.c_void_p(-2)

GWL_STYLE = -16
WS_THICKFRAME = 0x00040000
WS_MAXIMIZEBOX = 0x00010000
WS_MINIMIZEBOX = 0x00020000

SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
SWP_NOZORDER = 0x0004
SWP_FRAMECHANGED = 0x0020

SW_MINIMIZE = 6
SW_MAXIMIZE = 3
SW_RESTORE = 9

SWP_NOACTIVATE = 0x0010
VK_LBUTTON = 0x01

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x00000008
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2

# Windows 11 の角丸
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWCP_ROUND = 2

_user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
_user32.FindWindowW.restype = wintypes.HWND
_user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
_user32.IsZoomed.argtypes = [wintypes.HWND]
_user32.IsZoomed.restype = wintypes.BOOL
_user32.GetAsyncKeyState.argtypes = [ctypes.c_int]
_user32.GetAsyncKeyState.restype = ctypes.c_short
_user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
_user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
_user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int,
                                 ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]

# 64bit では GetWindowLongPtr を使う
_get_style = getattr(_user32, "GetWindowLongPtrW", _user32.GetWindowLongW)
_set_style = getattr(_user32, "SetWindowLongPtrW", _user32.SetWindowLongW)
_get_style.argtypes = [wintypes.HWND, ctypes.c_int]
_get_style.restype = ctypes.c_ssize_t
_set_style.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_ssize_t]
_set_style.restype = ctypes.c_ssize_t


def find_window(title: str):
    hwnd = _user32.FindWindowW(None, title)
    return hwnd or None


def make_resizable(hwnd) -> bool:
    """タイトルバー無しのまま、縁からのリサイズと最大化を有効にする。"""
    if not hwnd:
        return False
    try:
        style = _get_style(hwnd, GWL_STYLE)
        wanted = style | WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX
        if wanted == style:
            return True
        _set_style(hwnd, GWL_STYLE, wanted)
        _user32.SetWindowPos(hwnd, None, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED)
        return True
    except Exception:
        return False


def round_corners(hwnd) -> bool:
    """Windows 11 なら角を丸める。古い Windows では何も起きない。"""
    if not hwnd:
        return False
    try:
        value = ctypes.c_int(DWMWCP_ROUND)
        _dwmapi.DwmSetWindowAttribute(
            wintypes.HWND(hwnd), ctypes.c_int(DWMWA_WINDOW_CORNER_PREFERENCE),
            ctypes.byref(value), ctypes.sizeof(value))
        return True
    except Exception:
        return False


def is_topmost(hwnd) -> bool:
    if not hwnd:
        return False
    return bool(_get_style(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST)


def set_topmost(hwnd, enabled: bool) -> bool:
    """最前面固定を切り替える。

    pywebview の window.on_top は WinForms の TopMost を呼び出し元スレッドから
    直接触るため、UI スレッドと相互待ちになってアプリごと固まる。
    SetWindowPos はスレッドをまたいで呼んでよいので、こちらを使う。
    """
    if not hwnd:
        return False
    try:
        _user32.SetWindowPos(
            hwnd, ctypes.c_void_p(HWND_TOPMOST if enabled else HWND_NOTOPMOST),
            0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
    except Exception:
        return False
    return is_topmost(hwnd)


def cursor_pos() -> tuple[int, int]:
    point = wintypes.POINT()
    _user32.GetCursorPos(ctypes.byref(point))
    return point.x, point.y


def window_pos(hwnd) -> tuple[int, int]:
    rect = wintypes.RECT()
    _user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top


def move_to(hwnd, x: int, y: int) -> None:
    _user32.SetWindowPos(hwnd, None, int(x), int(y), 0, 0,
                         SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)


def left_button_down() -> bool:
    # 最上位ビットが立っていれば押されている
    return bool(_user32.GetAsyncKeyState(VK_LBUTTON) & 0x8000)


def drag_window(hwnd, should_stop=None) -> bool:
    """左ボタンが離されるまで、カーソルに追従してウィンドウを動かす。

    WebView2 がマウスキャプチャを握っているため、Windows 標準の移動ループ
    （WM_SYSCOMMAND / SC_MOVE）は起動しない。JS から1フレームずつ座標を
    送る方式もブリッジの往復で取りこぼす。ここで直接追従させるのが確実で、
    移動中は Python 内で完結するため通信も発生しない。
    """
    if not hwnd or not left_button_down():
        return False

    # ダウンロードジョブやURL解析などバックグラウンドのCPU処理が動いている間も
    # 追従が遅れないよう、このスレッドの優先度を上げる。既定の優先度のままだと
    # GIL の奪い合いに負けて呼び出しの間隔が空き、「ドラッグが効かない」ように
    # 見える（このスレッドはドラッグ中しか生きないので、他スレッドを長時間
    # 飢えさせる心配はない）。
    try:
        _kernel32.SetThreadPriority(_CURRENT_THREAD, THREAD_PRIORITY_TIME_CRITICAL)
    except Exception:
        pass

    cx, cy = cursor_pos()
    wx, wy = window_pos(hwnd)
    offset_x, offset_y = wx - cx, wy - cy

    deadline = time.monotonic() + 30.0      # 取りこぼしても永久には回さない
    while left_button_down() and time.monotonic() < deadline:
        if should_stop is not None and should_stop():
            break
        px, py = cursor_pos()
        move_to(hwnd, px + offset_x, py + offset_y)
        time.sleep(1 / 120)
    return True


def minimize(hwnd) -> None:
    if hwnd:
        _user32.ShowWindow(hwnd, SW_MINIMIZE)


def is_maximized(hwnd) -> bool:
    return bool(hwnd) and bool(_user32.IsZoomed(hwnd))


def toggle_maximize(hwnd) -> bool:
    """最大化と元のサイズを行き来する。戻り値は操作後に最大化されているか。"""
    if not hwnd:
        return False
    if is_maximized(hwnd):
        _user32.ShowWindow(hwnd, SW_RESTORE)
        return False
    _user32.ShowWindow(hwnd, SW_MAXIMIZE)
    return True
