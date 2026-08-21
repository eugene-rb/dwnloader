"""yt-dlp を使った動画・音声のダウンロード。

sites/ のアダプタは「ページを読んで画像を並べる」形なので動画には合わない。
yt-dlp に関わる知識はすべてここへ閉じ込め、外から使うのは
`resolve_media()` と `MediaJob` だけにしている。

`MediaJob` は `GalleryJob` と同じ形（job_id / cancel / run）で作ってあるので、
`DownloadManager` からは両者を区別せずに扱える。
"""

from __future__ import annotations

import base64
import hashlib
import re
import shutil
import tempfile
import threading
import time
from pathlib import Path

from .config import Settings, media_dir
from .models import SourceRef, Status
from .sites import is_url
from .util import human_size

KIND_VIDEO = "video"
KIND_AUDIO = "audio"
KIND_LABEL = {KIND_VIDEO: "動画", KIND_AUDIO: "音声"}

#: 保存名。PDF 側の既定 `{title} [{artist}] ({site}-{id})` と揃えている。
OUTPUT_TEMPLATE = ("%(title)s [%(uploader,channel,creator|unknown)s] "
                   "(%(extractor_key)s-%(id)s).%(ext)s")

#: 画質の指定。`<=?` は「その情報が無い形式を落とさない」ための書き方。
_QUALITY_FORMAT = {
    "best": "bestvideo*+bestaudio/best",
    "1080": "bestvideo[height<=?1080]+bestaudio/best[height<=?1080]/best",
    "720": "bestvideo[height<=?720]+bestaudio/best[height<=?720]/best",
    "480": "bestvideo[height<=?480]+bestaudio/best[height<=?480]/best",
}

#: 時間のかかる後処理。これが始まったら中止が効かないことを伝える。
_SLOW_POSTPROCESSORS = ("Merger", "FFmpegExtractAudio", "FFmpegVideoConvertor",
                        "FFmpegVideoRemuxer")

_SITE_CLEAN = re.compile(r"[^a-z0-9._-]+")
_ANSI = re.compile(r"\x1b\[[0-9;]*m")
_LEVEL_PREFIX = re.compile(r"^\s*(ERROR|WARNING):\s*", re.I)


class MediaError(Exception):
    """動画・音声として取得できなかった。"""


class _Canceled(Exception):
    """内部用。ユーザーによる中断。"""


# ====================================================================== URL判定

_lock = threading.Lock()
_extractors: list | None = None
_import_error = ""


def _extractor_classes() -> list:
    """yt-dlp の抽出器一覧。初回だけ読み込む（import に 0.3 秒ほどかかる）。

    Generic は「どんなURLでも一応受ける」ので外す。入れてしまうと、ただの
    ウェブページをコピーしただけでダウンロードが始まる。
    """
    global _extractors, _import_error
    with _lock:
        if _extractors is None:
            try:
                from yt_dlp.extractor import gen_extractor_classes
            except Exception as exc:
                _import_error = f"{type(exc).__name__}: {exc}"
                _extractors = []
            else:
                _extractors = [ie for ie in gen_extractor_classes()
                               if ie.ie_key() != "Generic" and ie.working()]
        return _extractors


def available() -> bool:
    """yt-dlp が使えるか。使えないときは動画・音声のURLを拾わない。"""
    return bool(_extractor_classes())


def import_error() -> str:
    _extractor_classes()
    return _import_error


def resolve_media(url: str, kind: str, *, allow_generic: bool = False) -> SourceRef | None:
    """yt-dlp が対応していると分かるURLだけを SourceRef にする。

    allow_generic を立てると、どの抽出器にも当たらないURLも受け付ける
    （直リンクの mp4 などを yt-dlp の汎用抽出器に賭ける）。クリップボード監視
    からも立つが、その場合は呼び出し側（session.py）が除外リストで絞り込む。
    """
    url = (url or "").strip()
    if not is_url(url):
        return None
    kind = kind if kind in KIND_LABEL else KIND_VIDEO

    for ie in _extractor_classes():
        try:
            if not ie.suitable(url):
                continue
        except Exception:
            continue           # 抽出器1つの不調で判定全体を止めない
        return SourceRef(site=_site_of(ie), gid=_id_of(ie, url), url=url, kind=kind)

    if allow_generic and available():
        return SourceRef(site="web", gid=_hashed(url), url=url, kind=kind)
    return None


def _site_of(ie) -> str:
    try:
        name = str(ie.IE_NAME or ie.ie_key())
    except Exception:
        name = ie.ie_key()
    # "niconico:live" のような下位区分は落として、表示は配信元だけにする
    return _SITE_CLEAN.sub("", name.lower().split(":")[0]) or "media"


def _id_of(ie, url: str) -> str:
    """抽出器が持つ正規表現からID部分を取る。通信はしない。"""
    try:
        gid = ie.get_temp_id(url)
    except Exception:
        gid = None
    return str(gid) if gid else _hashed(url)


def _hashed(url: str) -> str:
    return hashlib.sha1(url.encode("utf-8", "ignore")).hexdigest()[:12]


def ffmpeg_path() -> str:
    """ffmpeg の場所。無ければ空文字。"""
    return shutil.which("ffmpeg") or ""


def _clean_message(text) -> str:
    """yt-dlp のメッセージから色指定と重複した見出しを落とす。"""
    text = _ANSI.sub("", str(text or "")).strip()
    return _LEVEL_PREFIX.sub("", text)


def _duration_text(seconds) -> str:
    try:
        total = int(float(seconds))
    except (TypeError, ValueError):
        return ""
    if total <= 0:
        return ""
    h, rest = divmod(total, 3600)
    m, s = divmod(rest, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m}:{s:02d}"


class _Logger:
    """yt-dlp のメッセージをアプリのログへ流す。

    debug/info は取得形式の列挙などで大量に出るので捨てる。警告と失敗理由は
    原因究明に効くので必ず出す。
    """

    def __init__(self, log) -> None:
        self._log = log

    def debug(self, msg) -> None:
        pass

    def info(self, msg) -> None:
        pass

    def warning(self, msg) -> None:
        self._log("warn", _clean_message(msg))

    def error(self, msg) -> None:
        self._log("error", _clean_message(msg))


# ====================================================================== ジョブ

class MediaJob:
    """動画または音声を1本取得するまで。"""

    def __init__(self, job_id: str, ref: SourceRef, settings: Settings, emit,
                 reuse_path: str = ""):
        self.job_id = job_id
        self.ref = ref
        self.settings = settings
        self.emit = emit
        # GalleryJob と引数の形を合わせるためだけに受ける。動画は途中まで
        # 落とした .part から続けるので、置き換え先の指定は使わない。
        self.reuse_path = reuse_path
        self.cancel = threading.Event()

        # 走り出した後にトグルが変わっても、このジョブの動きは変えない
        self.playlist_all = bool(settings["playlist_all"])

        self._meta_sent = False
        self._processing = False
        self._last_progress = 0.0
        self._path = ""

    # ------------------------------------------------------------------ 実行

    def run(self) -> None:
        try:
            from yt_dlp import YoutubeDL
        except Exception as exc:
            self._log("error", f"yt-dlp を読み込めません: {exc}")
            self._finish(False, "", "yt-dlp が入っていません"
                                    "（pip install -r requirements.txt）")
            return

        try:
            if self.cancel.is_set():
                raise _Canceled

            self._status(Status.FETCHING)
            with YoutubeDL(self._options()) as ydl:
                info = ydl.extract_info(self.ref.url, download=True)

            if self.cancel.is_set():
                raise _Canceled

            if isinstance(info, dict) and info.get("entries") is not None:
                # 遅延リストのことがあるので、数える前に1度だけ実体化する
                entries = [e for e in info["entries"] if isinstance(e, dict)]
                info["entries"] = entries
                if self.playlist_all:
                    self._log("ok", f"再生リストから {len(entries)} 本を取得しました")
                else:
                    self._log("warn", "再生リスト・チャンネルのURLです。先頭の1本だけを"
                                      "取得しました（全部欲しいときは「リスト全体」を"
                                      "オンにして追加し直してください）")
            path = self._result_path(info)
            if not path or not Path(path).exists():
                raise MediaError("保存されたファイルを特定できませんでした")
            self._finish(True, path, "")

        except _Canceled:
            self._status(Status.CANCELED)
            self._finish(False, "", "キャンセルしました")
        except Exception as exc:
            # 中止すると yt-dlp が別の例外に包み直すことがあるので、
            # 例外の型ではなく中止フラグを先に見る。
            if self.cancel.is_set():
                self._status(Status.CANCELED)
                self._finish(False, "", "キャンセルしました")
                return
            message = _clean_message(exc) or type(exc).__name__
            self._log("error", f"{self.ref.url}: {message}")
            self._finish(False, "", message)

    # ------------------------------------------------------------ yt-dlp の設定

    def _options(self) -> dict:
        kind = self.ref.kind or KIND_VIDEO
        outdir = media_dir(self.settings, kind)
        outdir.mkdir(parents=True, exist_ok=True)
        ffmpeg = ffmpeg_path()
        retries = max(1, int(self.settings["retries"]))

        opts: dict = {
            "outtmpl": {"default": OUTPUT_TEMPLATE},
            "paths": {"home": str(outdir)},
            # noplaylist は「動画とリストの両方を指すURL（watch?v=…&list=…）なら
            # 動画を採る」だけで、リスト単体のURLには効かない（実測: チャンネルURL
            # が 5545 件に展開された）。全体を取るかどうかは playlist_items で決める。
            # ここは常に True のまま。動画を指しているURLから、その動画が属する
            # リスト全部が落ちてくるのは誰も望んでいない。
            "noplaylist": True,
            "quiet": True,
            "noprogress": True,
            "no_color": True,
            "logger": _Logger(self._log),
            "progress_hooks": [self._on_progress],
            "postprocessor_hooks": [self._on_postprocess],
            "retries": retries,
            "fragment_retries": retries,
            "socket_timeout": float(self.settings["timeout"]),
            "windowsfilenames": True,
            "trim_file_name": max(40, int(self.settings["filename_max_len"])),
            # 同名があれば落とし直さない。同じ動画で容量を二重に使わない。
            "overwrites": False,
            "continuedl": True,
            "ignoreerrors": False,
            "postprocessors": [],
        }
        if not self.playlist_all:
            opts["playlist_items"] = "1"
        else:
            # 1本が非公開・削除済みでもリスト全体を捨てない。理由はログに出る。
            opts["ignoreerrors"] = "only_download"
        if ffmpeg:
            opts["ffmpeg_location"] = ffmpeg

        cookies_file = str(self.settings["media_cookies_file"] or "").strip()
        if cookies_file:
            if Path(cookies_file).is_file():
                opts["cookiefile"] = cookies_file
            else:
                self._log("warn", f"Cookie ファイルが見つかりません: {cookies_file}")

        if kind == KIND_AUDIO:
            if not ffmpeg:
                raise MediaError("音声の取り出しには ffmpeg が必要です"
                                 "（winget install Gyan.FFmpeg）")
            opts["format"] = "bestaudio/best"
            opts["postprocessors"].append({
                "key": "FFmpegExtractAudio",
                "preferredcodec": str(self.settings["audio_format"] or "mp3"),
                "preferredquality": str(int(self.settings["audio_bitrate"] or 192)),
            })
        elif ffmpeg:
            quality = str(self.settings["video_quality"] or "best")
            opts["format"] = _QUALITY_FORMAT.get(quality, _QUALITY_FORMAT["best"])
            # 組み合わせが mp4 に入らないときは yt-dlp が mkv に切り替える
            opts["merge_output_format"] = "mp4"
        else:
            # 映像と音声を結合できないので、最初から1つになっている形式に限る
            opts["format"] = "best[ext=mp4]/best"
            self._log("warn", "ffmpeg が見つからないため、画質は結合不要の形式に限られます")

        if ffmpeg:
            # タイトルや投稿者をファイル自身に書き込む（PDF の文書情報と同じ考え）
            opts["postprocessors"].append({"key": "FFmpegMetadata"})
        return opts

    # ------------------------------------------------------------ yt-dlp からの通知

    def _on_progress(self, d: dict) -> None:
        if self.cancel.is_set():
            raise _Canceled

        info = d.get("info_dict") or {}
        if not self._meta_sent:
            self._emit_meta(info)

        status = d.get("status")
        if status not in ("downloading", "finished"):
            return

        bytes_done = int(d.get("downloaded_bytes") or 0)
        bytes_total = int(d.get("total_bytes") or d.get("total_bytes_estimate") or 0)

        if status == "finished":
            self._path = d.get("filename") or self._path
            bytes_done = bytes_done or bytes_total
            self._last_progress = 0.0        # 次のファイルの1回目は間引かない
        else:
            # フックは細かく呼ばれる。UI へ流す間隔はこちらで間引く。
            now = time.monotonic()
            if now - self._last_progress < 0.2:
                return
            self._last_progress = now

        count = self._playlist_count(info)
        if count > 1:
            # リストではバーを「何本目まで終わったか」で進める。1本ごとに
            # 巻き戻るバーは、止まったのか進んだのかが分からない。
            index = int(info.get("playlist_index") or 1)
            done, total = (index if status == "finished" else index - 1), count
        else:
            done = bytes_done
            total = bytes_done if status == "finished" else bytes_total

        self.emit("progress", {"id": self.job_id, "done": done, "total": total,
                               "detail": self._detail(info, d, bytes_done, bytes_total)})

    @staticmethod
    def _playlist_count(info: dict) -> int:
        try:
            return int(info.get("playlist_count") or info.get("n_entries") or 0)
        except (TypeError, ValueError):
            return 0

    def _on_postprocess(self, d: dict) -> None:
        name = str(d.get("postprocessor") or "")
        if d.get("status") != "started":
            path = (d.get("info_dict") or {}).get("filepath")
            if path:
                self._path = str(path)
            return
        if self._processing:
            return
        self._processing = True
        self._status(Status.PROCESSING)
        if name in _SLOW_POSTPROCESSORS:
            # ffmpeg の処理には割り込めない。押しても止まらない理由を先に出す。
            self._log("info", "変換・結合中です（この間は中止できません）")

    def _detail(self, info: dict, d: dict, done: int, total: int) -> str:
        count = self._playlist_count(info)
        if count > 1:
            head = f"{int(info.get('playlist_index') or 1)}/{count}本目 "
        elif self.ref.kind == KIND_VIDEO and info.get("vcodec") == "none":
            head = "音声 "          # 映像と音声が別配信のとき、2本目だと分かるように
        else:
            head = ""
        size = f"{human_size(done)} / {human_size(total)}" if total else human_size(done)
        speed = d.get("speed")
        return f"{head}{size} · {human_size(speed)}/s" if speed else f"{head}{size}"

    def _emit_meta(self, info: dict) -> None:
        self._meta_sent = True
        title = str(info.get("title") or self.ref.url)
        count = self._playlist_count(info)
        if count > 1:
            # リストの1本目の題名を出すと、以降ずっと嘘になる
            title = str(info.get("playlist") or info.get("playlist_title") or title)
        bits = [b for b in (str(info.get("uploader") or info.get("channel") or ""),
                            f"{count}本" if count > 1 else _duration_text(info.get("duration")),
                            KIND_LABEL.get(self.ref.kind, "")) if b]
        self.emit("meta", {
            "id": self.job_id,
            "title": title,
            "subtitle": " · ".join(bits),
            "tags": [],
            "artists": [], "groups": [], "series": [],
            "kind": self.ref.kind,
            "language": "",
        })
        self._status(Status.DOWNLOADING)

        thumb = info.get("thumbnail")
        if thumb:
            # フックの中で通信すると、その間ダウンロードが止まる
            threading.Thread(target=self._send_thumbnail, args=(str(thumb),),
                             daemon=True, name="media-thumb").start()

    def _send_thumbnail(self, url: str) -> None:
        """配信元のサムネイルをカードに出す。失敗しても黙って諦める。"""
        from .net import get_with_retry, make_session
        from .pdfbuild import make_thumbnail, normalize_to_jpeg

        session = make_session(pool=2)
        try:
            resp = get_with_retry(session, url, timeout=float(self.settings["timeout"]),
                                  retries=2, cancel=self.cancel)
            if resp.status_code != 200 or not resp.content:
                return
            with tempfile.TemporaryDirectory(prefix="dwnloader-thumb-") as tmp:
                dest = normalize_to_jpeg(resp.content, Path(tmp) / "cover.jpg", quality=85)
                data = base64.b64encode(make_thumbnail(dest)).decode("ascii")
            self.emit("thumbnail", {"id": self.job_id, "data": data})
        except Exception:
            pass
        finally:
            session.close()

    # ------------------------------------------------------------ 内部処理

    def _result_path(self, info) -> str:
        """実際に保存されたファイル。後処理で拡張子が変わるので info から拾う。

        リストのURLだと1枚外側に包まれて返るので、その中も見る。
        """
        for candidate in self._info_candidates(info):
            for entry in (candidate.get("requested_downloads") or []):
                path = entry.get("filepath") or entry.get("_filename")
                if path:
                    return str(path)
            for key in ("filepath", "_filename"):
                if candidate.get(key):
                    return str(candidate[key])
        return self._path

    @staticmethod
    def _info_candidates(info) -> list[dict]:
        if not isinstance(info, dict):
            return []
        entries = [e for e in (info.get("entries") or []) if isinstance(e, dict)]
        return [info, *entries]

    # ------------------------------------------------------------ 通知

    def _status(self, status: Status, message: str = "") -> None:
        self.emit("status", {"id": self.job_id, "status": status.name,
                             "label": status.value, "message": message})

    def _log(self, level: str, message: str) -> None:
        self.emit("log", {"level": level, "message": message})

    def _finish(self, ok: bool, path: str, error: str) -> None:
        self.emit("finished", {"id": self.job_id, "ok": ok, "path": path,
                               "error": error, "missing": 0})
