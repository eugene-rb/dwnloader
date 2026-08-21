"""Python と Web UI の橋渡し。

イベントは1件ずつ JS を呼ぶと、75ページ×複数作品の進捗で呼び出しが溢れる。
短い間隔でまとめ、進捗は同じ作品の最新だけ残してから送る。
"""

from __future__ import annotations

import json
import threading
from typing import Any

import webview

from .config import media_dir

#: まとめて送る間隔（秒）。人の目には十分速く、呼び出し回数は大きく減る。
_FLUSH_INTERVAL = 0.08


class EventPump:
    """Python 側のイベントを JS へ流す。"""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._queue: list[dict] = []
        self._progress: dict[str, int] = {}   # id -> _queue 内の位置
        self._window: Any = None
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def bind(self, window) -> None:
        self._window = window
        if self._thread is None:
            self._thread = threading.Thread(target=self._loop, daemon=True,
                                            name="event-pump")
            self._thread.start()

    def emit(self, name: str, payload: dict) -> None:
        with self._lock:
            if name == "progress":
                # 同じ作品の途中経過は最新だけあればよい
                job_id = payload.get("id", "")
                index = self._progress.get(job_id)
                if index is not None and index < len(self._queue):
                    self._queue[index] = {"type": name, **payload}
                    return
                self._progress[job_id] = len(self._queue)
            self._queue.append({"type": name, **payload})

    def _take(self) -> list[dict]:
        with self._lock:
            if not self._queue:
                return []
            batch, self._queue = self._queue, []
            self._progress.clear()
            return batch

    def _loop(self) -> None:
        while not self._stop.wait(_FLUSH_INTERVAL):
            batch = self._take()
            if not batch or self._window is None:
                continue
            # ensure_ascii=True にして、JS の行終端とみなされる文字を確実に escape する
            data = json.dumps(batch, ensure_ascii=True)
            try:
                self._window.evaluate_js(f"window.__recv({data})")
            except Exception:
                # ウィンドウが閉じる途中などで失敗する。監視は続ける。
                pass

    def stop(self) -> None:
        self._stop.set()


class _NoWindowOps:
    """ウィンドウがまだ無い間の受け皿。呼ばれても何も起きない。"""

    def begin_drag(self) -> bool: return False
    def minimize(self) -> bool: return False
    def toggle_maximize(self) -> bool: return False
    def is_maximized(self) -> bool: return False
    def close(self) -> bool: return False
    def set_on_top(self, enabled: bool) -> bool: return False


class Api:
    """JS から呼べる窓口。pywebview が js_api として公開する。"""

    def __init__(self, session, pump: EventPump, on_quit) -> None:
        self._session = session
        self._pump = pump
        self._on_quit = on_quit
        self._window = None
        self._ops = _NoWindowOps()

    def bind(self, window) -> None:
        self._window = window

    # ------------------------------------------------------------ 取得

    def ready(self) -> dict:
        return self._session.snapshot()

    def snapshot(self) -> dict:
        return self._session.snapshot()

    # ------------------------------------------------------------ キュー

    def add_urls(self, text: str) -> int:
        return self._session.add_text(text or "")

    def start_all(self) -> bool:
        self._session.start_all()
        return True

    def retry_all(self) -> bool:
        self._session.retry_all()
        return True

    def retry(self, job_id: str) -> bool:
        self._session.retry(job_id)
        return True

    def cancel(self, job_id: str) -> bool:
        self._session.cancel(job_id)
        return True

    def remove(self, job_id: str) -> bool:
        self._session.remove(job_id)
        return True

    def clear_finished(self) -> bool:
        self._session.clear_finished()
        return True

    def clear_all(self) -> int:
        return self._session.clear_all()

    def remove_many(self, job_ids: list) -> int:
        return self._session.remove_many([str(i) for i in (job_ids or [])])

    # ------------------------------------------------------------ ファイル

    def open_result(self, job_id: str) -> bool:
        return self._session.open_result(job_id)

    def reveal_result(self, job_id: str) -> bool:
        return self._session.reveal_result(job_id)

    def open_page(self, job_id: str) -> bool:
        return self._session.open_page(job_id)

    def open_output_dir(self, kind: str = "") -> bool:
        return self._session.open_output_dir(str(kind or ""))

    def pick_output_dir(self) -> str:
        if self._window is None:
            return ""
        result = self._window.create_file_dialog(
            webview.FOLDER_DIALOG, directory=self._session.settings["output_dir"])
        if not result:
            return ""
        path = result[0] if isinstance(result, (list, tuple)) else str(result)
        self._session.save_settings({"output_dir": path})
        return path

    def pick_media_dir(self, kind: str) -> str:
        """動画・音声の保存先を選ぶ。PDF とは別のフォルダを指定できる。"""
        if self._window is None:
            return ""
        kind = str(kind or "")
        result = self._window.create_file_dialog(
            webview.FOLDER_DIALOG, directory=str(media_dir(self._session.settings, kind)))
        if not result:
            return ""
        path = result[0] if isinstance(result, (list, tuple)) else str(result)
        self._session.save_settings({"audio_dir" if kind == "audio" else "video_dir": path})
        return path

    def pick_media_cookies_file(self) -> str:
        """動画・音声のCookieファイル（cookies.txt）を選ぶ。"""
        if self._window is None:
            return ""
        result = self._window.create_file_dialog(
            webview.OPEN_DIALOG,
            file_types=("テキストファイル (*.txt)", "すべてのファイル (*.*)"))
        if not result:
            return ""
        path = result[0] if isinstance(result, (list, tuple)) else str(result)
        self._session.save_settings({"media_cookies_file": path})
        return path

    # ------------------------------------------------------------ 設定

    def set_watch(self, enabled: bool) -> bool:
        self._session.set_watch(bool(enabled))
        return True

    def set_media_kind(self, kind: str) -> str:
        return self._session.set_media_kind(str(kind or ""))

    def set_playlist_all(self, enabled: bool) -> bool:
        return self._session.set_playlist_all(bool(enabled))

    def set_entry_kind(self, job_id: str, kind: str) -> bool:
        return self._session.set_entry_kind(str(job_id), str(kind or ""))

    def save_settings(self, values: dict) -> dict:
        self._session.save_settings(values or {})
        return self._session.settings.as_dict()

    def get_settings(self) -> dict:
        return self._session.settings.as_dict()

    # ------------------------------------------------------------ ウィンドウ
    # ボーダーレスにしたので、最小化・最大化・閉じるも自前で用意する。

    def set_window_ops(self, ops) -> None:
        self._ops = ops

    def begin_drag(self) -> bool:
        return self._ops.begin_drag()

    def minimize_window(self) -> bool:
        return self._ops.minimize()

    def toggle_maximize(self) -> bool:
        return self._ops.toggle_maximize()

    def is_maximized(self) -> bool:
        return self._ops.is_maximized()

    def close_window(self) -> bool:
        return self._ops.close()

    def set_on_top(self, enabled: bool) -> bool:
        return self._ops.set_on_top(bool(enabled))

    def minimize_to_tray(self) -> bool:
        if self._window is not None:
            self._window.hide()
        return True

    def running_count(self) -> int:
        return self._session.running_count()

    def quit(self) -> bool:
        self._on_quit()
        return True
