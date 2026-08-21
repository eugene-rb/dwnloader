"""設定と履歴の永続化。%APPDATA%\\dwnloader\\ に JSON で置く。"""

from __future__ import annotations

import json
import os
import threading
from pathlib import Path

from . import APP_NAME


def _config_dir() -> Path:
    base = os.getenv("APPDATA") or os.getenv("XDG_CONFIG_HOME")
    root = Path(base) if base else Path.home() / ".config"
    return root / APP_NAME


CONFIG_DIR = _config_dir()
SETTINGS_PATH = CONFIG_DIR / "settings.json"
HISTORY_PATH = CONFIG_DIR / "history.json"
QUEUE_PATH = CONFIG_DIR / "queue.json"


def _write_json(path: Path, data) -> None:
    """一時ファイル経由で書く。書き込み中に落ちても既存を壊さない。"""
    try:
        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(path)
    except OSError:
        pass


def _read_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _default_output_dir() -> str:
    for env in ("USERPROFILE", "HOME"):
        home = os.getenv(env)
        if home:
            return str(Path(home) / "Downloads" / "gallery-pdf")
    return str(Path.cwd() / "output")


def _default_media_dir(name: str) -> str:
    """動画・音声の既定の保存先。PDF とは別の場所に分ける。"""
    for env in ("USERPROFILE", "HOME"):
        home = os.getenv(env)
        if home:
            return str(Path(home) / "Downloads" / APP_NAME / name)
    return str(Path.cwd() / "output" / name)


DEFAULTS: dict = {
    "output_dir": _default_output_dir(),
    "site_subfolder": False,
    "filename_template": "{title} [{artist}] ({site}-{id})",
    "filename_max_len": 120,

    "watch_clipboard": True,
    "auto_start": True,              # 検出したら確認なしでダウンロード開始
    "skip_downloaded": True,         # 履歴にあるものは飛ばす

    # クリップボード監視は既知サイト以外もyt-dlpの汎用抽出器で拾う（ブラックリスト式）。
    # ここに列挙したドメイン(完全一致。サブドメインは別ドメインとして区別する)だけは
    # 対象外にする(検索・コード共有・文書・メールなど、動画目的でコピーすることが
    # まず無い定番サイト。実際に使われるホスト名で列挙している)。
    # 手動のURL貼り付けはこの影響を受けない。
    "clipboard_blacklist": ("google.com, www.google.com, bing.com, www.bing.com, "
                            "duckduckgo.com, yahoo.co.jp, www.yahoo.co.jp, "
                            "github.com, gitlab.com, stackoverflow.com, "
                            "en.wikipedia.org, ja.wikipedia.org, "
                            "docs.google.com, drive.google.com, mail.google.com, "
                            "outlook.live.com, outlook.office.com, outlook.office365.com, "
                            "notion.so, www.notion.so, "
                            "amazon.co.jp, www.amazon.co.jp, amazon.com, www.amazon.com, "
                            "paypal.com, www.paypal.com"),
    # 汎用抽出器での取得に実際に成功したドメイン(完全一致)。自動で増える(設定から編集も可能)。
    # 除外リストに同じドメインが載っていても、こちらを優先して拾う。
    "clipboard_whitelist": "",

    "gallery_workers": 2,            # 同時にPDF化する作品数
    "image_workers": 5,              # 1作品内で同時取得する画像数
    "retries": 3,
    "timeout": 30,

    "prefer_format": "avif",         # hitomi の画像形式優先: avif / webp
    "jpeg_quality": 90,
    "keep_images": False,            # 元画像も残す

    "pixiv_cookie": "",              # PHPSESSID（R-18作品に必要）

    # 動画・音声は PDF と別の場所へ保存する。空にすると既定値へ戻る
    # （未設定で止まるより、すぐ使えて後から変えられる方が迷わない）。
    "video_dir": _default_media_dir("video"),
    "audio_dir": _default_media_dir("audio"),
    "media_cookies_file": "",        # cookies.txt のパス（ログイン必須の動画に必要）
    "media_kind": "video",           # 追加種別トグルの現在値: "video" / "audio"
    # 再生リスト・チャンネルのURLを全部取得するか。既定は先頭の1本だけ
    # （コピーしただけで何千本も落ち始めるのを既定にはしない）。
    "playlist_all": False,
    "video_quality": "best",         # best / 1080 / 720 / 480
    "audio_format": "mp3",           # mp3 / m4a / opus
    "audio_bitrate": 192,            # kbps
    "video_workers": 2,              # 同時ダウンロード数（yt-dlp）

    "notify": True,
    "minimize_to_tray": False,
    "always_on_top": False,          # 他のウィンドウより手前に固定する
}


class Settings:
    """辞書ライクな設定。変更のたびにディスクへ書く。"""

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._data = dict(DEFAULTS)
        self.load()

    def load(self) -> None:
        raw = _read_json(SETTINGS_PATH)
        if isinstance(raw, dict):
            with self._lock:
                # 未知のキーは無視し、既定値にない項目で設定を汚さない
                self._data.update({k: v for k, v in raw.items() if k in DEFAULTS})

    def save(self) -> None:
        with self._lock:
            data = dict(self._data)
        _write_json(SETTINGS_PATH, data)

    def __getitem__(self, key: str):
        with self._lock:
            return self._data.get(key, DEFAULTS.get(key))

    def get(self, key: str, default=None):
        with self._lock:
            return self._data.get(key, DEFAULTS.get(key, default))

    def __setitem__(self, key: str, value) -> None:
        with self._lock:
            self._data[key] = value
        self.save()

    def update(self, values: dict) -> None:
        with self._lock:
            self._data.update(values)
        self.save()

    def as_dict(self) -> dict:
        with self._lock:
            return dict(self._data)


def media_dir(settings: "Settings", kind: str) -> Path:
    """動画・音声の保存先。空欄なら既定へ戻す（保存先が無い状態を作らない）。"""
    key = "audio_dir" if kind == "audio" else "video_dir"
    value = str(settings[key] or "").strip()
    return Path(value or DEFAULTS[key])


class History:
    """ダウンロード済みキーの集合。再起動しても重複取得しないため。"""

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._done: dict[str, str] = {}
        self.load()

    def load(self) -> None:
        raw = _read_json(HISTORY_PATH)
        if isinstance(raw, dict):
            with self._lock:
                self._done = {str(k): str(v) for k, v in raw.get("done", {}).items()}

    def save(self) -> None:
        with self._lock:
            data = {"done": dict(self._done)}
        _write_json(HISTORY_PATH, data)

    def __contains__(self, key: str) -> bool:
        with self._lock:
            return key in self._done

    def path_for(self, key: str) -> str | None:
        with self._lock:
            return self._done.get(key)

    def add(self, key: str, path: str) -> None:
        with self._lock:
            self._done[key] = path
        self.save()

    def clear(self) -> None:
        with self._lock:
            self._done.clear()
        self.save()

    def __len__(self) -> int:
        with self._lock:
            return len(self._done)


#: 復元対象として保存するキーの一覧。増減しても古いファイルを壊さない。
_QUEUE_FIELDS = ("site", "gid", "url", "kind", "status", "title", "subtitle",
                 "done", "total", "out_path", "message")


class QueueStore:
    """未完了のキューを保存し、次回起動時に復元できるようにする。

    完了したものは履歴側が持つのでここには入れない。保存するのは
    「まだ片付いていない」もの、つまり中断・失敗・一部失敗・待機中だけ。
    """

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._records: list[dict] = []
        self.load()

    def load(self) -> None:
        raw = _read_json(QUEUE_PATH)
        if not isinstance(raw, dict):
            return
        items = raw.get("items")
        if not isinstance(items, list):
            return
        records = []
        for item in items:
            if not isinstance(item, dict):
                continue
            # 最低限これが無いと作品を特定できない。型まで見るのは、
            # このファイルはセッションをまたいで残るので、壊れた1件で
            # 起動できなくなると手で消すまで復旧できないため。
            if not (isinstance(item.get("site"), str) and item["site"]
                    and isinstance(item.get("url"), str) and item["url"]):
                continue
            records.append({k: item.get(k) for k in _QUEUE_FIELDS})
        with self._lock:
            self._records = records

    def records(self) -> list[dict]:
        with self._lock:
            return [dict(r) for r in self._records]

    def replace(self, records: list[dict]) -> None:
        clean = [{k: r.get(k) for k in _QUEUE_FIELDS} for r in records]
        with self._lock:
            if clean == self._records:
                return          # 変化がなければ書かない
            self._records = clean
        _write_json(QUEUE_PATH, {"items": clean})

    def clear(self) -> None:
        self.replace([])

    def __len__(self) -> int:
        with self._lock:
            return len(self._records)
