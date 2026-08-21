"""ダウンロードジョブの実行。UI フレームワークに依存しない。

進捗などの通知は `emit(event_name, payload_dict)` という1本の関数で外へ出す。
受け手が Qt でも Web でも同じように扱えるようにしている。
"""

from __future__ import annotations

import base64
import shutil
import tempfile
import threading
import time
from concurrent.futures import Future, ThreadPoolExecutor
from pathlib import Path
from typing import Callable

from . import APP_TITLE, APP_VERSION
from .config import Settings
from .models import GalleryMeta, ImageRef, SourceRef, Status
from .net import TransientError, get_with_retry, make_session
from .pdfbuild import PdfMeta, build_pdf, make_thumbnail, normalize_to_jpeg
from .sites import AuthRequired, Context, SiteError, Unsupported, adapter_for
from .util import format_filename, unique_path

TMP_PREFIX = "dwnloader-"

#: URL失効を疑って作り直しを試みる回数
_URL_REBUILD_ATTEMPTS = 3

#: これより新しい作業フォルダは触らない（誰かが使っている可能性がある）
_SWEEP_MIN_AGE = 300.0

#: 通知の送り先。(イベント名, 中身) を受け取る。
Emit = Callable[[str, dict], None]


def sweep_temp_dirs(min_age: float = _SWEEP_MIN_AGE) -> int:
    """強制終了などで残った作業用ディレクトリを片付ける。

    多重起動は防いでいるが、それをすり抜けた場合に動作中のダウンロードを
    壊さないよう、最近更新されたフォルダは残す。掃除の失敗より、
    走っている処理を消す方が害が大きい。
    """
    removed = 0
    root = Path(tempfile.gettempdir())
    now = time.time()
    try:
        candidates = list(root.glob(f"{TMP_PREFIX}*"))
    except OSError:
        return 0

    for path in candidates:
        if not path.is_dir():
            continue
        try:
            if now - path.stat().st_mtime < min_age:
                continue
        except OSError:
            continue
        shutil.rmtree(path, ignore_errors=True)
        removed += 1
    return removed


class _Canceled(Exception):
    """内部用。ユーザーによる中断。"""


class GalleryJob:
    """作品1件を取得して PDF にするまで。"""

    def __init__(self, job_id: str, ref: SourceRef, settings: Settings, emit: Emit,
                 reuse_path: str = ""):
        self.job_id = job_id
        self.ref = ref
        self.settings = settings
        self.emit = emit
        # 再試行で、前回の不完全な PDF を置き換えたいときの出力先。
        # 別名で出すと、壊れた方が綺麗な名前に居座り続けてしまう。
        self.reuse_path = reuse_path
        self.cancel = threading.Event()

    # ------------------------------------------------------------------ 実行

    def run(self) -> None:
        session = make_session(pool=max(8, int(self.settings["image_workers"]) * 2))
        ctx = Context(session=session, settings=self.settings, cancel=self.cancel)
        tmpdir: str | None = None

        try:
            if self.cancel.is_set():
                raise _Canceled

            adapter = adapter_for(self.ref.site)

            self._status(Status.FETCHING)
            meta = adapter.fetch(self.ref, ctx)
            self._emit_meta(meta)
            self._log("info", f"[{adapter.label}] {meta.title} — {len(meta.images)}ページ")

            out_path = self._output_path(meta)

            self._status(Status.DOWNLOADING)
            tmpdir = tempfile.mkdtemp(prefix=TMP_PREFIX)
            jpegs, missing = self._download_all(meta, adapter, ctx, Path(tmpdir))

            if self.cancel.is_set():
                raise _Canceled

            self._status(Status.BUILDING)
            build_pdf(jpegs, out_path, self._pdf_meta(meta),
                      warn=lambda m: self._log("warn", m))

            if self.settings["keep_images"]:
                self._keep_images(Path(tmpdir), out_path)

            self._finish(True, str(out_path), "", missing)

        except _Canceled:
            self._status(Status.CANCELED)
            self._finish(False, "", "キャンセルしました", 0)
        except Unsupported as exc:
            self._log("warn", f"{self.ref.url}: {exc}")
            self._finish(False, "", str(exc), 0)
        except (AuthRequired, SiteError) as exc:
            self._log("error", f"{self.ref.url}: {exc}")
            self._finish(False, "", str(exc), 0)
        except Exception as exc:  # 想定外もUIに出す。落とさない。
            self._log("error", f"{self.ref.url}: {type(exc).__name__}: {exc}")
            self._finish(False, "", f"{type(exc).__name__}: {exc}", 0)
        finally:
            if tmpdir:
                shutil.rmtree(tmpdir, ignore_errors=True)
            session.close()

    # ------------------------------------------------------------ 通知

    def _status(self, status: Status, message: str = "") -> None:
        self.emit("status", {"id": self.job_id, "status": status.name,
                             "label": status.value, "message": message})

    def _log(self, level: str, message: str) -> None:
        self.emit("log", {"level": level, "message": message})

    def _finish(self, ok: bool, path: str, error: str, missing: int) -> None:
        self.emit("finished", {"id": self.job_id, "ok": ok, "path": path,
                               "error": error, "missing": missing})

    def _emit_meta(self, meta: GalleryMeta) -> None:
        self.emit("meta", {
            "id": self.job_id,
            "title": meta.title,
            "artists": meta.artists,
            "groups": meta.groups,
            "series": meta.series,
            "tags": meta.tags[:12],
            "pages": len(meta.images),
            "kind": meta.kind,
            "language": meta.language,
        })

    # ------------------------------------------------------------ 内部処理

    @staticmethod
    def _pdf_meta(meta: GalleryMeta) -> PdfMeta:
        """作品情報を PDF の文書情報に写す。

        作者が取れないサイトではサークル名で代用する。キーワードには
        タグとキャラクターに加えて出典URLを入れ、後から辿れるようにする。
        """
        keywords = [*meta.tags, *meta.characters]
        if meta.page_url:
            keywords.append(meta.page_url)

        return PdfMeta(
            title=meta.title,
            author="、".join(meta.artists or meta.groups),
            subject="、".join(meta.series),
            keywords=[k for k in keywords if k][:40],
            creator=f"{APP_TITLE} {APP_VERSION}",
            created=meta.published,
        )

    def _output_path(self, meta: GalleryMeta) -> Path:
        if self.reuse_path:
            # build_pdf は一時ファイルに書いてから置換するので、失敗しても
            # 既存ファイルは壊れない
            reuse = Path(self.reuse_path)
            reuse.parent.mkdir(parents=True, exist_ok=True)
            return reuse

        base = Path(self.settings["output_dir"])
        if self.settings["site_subfolder"]:
            base = base / meta.site
        base.mkdir(parents=True, exist_ok=True)
        name = format_filename(
            self.settings["filename_template"], meta,
            max_len=int(self.settings["filename_max_len"]),
        )
        return unique_path(base / f"{name}.pdf")

    def _download_all(self, meta: GalleryMeta, adapter, ctx: Context,
                      workdir: Path) -> tuple[list[Path], int]:
        """全ページを取得する。戻り値は (JPEGのパス, 取得できなかったページ数)。"""
        total = len(meta.images)
        done = 0
        lock = threading.Lock()
        refresh_lock = threading.Lock()
        quality = int(self.settings["jpeg_quality"])
        keep = bool(self.settings["keep_images"])
        results: dict[int, Path] = {}
        errors: list[str] = []

        origdir = workdir / "original"
        if keep:
            origdir.mkdir(parents=True, exist_ok=True)

        workers = max(1, min(int(self.settings["image_workers"]), adapter.max_image_workers))

        def fetch_one(image: ImageRef) -> None:
            nonlocal done
            if self.cancel.is_set():
                return
            try:
                data, used_fmt = self._fetch_image(image, meta, adapter, ctx, refresh_lock)
                if keep:
                    (origdir / f"{image.index:05d}.{used_fmt}").write_bytes(data)
                dest = workdir / f"{image.index:05d}.jpg"
                normalize_to_jpeg(data, dest, quality=quality)
                if image.index == 1:
                    # 表紙は1ページ目から作る。追加の通信はしない。
                    try:
                        thumb = base64.b64encode(make_thumbnail(dest)).decode("ascii")
                        self.emit("thumbnail", {"id": self.job_id, "data": thumb})
                    except Exception:
                        pass
            except _Canceled:
                return
            except Exception as exc:
                with lock:
                    errors.append(f"p.{image.index}: {exc}")
                return
            with lock:
                results[image.index] = dest
                done += 1
                self.emit("progress", {"id": self.job_id, "done": done, "total": total})

        with ThreadPoolExecutor(max_workers=workers) as pool:
            list(pool.map(fetch_one, meta.images))

        if self.cancel.is_set():
            raise _Canceled
        if not results:
            raise SiteError(f"画像を1枚も取得できませんでした（{errors[0] if errors else '原因不明'}）")

        missing = total - len(results)
        if missing:
            head = errors[0] if errors else "原因不明"
            self._log("warn", f"{meta.title}: {missing}/{total} ページを取得できませんでした（{head}）")

        return [results[i] for i in sorted(results)], missing

    def _fetch_image(self, image: ImageRef, meta: GalleryMeta, adapter, ctx: Context,
                     refresh_lock: threading.Lock) -> tuple[bytes, str]:
        """1ページ分を取得。404 は URL 失効の可能性があるので作り直して再挑戦する。

        並列取得中に URL が作り直された場合、古い URL を掴んだままのスレッドも
        最新の URL を読み直して再試行する（自分が作り直した本人かどうかは問わない）。
        """
        tried: set[str] = set()

        for _ in range(_URL_REBUILD_ATTEMPTS):
            current = self._current_ref(meta, image)
            candidates = [u for u in (current.url, *current.fallbacks) if u not in tried]

            for url in candidates:
                if self.cancel.is_set():
                    raise _Canceled
                tried.add(url)
                resp = get_with_retry(
                    ctx.session, url, headers=current.headers,
                    timeout=ctx.timeout, retries=ctx.retries,
                    limiter=adapter.limiter, cancel=self.cancel,
                )
                if resp.status_code == 200 and resp.content:
                    if url != current.url:
                        self._log("warn", f"p.{image.index}: 代替URLで取得しました"
                                          "（画質が下がる場合があります）")
                    fmt = url.rsplit(".", 1)[-1].lower()
                    return resp.content, (fmt if fmt.isalnum() and len(fmt) <= 5 else current.fmt)

            # 全滅した。URL が失効した可能性があるので作り直す。
            with refresh_lock:
                latest = self._current_ref(meta, image)
                if latest.url not in tried:
                    # 別スレッドが既に作り直していた。そちらを使う。
                    continue
                if not adapter.refresh(meta, ctx):
                    break
                self._log("info", "画像URLの有効期限切れを検出したため再取得します")

        raise TransientError("画像を取得できません")

    @staticmethod
    def _current_ref(meta: GalleryMeta, image: ImageRef) -> ImageRef:
        """refresh() で差し替わっている可能性があるので、常に最新を読み直す。"""
        for candidate in meta.images:
            if candidate.index == image.index:
                return candidate
        return image

    def _keep_images(self, workdir: Path, out_path: Path) -> None:
        """変換前の元画像を PDF と同名のフォルダに残す。"""
        src_dir = workdir / "original"
        if not src_dir.is_dir():
            return
        dest = out_path.with_suffix("")
        try:
            dest.mkdir(parents=True, exist_ok=True)
            for src in sorted(src_dir.iterdir()):
                if src.is_file():
                    shutil.copy2(src, dest / src.name)
        except OSError as exc:
            self._log("warn", f"元画像の保存に失敗しました: {exc}")


class DownloadManager:
    """同時実行数を保ちながらジョブを走らせる。

    レーンごとにプールを分ける。ギャラリー（画像を多数取得してPDF化）と
    メディア（yt-dlp）では1件あたりの重さも適正な同時数も違うので、
    片方が枠を埋めるともう片方が待たされてしまう。

    ジョブ表はレーンをまたいで1つ。中止も削除も job_id だけで引けるので、
    呼び出し側がどちらのレーンかを覚えておく必要はない。
    """

    def __init__(self, settings: Settings, emit: Emit, lanes: dict[str, int] | None = None):
        self.settings = settings
        self.emit = emit
        self._lock = threading.Lock()
        lanes = lanes or {"gallery": int(settings["gallery_workers"])}
        self._workers = {name: max(1, int(count)) for name, count in lanes.items()}
        self._pools = {name: ThreadPoolExecutor(max_workers=count,
                                                thread_name_prefix=name)
                       for name, count in self._workers.items()}
        self._jobs: dict[str, object] = {}

    def submit(self, job, lane: str = "gallery") -> Future:
        with self._lock:
            self._jobs[job.job_id] = job
            pool = self._pools[lane]
        return pool.submit(job.run)

    def job(self, job_id: str):
        with self._lock:
            return self._jobs.get(job_id)

    def cancel(self, job_id: str) -> bool:
        job = self.job(job_id)
        if job is None:
            return False
        job.cancel.set()
        return True

    def cancel_all(self) -> None:
        with self._lock:
            jobs = list(self._jobs.values())
        for job in jobs:
            job.cancel.set()

    def forget(self, job_id: str) -> None:
        with self._lock:
            self._jobs.pop(job_id, None)

    def set_workers(self, lane: str, count: int) -> None:
        """同時実行数を変える。ThreadPoolExecutor は後から変更できないので、
        新しいプールに差し替えて古い方は走行中の分だけ流し切らせる。"""
        count = max(1, int(count))
        with self._lock:
            if self._workers.get(lane) == count:
                return
            old = self._pools.get(lane)
            self._workers[lane] = count
            self._pools[lane] = ThreadPoolExecutor(max_workers=count,
                                                   thread_name_prefix=lane)
        if old is not None:
            threading.Thread(target=old.shutdown, kwargs={"wait": True},
                             daemon=True).start()

    def shutdown(self, timeout: float = 5.0) -> None:
        self.cancel_all()
        with self._lock:
            pools = list(self._pools.values())
        for pool in pools:
            pool.shutdown(wait=False, cancel_futures=True)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            time.sleep(0.05)
            break
