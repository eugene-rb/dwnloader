"""HTTP セッションとサイトごとのレート制限。"""

from __future__ import annotations

import threading
import time

import requests
from requests.adapters import HTTPAdapter

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
)


class RateLimiter:
    """呼び出し間隔の下限を保証する。サイトごとに1つ持つ。"""

    def __init__(self, min_interval: float) -> None:
        self.min_interval = min_interval
        self._lock = threading.Lock()
        self._next = 0.0

    def wait(self) -> None:
        if self.min_interval <= 0:
            return
        with self._lock:
            now = time.monotonic()
            delay = self._next - now
            if delay > 0:
                time.sleep(delay)
                now = time.monotonic()
            self._next = now + self.min_interval


def make_session(pool: int = 16) -> requests.Session:
    s = requests.Session()
    s.headers.update({
        "User-Agent": UA,
        "Accept-Language": "ja,en-US;q=0.8,en;q=0.6",
    })
    # リトライは呼び出し側で制御するのでアダプタ側は接続プールだけ広げる
    adapter = HTTPAdapter(pool_connections=pool, pool_maxsize=pool, max_retries=0)
    s.mount("https://", adapter)
    s.mount("http://", adapter)
    return s


class TransientError(Exception):
    """リトライして良いエラー。"""


def get_with_retry(
    session: requests.Session,
    url: str,
    *,
    headers: dict | None = None,
    timeout: float = 30,
    retries: int = 3,
    limiter: RateLimiter | None = None,
    cancel=None,
    stream: bool = False,
) -> requests.Response:
    """一時的な失敗を指数バックオフで再試行する GET。

    4xx（429 を除く）は再試行せず即座に返す。呼び出し側で 404 を見て
    URL を作り直したいケースがあるため、例外にせずレスポンスを返す。
    """
    last_exc: Exception | None = None
    for attempt in range(max(1, retries)):
        if cancel is not None and cancel.is_set():
            raise TransientError("キャンセルされました")
        if limiter is not None:
            limiter.wait()
        try:
            resp = session.get(url, headers=headers, timeout=timeout, stream=stream)
        except requests.RequestException as exc:
            last_exc = exc
        else:
            if resp.status_code < 400:
                return resp
            if resp.status_code == 429 or resp.status_code >= 500:
                last_exc = TransientError(f"HTTP {resp.status_code}")
                resp.close()
            else:
                return resp  # 恒久的なエラーはそのまま返す

        if attempt < retries - 1:
            time.sleep(min(2 ** attempt, 8))

    raise TransientError(f"{url} の取得に失敗しました: {last_exc}")
