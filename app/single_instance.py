"""多重起動の防止。UI フレームワークに依存しない。

クリップボードを監視する常駐ツールなので、2つ動くと同じURLを両方が拾って
同じ作品を二重にダウンロードする。さらに起動時の一時フォルダ掃除が、
先に動いている側の作業中フォルダまで消してしまう。
2つ目は起動せず、動いている側のウィンドウを前に出す。

ループバックの1ポートを「占有」と「合図の受け渡し」の両方に使う。
bind できたら自分が主、できなければ既に誰かがいる。
"""

from __future__ import annotations

import socket
import threading
from typing import Callable

HOST = "127.0.0.1"
DEFAULT_PORT = 49731

#: 相手が本当にこのアプリか確かめるための合言葉。ポートが偶然
#: 他のソフトに使われていた場合に、誤って二番手にならないようにする。
_HELLO = b"dwnloader/1\n"
_TIMEOUT = 1.0


class SingleInstance:
    def __init__(self, on_message: Callable[[str], None] | None = None,
                 port: int = DEFAULT_PORT):
        self.port = port
        self._on_message = on_message
        self._server: socket.socket | None = None
        self._stop = threading.Event()
        self._primary = self._claim()

    # ------------------------------------------------------------ 確保

    def _claim(self) -> bool:
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        # SO_REUSEADDR は付けない。付けると二重に bind できてしまい、
        # 占有の判定にならない。
        try:
            server.bind((HOST, self.port))
            server.listen(4)
        except OSError:
            server.close()
            return not self._peer_is_ours()

        server.settimeout(0.4)
        self._server = server
        threading.Thread(target=self._accept_loop, daemon=True,
                         name="single-instance").start()
        return True

    def _peer_is_ours(self) -> bool:
        """ポートを握っているのが本当にこのアプリか確かめる。"""
        try:
            with socket.create_connection((HOST, self.port), _TIMEOUT) as sock:
                sock.settimeout(_TIMEOUT)
                return sock.recv(len(_HELLO)) == _HELLO
        except OSError:
            return False

    def is_primary(self) -> bool:
        return self._primary

    # ------------------------------------------------------------ 送受信

    def send(self, payload: str) -> bool:
        """先行インスタンスへ合図を送る。"""
        try:
            with socket.create_connection((HOST, self.port), _TIMEOUT) as sock:
                sock.settimeout(_TIMEOUT)
                if sock.recv(len(_HELLO)) != _HELLO:
                    return False
                sock.sendall(payload.encode("utf-8"))
                sock.shutdown(socket.SHUT_WR)
                # 相手が読み終えて閉じるのを待ってから戻る。待たずに
                # プロセスを終えると、書いた内容が届かないことがある。
                sock.recv(16)
                return True
        except OSError:
            return False

    def _accept_loop(self) -> None:
        while not self._stop.is_set():
            try:
                conn, _ = self._server.accept()  # type: ignore[union-attr]
            except (socket.timeout, TimeoutError):
                continue
            except OSError:
                break
            threading.Thread(target=self._handle, args=(conn,), daemon=True).start()

    def _handle(self, conn: socket.socket) -> None:
        with conn:
            try:
                conn.settimeout(_TIMEOUT)
                conn.sendall(_HELLO)
                chunks = []
                while True:
                    data = conn.recv(4096)
                    if not data:
                        break
                    chunks.append(data)
                payload = b"".join(chunks).decode("utf-8", "replace")
            except OSError:
                return

            # 生存確認だけの接続では何も起こさない
            if payload and self._on_message is not None:
                try:
                    self._on_message(payload)
                except Exception:
                    pass
            try:
                conn.sendall(b"ok")
            except OSError:
                pass

    def close(self) -> None:
        self._stop.set()
        if self._server is not None:
            try:
                self._server.close()
            except OSError:
                pass
            self._server = None
