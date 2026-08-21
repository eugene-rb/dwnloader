"""アプリの状態とふるまい。描画を含まないので UI から切り離してテストできる。"""

from __future__ import annotations

import itertools
import os
import subprocess
import sys
import threading
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable
from urllib.parse import urlsplit

from . import media
from .clipboard import ClipboardWatcher
from .config import History, QueueStore, Settings, media_dir
from .downloader import DownloadManager, Emit, GalleryJob, sweep_temp_dirs
from .models import ACTIVE, SourceRef, Status
from .sites import ADAPTERS, extract_all, find_urls, resolve

SITE_LABEL = {"hitomi": "hitomi", "momonga": "momonga", "pixiv": "pixiv"}

#: 完了済みは履歴が持つので保存しない。片付いていないものだけ残す。
_PERSIST = {Status.QUEUED, Status.FETCHING, Status.DOWNLOADING, Status.BUILDING,
            Status.FAILED, Status.PARTIAL, Status.CANCELED}

_UNFINISHED = (Status.FAILED, Status.PARTIAL, Status.CANCELED)


def _result_label(kind: str) -> str:
    """保存されるものの呼び名。ギャラリーは PDF、動画・音声はそのまま。"""
    return media.KIND_LABEL.get(kind, "PDF")


def _swap_kind_label(subtitle: str, kind: str) -> str:
    """説明文に混じっている「動画」「音声」の表示だけを差し替える。"""
    label = media.KIND_LABEL[kind]
    others = tuple(media.KIND_LABEL.values())
    parts = [label if p in others else p for p in (subtitle or "").split(" · ") if p]
    if label not in parts:
        parts.append(label)
    return " · ".join(parts)


@dataclass
class Entry:
    job_id: str
    ref: SourceRef
    status: Status = Status.QUEUED
    title: str = ""
    subtitle: str = ""
    detail: str = ""
    message: str = ""
    out_path: str = ""
    thumb: str = ""                      # data URL 用の base64
    done: int = 0
    total: int = 0
    started: bool = False
    tags: list[str] = field(default_factory=list)

    @property
    def can_switch_kind(self) -> bool:
        """動画↔音声を切り替えられるか。

        走り出したものと保存済みは触らない。前者は落としている最中に行き先が
        変わることになり、後者は既にあるファイルと表示が食い違う。
        """
        return (bool(self.ref.kind) and not self.started
                and self.status in (Status.QUEUED, Status.FAILED, Status.CANCELED))

    def to_dict(self) -> dict:
        return {
            "id": self.job_id,
            "site": self.ref.site,
            "siteLabel": SITE_LABEL.get(self.ref.site, self.ref.site),
            "kind": self.ref.kind,
            "canSwitchKind": self.can_switch_kind,
            "url": self.ref.url,
            "status": self.status.name,
            "statusLabel": self.status.value,
            "title": self.title or self.ref.url,
            "subtitle": self.subtitle,
            "detail": self.detail,
            "message": self.message,
            "outPath": self.out_path,
            "thumb": self.thumb,
            "done": self.done,
            "total": self.total,
            "active": self.status in ACTIVE,
            "tags": self.tags,
        }


class Session:
    """キューの実体。UI へは emit() 経由でのみ知らせる。"""

    def __init__(self, settings: Settings, history: History, queue_store: QueueStore,
                 emit: Emit):
        self.settings = settings
        self.history = history
        self.queue_store = queue_store
        self.emit = emit

        self._lock = threading.RLock()
        self.entries: dict[str, Entry] = {}
        self._ids = itertools.count(1)

        self.manager = DownloadManager(settings, self._on_job_event, lanes={
            "gallery": int(settings["gallery_workers"]),
            "media": int(settings["video_workers"]),
        })
        self.clipboard = ClipboardWatcher(self._on_clipboard)
        self.clipboard.start()
        self.clipboard.set_enabled(bool(settings["watch_clipboard"]))

    # ============================================================== 起動処理

    def bootstrap(self) -> None:
        """一時フォルダの掃除と、前回の未完了の復元。"""
        stale = sweep_temp_dirs()
        if stale:
            self.log("info", f"前回の作業用フォルダ {stale} 件を削除しました")

        supported = "・".join(a.label for a in ADAPTERS.values())
        self.log("info", f"{supported} に対応しています。URLをコピーすると自動で取り込みます。")
        if media.available():
            self.log("info", "動画・音声は yt-dlp が対応するサイトから取得します"
                             f"（現在は{media.KIND_LABEL[self.media_kind()]}として保存）。")
            if not media.ffmpeg_path():
                self.log("warn", "ffmpeg が見つかりません。音声の取り出しと高画質動画の"
                                 "結合ができません（winget install Gyan.FFmpeg）")
        else:
            self.log("warn", "yt-dlp を読み込めないため、動画・音声は取得できません"
                             f"（{media.import_error()}）")
        self.restore_queue()

    # ============================================================== 通知

    def log(self, level: str, message: str) -> None:
        self.emit("log", {"level": level, "message": message})

    def notify(self, title: str, body: str, kind: str = "info") -> None:
        self.emit("notify", {"title": title, "body": body, "kind": kind})

    def _push(self, entry: Entry) -> None:
        self.emit("entry", entry.to_dict())

    def push_state(self) -> None:
        self.emit("state", self.snapshot())

    def snapshot(self) -> dict:
        with self._lock:
            entries = [e.to_dict() for e in self.entries.values()]
        return {
            "entries": entries,
            "settings": self.settings.as_dict(),
            # 実際に保存される場所。設定が空欄でも既定を出す（画面には
            # 「どこへ入るのか」を出したい。空欄の意味は設定側で説明する）。
            "dirs": {
                "pdf": str(self.settings["output_dir"]),
                "video": str(media_dir(self.settings, media.KIND_VIDEO)),
                "audio": str(media_dir(self.settings, media.KIND_AUDIO)),
            },
            "watching": self.clipboard.is_enabled(),
            "historyCount": len(self.history),
            "status": self.status_text(),
            "canStart": self.has_startable(),
            "unfinished": self.unfinished_count(),
            "alwaysOnTop": bool(self.settings["always_on_top"]),
        }

    # ============================================================== 状態計算

    def has_startable(self) -> bool:
        with self._lock:
            return any(e.status is Status.QUEUED and not e.started
                       for e in self.entries.values())

    def unfinished_count(self) -> int:
        with self._lock:
            return sum(1 for e in self.entries.values() if e.status in _UNFINISHED)

    def status_text(self) -> str:
        """状況に応じて言い換える。0が並ぶだけの表示は情報量がない。"""
        with self._lock:
            entries = list(self.entries.values())

        running = [e for e in entries
                   if e.status in ACTIVE and e.status is not Status.QUEUED]
        queued = [e for e in entries if e.status is Status.QUEUED]
        unfinished = [e for e in entries if e.status in _UNFINISHED]

        if running:
            head = running[0]
            # 単位はジョブ側が決める（ページ数だったりバイト数だったりする）
            detail = head.detail or head.status.value
            more = f" ほか{len(running) - 1}件" if len(running) > 1 else ""
            return f"{head.title[:28]} — {detail}{more}"
        if queued:
            return f"{len(queued)}件が待機中"
        if unfinished:
            return f"{len(unfinished)}件が未完了 — 再試行できます"
        if self.clipboard.is_enabled():
            return f"監視中 — 保存先 {self.settings['output_dir']}"
        return "監視は停止中です"

    # ============================================================== 追加

    def media_kind(self) -> str:
        kind = str(self.settings["media_kind"] or "")
        return kind if kind in media.KIND_LABEL else media.KIND_VIDEO

    def _blacklist_hosts(self) -> set[str]:
        raw = str(self.settings["clipboard_blacklist"] or "")
        return {h.strip().lower() for h in raw.replace("\n", ",").split(",") if h.strip()}

    def _whitelist_hosts(self) -> set[str]:
        raw = str(self.settings["clipboard_whitelist"] or "")
        return {h.strip().lower() for h in raw.replace("\n", ",").split(",") if h.strip()}

    @staticmethod
    def _host_matches(url: str, hosts: set[str]) -> bool:
        """ドメインの完全一致のみ。サブドメインは別ドメインとして区別する
        （docs.google.com は google.com には一致しない）。yt-dlp の Generic は
        サブドメインごとに挙動が違うことがあり、まとめて扱うと片方の成功・失敗が
        無関係なサブドメインへ波及してしまうため。
        """
        if not hosts:
            return False
        host = (urlsplit(url).hostname or "").lower()
        return bool(host) and host in hosts

    def _learn_blacklist(self, url: str) -> None:
        """yt-dlp の汎用抽出器すら失敗したドメインを除外リストへ足す。

        次にクリップボードで同じドメインを拾っても、汎用抽出器には回さない
        （専用抽出器のマッチには影響しない）。手動貼り付けはこの影響を受けない。
        一度でも成功して信頼リストに載っているドメインは、1回の失敗では除外しない
        （たまたまの通信エラーなどでフラップさせないため）。
        """
        host = (urlsplit(url).hostname or "").lower()
        if not host:
            return
        if self._host_matches(url, self._whitelist_hosts()):
            return
        if self._host_matches(url, self._blacklist_hosts()):
            return                     # 既に除外済み
        raw = str(self.settings["clipboard_blacklist"] or "").strip()
        self.settings["clipboard_blacklist"] = f"{raw}, {host}" if raw else host
        self.log("info", f"クリップボード監視の除外リストに {host} を追加しました"
                         "（汎用抽出器で取得できなかったため。設定 → ダウンロードタブから編集できます）")
        self.push_state()

    def _learn_whitelist(self, url: str) -> None:
        """yt-dlp の汎用抽出器で実際に成功したドメインを信頼リストへ足す。

        除外リストに同じドメイン（完全一致）が載っていれば掃除する。ドメインは
        完全一致でしか比較しないので、他のサブドメインの除外には影響しない
        （docs.google.com が成功しても google.com の除外はそのまま残る）。
        設定画面で同じドメインを両方のリストに手で足してしまった場合の保険として、
        _resolve_text では信頼リストを除外リストより優先して扱う。
        """
        host = (urlsplit(url).hostname or "").lower()
        if not host:
            return
        already_white = self._host_matches(url, self._whitelist_hosts())

        raw_black = str(self.settings["clipboard_blacklist"] or "")
        black_list = [h.strip() for h in raw_black.replace("\n", ",").split(",") if h.strip()]
        kept = [h for h in black_list if h.lower() != host]
        cleaned = len(kept) != len(black_list)
        if cleaned:
            self.settings["clipboard_blacklist"] = ", ".join(kept)
            self.log("info", f"クリップボード監視の除外リストから {host} を外しました"
                             "（汎用抽出器での取得に成功したため）")

        if not already_white:
            raw_white = str(self.settings["clipboard_whitelist"] or "").strip()
            self.settings["clipboard_whitelist"] = f"{raw_white}, {host}" if raw_white else host
            self.log("info", f"クリップボード監視の信頼リストに {host} を追加しました"
                             "（汎用抽出器での取得に成功したため）")

        if cleaned or not already_white:
            self.push_state()

    def _resolve_text(self, text: str, *, allow_generic: bool = False,
                      blacklist: bool = False) -> list[SourceRef]:
        """テキストからギャラリーと動画・音声のURLを拾う。

        ギャラリー側を先に見る。hitomi などは yt-dlp も一応受け取るが、
        このアプリでの本来の扱いは PDF 化なのでそちらを優先する。

        blacklist を立てると、clipboard_blacklist に列挙されたドメインだけは
        yt-dlp の汎用抽出器（Generic）に回さない。専用抽出器のマッチには影響しない
        （X/Twitter などの既知サイトは除外リストと無関係に動く）。
        clipboard_whitelist に載っているドメインは除外リストより優先する
        （過去に汎用抽出器で成功した実績があるため）。
        """
        refs = extract_all(text)
        seen = {r.key for r in refs}
        kind = self.media_kind()
        black_hosts = self._blacklist_hosts() if blacklist else set()
        white_hosts = self._whitelist_hosts() if blacklist else set()

        for cand in find_urls(text):
            if resolve(cand) is not None:
                continue                       # ギャラリー側で拾い済み
            blocked = (blacklist and self._host_matches(cand, black_hosts)
                      and not self._host_matches(cand, white_hosts))
            ref = media.resolve_media(cand, kind, allow_generic=allow_generic and not blocked)
            if ref is not None and ref.key not in seen:
                seen.add(ref.key)
                refs.append(ref)
        return refs

    def _on_clipboard(self, text: str) -> None:
        # 既知サイト以外もyt-dlpの汎用抽出器で拾う（ブラックリスト式）。
        # 除外ドメインだけは対象外にして、ただのページ巡回コピーの誤検出を減らす。
        refs = self._resolve_text(text, allow_generic=True, blacklist=True)
        if refs:
            self.add_refs(refs, source="クリップボード")

    def add_text(self, text: str, source: str = "手動") -> int:
        # 手で貼られたURLは、判定に外れても yt-dlp の汎用抽出器に賭ける
        refs = self._resolve_text(text, allow_generic=True)
        if not refs:
            self.notify("追加できません",
                        "hitomi.la / momon-ga.com / pixiv.net の作品URL、"
                        "または動画・音声のURLを入れてください", "warning")
            return 0
        return self.add_refs(refs, source)

    def add_refs(self, refs: list[SourceRef], source: str) -> int:
        added = skipped = duplicates = 0
        focus = ""
        new_entries: list[Entry] = []

        for ref in refs:
            existing = self._find_entry(ref.key)
            if existing is not None:
                # 行を増やさず、既にある行へ案内する。未完了なら再試行の
                # 入口がそこに出ている。
                duplicates += 1
                focus = focus or existing.job_id
                continue

            if self.settings["skip_downloaded"] and ref.key in self.history:
                entry = self._create(ref, Status.SKIPPED)
                entry.out_path = self.history.path_for(ref.key) or ""
                entry.subtitle = f"取得済み — 保存済みの{_result_label(ref.kind)}を開けます"
                entry.detail = "取得済み"
                skipped += 1
            else:
                entry = self._create(ref, Status.QUEUED)
                entry.detail = "待機中"
                added += 1
            new_entries.append(entry)

        for entry in new_entries:
            self._push(entry)
        if self.settings["auto_start"]:
            for entry in new_entries:
                if entry.status is Status.QUEUED:
                    self.start(entry.job_id)

        if added or skipped or duplicates:
            sites = "・".join(sorted({SITE_LABEL.get(r.site, r.site) for r in refs}))
            parts = []
            if added:
                parts.append(f"{added}件を追加（{sites}）" if not (skipped or duplicates)
                             else f"{added}件を追加")
            if skipped:
                parts.append(f"{skipped}件は取得済み")
            if duplicates:
                parts.append(f"{duplicates}件はすでにキューにあります")
            self.notify(f"{source}から検出", "、".join(parts),
                        "info" if added else "warning")
        if focus and not added:
            self.emit("focus", {"id": focus})

        self.persist()
        self.push_state()
        return added

    def _create(self, ref: SourceRef, status: Status) -> Entry:
        head = f"{media.KIND_LABEL[ref.kind]} · " if ref.kind in media.KIND_LABEL else ""
        with self._lock:
            job_id = f"job{next(self._ids)}"
            entry = Entry(job_id=job_id, ref=ref, status=status, title=ref.url,
                          subtitle=f"{head}情報を取得しています…")
            self.entries[job_id] = entry
            return entry

    def _find_entry(self, key: str) -> Entry | None:
        """同じ作品がキューにあれば返す。

        状態は問わない。失敗・一部失敗・中止のカードは復元によって
        セッションをまたいで残るので、進行中だけを見ると同じURLを
        コピーするたびに行が増え続ける。
        """
        with self._lock:
            for entry in self.entries.values():
                if entry.ref.key == key:
                    return entry
        return None

    # ============================================================== 実行

    def start(self, job_id: str, reuse_path: str = "") -> None:
        with self._lock:
            entry = self.entries.get(job_id)
            if entry is None or entry.status is not Status.QUEUED or entry.started:
                return
            entry.started = True

        if entry.ref.kind:
            self.manager.set_workers("media", int(self.settings["video_workers"]))
            self.manager.submit(media.MediaJob(job_id, entry.ref, self.settings,
                                               self._on_job_event), lane="media")
        else:
            self.manager.set_workers("gallery", int(self.settings["gallery_workers"]))
            self.manager.submit(GalleryJob(job_id, entry.ref, self.settings,
                                           self._on_job_event, reuse_path=reuse_path),
                                lane="gallery")
        self.push_state()

    def start_all(self) -> None:
        with self._lock:
            targets = [e.job_id for e in self.entries.values()
                       if e.status is Status.QUEUED and not e.started]
        for job_id in targets:
            self.start(job_id)
        if targets:
            self.log("info", f"{len(targets)} 件のダウンロードを開始しました")

    def cancel(self, job_id: str) -> None:
        with self._lock:
            entry = self.entries.get(job_id)
            if entry is None:
                return
            if entry.status is Status.QUEUED and not entry.started:
                entry.status = Status.CANCELED
                entry.detail = "キャンセル"
                self._push(entry)
                self.persist()
                self.push_state()
                return
        if self.manager.cancel(job_id):
            self.log("warn", f"キャンセルを要求しました: {entry.ref.url}")

    def retry(self, job_id: str) -> None:
        with self._lock:
            entry = self.entries.get(job_id)
            if entry is None or entry.status in ACTIVE:
                return
            # 前回が一部失敗なら、その不完全なPDFを置き換える。別名で出すと
            # 壊れた方が綺麗な名前に残り、良い方が「(2)」になってしまう。
            replace = (entry.out_path
                       if entry.status is Status.PARTIAL and entry.out_path
                       and Path(entry.out_path).exists() else "")
            entry.status = Status.QUEUED
            entry.started = False
            entry.done = entry.total = 0
            entry.detail = "待機中"
            entry.message = ""
            entry.out_path = ""
            self._push(entry)

        self.manager.forget(job_id)
        if replace:
            self.log("info", f"再試行: {Path(replace).name} を上書きします")
        self.start(job_id, reuse_path=replace)
        self.persist()

    def retry_all(self) -> None:
        with self._lock:
            targets = [e.job_id for e in self.entries.values() if e.status in _UNFINISHED]
        for job_id in targets:
            self.retry(job_id)
        if targets:
            self.log("info", f"{len(targets)} 件を再試行します")
            self.notify("再試行を開始しました", f"{len(targets)}件", "info")

    def remove(self, job_id: str) -> None:
        with self._lock:
            entry = self.entries.pop(job_id, None)
        if entry is None:
            return
        self.manager.cancel(job_id)
        self.manager.forget(job_id)
        self.emit("removed", {"id": job_id})
        self.persist()
        self.push_state()

    def clear_finished(self) -> None:
        with self._lock:
            targets = [e.job_id for e in self.entries.values()
                       if e.status not in ACTIVE]
            for job_id in targets:
                self.entries.pop(job_id, None)
        for job_id in targets:
            self.manager.forget(job_id)
            self.emit("removed", {"id": job_id})
        self.persist()
        self.push_state()

    def clear_all(self) -> int:
        """進行中も含めてキューを空にする。走っているものは中止してから消す。"""
        with self._lock:
            targets = list(self.entries.keys())
            self.entries.clear()
        for job_id in targets:
            self.manager.cancel(job_id)
            self.manager.forget(job_id)
            self.emit("removed", {"id": job_id})
        if targets:
            self.log("info", f"キューから {len(targets)} 件を削除しました")
        self.persist()
        self.push_state()
        return len(targets)

    def remove_many(self, job_ids: list[str]) -> int:
        removed = 0
        for job_id in job_ids:
            with self._lock:
                entry = self.entries.pop(job_id, None)
            if entry is None:
                continue
            self.manager.cancel(job_id)
            self.manager.forget(job_id)
            self.emit("removed", {"id": job_id})
            removed += 1
        if removed:
            self.persist()
            self.push_state()
        return removed

    # ============================================================== 種別の切り替え

    def set_media_kind(self, kind: str) -> str:
        """これから検出する動画・音声を、どちらとして保存するかを決める。"""
        if kind in media.KIND_LABEL:
            self.settings["media_kind"] = kind
            self.push_state()
        return self.media_kind()

    def set_playlist_all(self, enabled: bool) -> bool:
        """再生リスト・チャンネルのURLを全部取るか、先頭の1本だけにするか。

        判断はジョブが走り出す時点の値で固定するので、走行中のものは変わらない。
        """
        enabled = bool(enabled)
        self.settings["playlist_all"] = enabled
        self.log("info", "再生リスト・チャンネルのURLは全体を取得します" if enabled
                         else "再生リスト・チャンネルのURLは先頭の1本だけ取得します")
        self.push_state()
        return enabled

    def set_entry_kind(self, job_id: str, kind: str) -> bool:
        """キューにあるカードを動画↔音声で切り替える。

        完了済みは触らない。既に保存したファイルと表示が食い違うためで、
        もう一方も欲しいときはトグルを変えて同じURLを追加してもらう
        （動画と音声は別の記録として扱われる）。
        """
        if kind not in media.KIND_LABEL:
            return False

        with self._lock:
            entry = self.entries.get(job_id)
            if entry is None or not entry.ref.kind:
                return False
            if entry.ref.kind == kind:
                return True
            switchable, status = entry.can_switch_kind, entry.status
            twin = self._find_entry(
                SourceRef(entry.ref.site, entry.ref.gid, entry.ref.url, kind).key)

        label = media.KIND_LABEL[kind]
        if not switchable:
            if status in (Status.DONE, Status.SKIPPED, Status.PARTIAL):
                self.notify("切り替えられません",
                            f"保存済みです。{label}でも欲しいときは、"
                            "トグルを切り替えて同じURLを追加してください", "warning")
            else:
                self.notify("切り替えられません",
                            "進行中です。中止してから切り替えてください", "warning")
            return False
        if twin is not None:
            self.emit("focus", {"id": twin.job_id})
            self.notify("すでにあります", f"{label}のカードがキューにあります", "warning")
            return False

        with self._lock:
            entry.ref = SourceRef(entry.ref.site, entry.ref.gid, entry.ref.url, kind)
            entry.status = Status.QUEUED
            entry.started = False
            entry.done = entry.total = 0
            entry.out_path = ""
            entry.message = ""
            entry.detail = "待機中"
            entry.subtitle = _swap_kind_label(entry.subtitle, kind)

        self.manager.forget(job_id)
        self._push(entry)
        self.persist()
        self.log("info", f"{entry.title} を{label}として取得します")
        if self.settings["auto_start"]:
            self.start(job_id)
        self.push_state()
        return True

    # ============================================================== ジョブ通知

    def _on_job_event(self, name: str, payload: dict) -> None:
        job_id = payload.get("id", "")
        if name == "log":
            self.emit("log", payload)
            return

        with self._lock:
            entry = self.entries.get(job_id)
        if entry is None:
            return

        if name == "meta":
            entry.title = payload.get("title") or entry.title
            entry.tags = payload.get("tags") or []
            # 動画側は組み立て済みの説明文を送ってくる。ページ数のような
            # ギャラリー固有の項目は持たないので、無ければ触らない。
            subtitle = payload.get("subtitle")
            if subtitle is None:
                bits = []
                if payload.get("artists"):
                    bits.append("、".join(payload["artists"][:2]))
                if payload.get("series"):
                    bits.append(payload["series"][0])
                bits.append(f"{payload.get('pages', 0)}ページ")
                subtitle = " · ".join(bits)
            entry.subtitle = subtitle
            if payload.get("pages"):
                entry.total = int(payload["pages"])
            self._push(entry)

        elif name == "status":
            entry.status = Status[payload["status"]]
            entry.message = payload.get("message", "")
            if entry.status is Status.FETCHING:
                entry.detail = "情報取得中"
            elif entry.status is Status.DOWNLOADING and not entry.done:
                entry.detail = "取得中"
            elif entry.status is Status.BUILDING:
                entry.detail = "PDF生成中"
            elif entry.status is Status.PROCESSING:
                entry.detail = "変換中"
            self._push(entry)
            self.push_state()

        elif name == "progress":
            entry.done = payload["done"]
            entry.total = payload["total"]
            entry.detail = payload.get("detail") or f"{entry.done} / {entry.total} ページ"
            # 進捗は件数が多いので、行の更新だけ送って全体は送らない
            self.emit("progress", {"id": job_id, "done": entry.done,
                                   "total": entry.total, "detail": entry.detail})

        elif name == "thumbnail":
            entry.thumb = payload["data"]
            self.emit("thumb", {"id": job_id, "data": payload["data"]})

        elif name == "finished":
            self._on_finished(entry, payload)

    def _on_finished(self, entry: Entry, payload: dict) -> None:
        ok = payload["ok"]
        missing = int(payload.get("missing") or 0)
        title = entry.title or entry.ref.url
        label = _result_label(entry.ref.kind)

        if ok:
            entry.out_path = payload["path"]
            got = max(0, entry.total - missing)
            if missing > 0:
                # 欠けたまま履歴に入れると二度と取り直せなくなるので記録しない
                entry.status = Status.PARTIAL
                entry.detail = f"{got}/{entry.total}ページ"
                entry.message = (f"{missing}ページ取得できませんでした"
                                 "（再試行できます）")
                self.log("warn", f"一部欠落: {Path(entry.out_path).name}"
                                 f"（{got}/{entry.total}）")
                self.notify("一部のページを取得できませんでした",
                            f"{title}（{got}/{entry.total}ページ）", "warning")
            else:
                entry.status = Status.DONE
                entry.detail = "完了"
                self.history.add(entry.ref.key, entry.out_path)
                self.log("ok", f"保存しました: {Path(entry.out_path).name}")
                self.notify(f"{label}を保存しました", title, "success")
            if entry.ref.site == "web":
                # 汎用抽出器（Generic）での取得が実際に成功した。次回以降
                # クリップボードでも信頼して拾えるよう、このドメインを学習する。
                self._learn_whitelist(entry.ref.url)
        elif entry.status is not Status.CANCELED:
            entry.status = Status.FAILED
            entry.detail = "失敗"
            entry.message = payload.get("error") or "失敗しました"
            self.notify("ダウンロードに失敗しました", entry.message or title, "error")
            if entry.ref.site == "web":
                # site="web" は yt-dlp の専用抽出器ではなく汎用抽出器（Generic）で
                # 拾ったことの目印。それすら失敗したなら、次回からクリップボードで
                # 同じドメインを拾わないよう学習する（手動貼り付けには影響しない）。
                self._learn_blacklist(entry.ref.url)
        else:
            entry.detail = "キャンセル"
            entry.message = "キャンセルしました"

        entry.started = False
        self.manager.forget(entry.job_id)
        self._push(entry)
        self.persist()
        self.push_state()

    # ============================================================== 保存と復元

    def persist(self) -> None:
        with self._lock:
            records = [{
                "site": e.ref.site,
                "gid": e.ref.gid,
                "url": e.ref.url,
                "kind": e.ref.kind,
                # 表示文言は変わりうるので列挙子の名前で保存する
                "status": e.status.name,
                "title": e.title,
                "subtitle": e.subtitle,
                "done": e.done,
                "total": e.total,
                "out_path": e.out_path,
                "message": e.message,
            } for e in self.entries.values() if e.status in _PERSIST]
        self.queue_store.replace(records)

    def restore_queue(self) -> None:
        records = self.queue_store.records()
        if not records:
            return

        restored = broken = 0
        for rec in records:
            # 1件でも壊れていると起動できない、という状態にはしない
            try:
                restored += self._restore_one(rec)
            except Exception:
                broken += 1

        if broken:
            self.log("warn", f"復元できなかった記録を {broken} 件読み飛ばしました")
        self.persist()          # 読み飛ばした分をファイルからも掃除する
        if not restored:
            return

        # 起動と同時に大きなダウンロードを始めない。終了時に中止を選んだ人の
        # 判断を覆さないため、再開は明示的な操作に任せる。
        self.log("info", f"前回の未完了 {restored} 件を復元しました（自動では再開しません）")
        waiting = sum(1 for e in self.entries.values() if e.status is Status.QUEUED)
        body = (f"{waiting}件が待機中です。「待機中を開始」で再開できます。" if waiting
                else "カードから再試行できます。")
        self.notify(f"未完了の {restored} 件を復元しました", body, "info")

    @staticmethod
    def _as_int(value) -> int:
        """数えられない値は 0 として扱う。

        ページ数が読めないという理由で記録ごと捨てると、肝心のURLまで失う。
        """
        try:
            return max(0, int(value))
        except (TypeError, ValueError):
            return 0

    def _restore_one(self, rec: dict) -> int:
        try:
            status = Status[rec.get("status") or "QUEUED"]
        except KeyError:
            status = Status.QUEUED
        # 中断された作業は「待機中」に戻す。途中まで落とした一時ファイルは
        # 起動時に掃除済みなので、続きからではなく最初からやり直す。
        if status in ACTIVE:
            status = Status.QUEUED

        # 知らない種別は PDF 扱いに倒す。中途半端な値でメディア側へ回すと
        # 復元したカードが必ず失敗する。
        kind = str(rec.get("kind") or "")
        if kind not in media.KIND_LABEL:
            kind = ""
        ref = SourceRef(str(rec["site"]), str(rec.get("gid") or ""), str(rec["url"]), kind)
        if self._find_entry(ref.key) is not None:
            return 0                       # 記録が重複していても行は増やさない

        # カードを作る前に値を確定させる。途中で失敗すると半端な行が残る。
        done = self._as_int(rec.get("done"))
        total = self._as_int(rec.get("total"))
        entry = self._create(ref, status)
        entry.done, entry.total = done, total
        entry.out_path = str(rec.get("out_path") or "")
        entry.message = str(rec.get("message") or "")
        entry.title = str(rec.get("title") or "") or ref.url
        entry.subtitle = str(rec.get("subtitle") or "") or "前回の続き"
        entry.detail = (f"{done}/{total}ページ" if status is Status.PARTIAL and total
                        else status.value)
        self._push(entry)
        return 1

    # ============================================================== 設定

    def set_watch(self, enabled: bool) -> None:
        self.settings["watch_clipboard"] = enabled
        self.clipboard.set_enabled(enabled)
        self.push_state()

    def toggle_watch(self) -> bool:
        enabled = not self.clipboard.is_enabled()
        self.set_watch(enabled)
        return enabled

    def save_settings(self, values: dict) -> None:
        allowed = {k: v for k, v in values.items() if k in self.settings.as_dict()}
        self.settings.update(allowed)
        self.manager.set_workers("gallery", int(self.settings["gallery_workers"]))
        self.manager.set_workers("media", int(self.settings["video_workers"]))
        self.log("info", "設定を保存しました")
        self.push_state()

    # ============================================================== ファイル操作

    def _resolve_path(self, job_id: str) -> str:
        with self._lock:
            entry = self.entries.get(job_id)
        if entry is None:
            return ""
        return entry.out_path or (self.history.path_for(entry.ref.key) or "")

    def _kind_of(self, job_id: str) -> str:
        with self._lock:
            entry = self.entries.get(job_id)
        return entry.ref.kind if entry is not None else ""

    def open_result(self, job_id: str) -> bool:
        path = self._resolve_path(job_id)
        if path and Path(path).exists():
            os.startfile(path)  # noqa: S606
            return True
        self.notify(f"{_result_label(self._kind_of(job_id))}が見つかりません",
                    "移動または削除されたようです。再試行できます。", "warning")
        return False

    def reveal_result(self, job_id: str) -> bool:
        kind = self._kind_of(job_id)
        path = self._resolve_path(job_id)
        if path and Path(path).exists():
            if sys.platform == "win32":
                subprocess.Popen(["explorer", "/select,", os.path.normpath(path)])
            else:
                self.open_output_dir(kind)
            return True
        # ファイルが無くなっていても、種別に応じた保存先までは案内する
        return self.open_output_dir(kind)

    def open_page(self, job_id: str) -> bool:
        with self._lock:
            entry = self.entries.get(job_id)
        if entry is None:
            return False
        import webbrowser
        webbrowser.open(entry.ref.url)
        return True

    def open_output_dir(self, kind: str = "") -> bool:
        """保存先を開く。kind を渡すと動画・音声のフォルダを開く。"""
        target = (media_dir(self.settings, kind) if kind in media.KIND_LABEL
                  else Path(self.settings["output_dir"]))
        try:
            target.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            self.notify("フォルダを開けません", str(exc), "error")
            return False
        os.startfile(str(target))  # noqa: S606
        return True

    # ============================================================== 終了

    def shutdown(self) -> None:
        self.clipboard.stop()
        self.persist()
        self.manager.shutdown()

    def running_count(self) -> int:
        with self._lock:
            return sum(1 for e in self.entries.values() if e.status in ACTIVE)


#: 型ヒント用
EmitFn = Callable[[str, dict], None]
