"""hitomi.la アダプタ。

画像URLは ltn ホストが配る gg.js のパラメータから組み立てる。この値は
定期的に更新されるため、TTL 付きでキャッシュし、404 を踏んだら再取得する。
"""

from __future__ import annotations

import json
import re
import threading
import time

from ..models import GalleryMeta, ImageRef, SourceRef
from ..net import get_with_retry
from ..util import parse_datetime
from .base import Context, SiteAdapter, SiteError

DOMAIN = "gold-usergeneratedcontent.net"
LTN = f"https://ltn.{DOMAIN}"

_URL_RE = re.compile(r"hitomi\.la/(?:[a-z]+/)?[^/\s?#]*?(\d+)\.html", re.I)

_GG_B = re.compile(r"b\s*:\s*['\"](.*?)['\"]")
_GG_DEFAULT = re.compile(r"var\s+o\s*=\s*(\d+)")
_GG_CASE = re.compile(r"case\s+(\d+)\s*:")
_GG_SET = re.compile(r"o\s*=\s*(\d+)\s*;\s*break")

_GG_TTL = 600.0  # 10分


class _GG:
    """gg.js のパラメータ（b と g→サブドメイン番号の対応表）のキャッシュ。"""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._b = ""
        self._map: dict[int, int] = {}
        self._default = 0
        self._at = 0.0

    def get(self, ctx: Context, force: bool = False) -> tuple[str, dict[int, int], int]:
        with self._lock:
            fresh = self._b and (time.monotonic() - self._at) < _GG_TTL
            if force or not fresh:
                self._load(ctx)
            return self._b, self._map, self._default

    def _load(self, ctx: Context) -> None:
        resp = get_with_retry(
            ctx.session, f"{LTN}/gg.js",
            headers={"Referer": "https://hitomi.la/"},
            timeout=ctx.timeout, retries=ctx.retries, cancel=ctx.cancel,
        )
        if resp.status_code != 200:
            raise SiteError(f"gg.js を取得できません (HTTP {resp.status_code})")
        text = resp.text

        m = _GG_B.search(text)
        if not m:
            raise SiteError("gg.js の形式が変わっています（b が見つかりません）")
        b = m.group(1)

        md = _GG_DEFAULT.search(text)
        default = int(md.group(1)) if md else 0

        table: dict[int, int] = {}
        pending: list[int] = []
        for line in text.splitlines():
            line = line.strip()
            mc = _GG_CASE.match(line)
            if mc:
                pending.append(int(mc.group(1)))
                continue
            ms = _GG_SET.match(line)
            if ms and pending:
                value = int(ms.group(1))
                for g in pending:
                    table[g] = value
                pending = []

        if not table:
            raise SiteError("gg.js の形式が変わっています（対応表が空です）")

        self._b, self._map, self._default, self._at = b, table, default, time.monotonic()


_gg = _GG()


def _hash_bucket(image_hash: str) -> int:
    """ハッシュ末尾3桁から配信バケット番号を求める。"""
    return int(image_hash[-1] + image_hash[-3:-1], 16)


def _join(values, key: str) -> list[str]:
    out = []
    for item in values or []:
        if isinstance(item, dict):
            v = item.get(key) or ""
        else:
            v = str(item)
        v = v.strip()
        if v:
            out.append(v)
    return out


class HitomiAdapter(SiteAdapter):
    name = "hitomi"
    label = "hitomi.la"
    max_image_workers = 8
    min_interval = 0.0

    def match(self, url: str) -> SourceRef | None:
        m = _URL_RE.search(url)
        if not m:
            return None
        gid = m.group(1)
        return SourceRef(self.name, gid, f"https://hitomi.la/reader/{gid}.html")

    def fetch(self, ref: SourceRef, ctx: Context) -> GalleryMeta:
        resp = get_with_retry(
            ctx.session, f"{LTN}/galleries/{ref.gid}.js",
            headers={"Referer": "https://hitomi.la/"},
            timeout=ctx.timeout, retries=ctx.retries,
            limiter=self.limiter, cancel=ctx.cancel,
        )
        if resp.status_code == 404:
            raise SiteError(f"作品 {ref.gid} は存在しません（削除された可能性があります）")
        if resp.status_code != 200:
            raise SiteError(f"作品情報を取得できません (HTTP {resp.status_code})")

        # `var galleryinfo = {...}` の形で返ってくる
        _, _, body = resp.text.partition("=")
        try:
            info = json.loads(body.strip().rstrip(";"))
        except ValueError as exc:
            raise SiteError(f"作品情報を解釈できません: {exc}") from exc

        files = info.get("files") or []
        if not files:
            raise SiteError("ページが1枚もありません")

        title = (info.get("japanese_title") or info.get("title") or f"hitomi-{ref.gid}").strip()
        meta = GalleryMeta(
            site=self.name,
            gid=str(info.get("id") or ref.gid),
            title=title,
            page_url=f"https://hitomi.la/reader/{ref.gid}.html",
            artists=_join(info.get("artists"), "artist"),
            groups=_join(info.get("groups"), "group"),
            series=_join(info.get("parodys"), "parody"),
            characters=_join(info.get("characters"), "character"),
            tags=_join(info.get("tags"), "tag"),
            language=(info.get("language_localname") or info.get("language") or ""),
            kind=info.get("type") or "",
            published=parse_datetime(info.get("date") or info.get("datepublished")),
        )
        meta.raw = {"files": files}
        meta.images = self._build_images(files, meta.gid, ctx, force_gg=False)
        return meta

    def refresh(self, meta: GalleryMeta, ctx: Context) -> bool:
        """gg.js を強制再取得して画像URLを作り直す。"""
        files = meta.raw.get("files")
        if not files:
            return False
        try:
            images = self._build_images(files, meta.gid, ctx, force_gg=True)
        except SiteError:
            return False
        meta.images = images
        return True

    def _build_images(self, files, gid: str, ctx: Context, force_gg: bool) -> list[ImageRef]:
        b, table, default = _gg.get(ctx, force=force_gg)
        prefer = ctx.settings["prefer_format"]
        order = ("avif", "webp") if prefer == "avif" else ("webp", "avif")
        referer = f"https://hitomi.la/reader/{gid}.html"

        images: list[ImageRef] = []
        for i, f in enumerate(files, start=1):
            image_hash = f.get("hash") or ""
            if len(image_hash) < 3:
                continue
            # 形式は作品単位ではなくページ単位で決まる
            available = [fmt for fmt in order if f.get(f"has{fmt}")]
            if not available:
                continue

            urls = [self._url(image_hash, fmt, b, table, default) for fmt in available]
            images.append(ImageRef(
                index=i,
                url=urls[0],
                fmt=available[0],
                headers={"Referer": referer},
                fallbacks=urls[1:],
            ))

        if not images:
            raise SiteError("対応する形式の画像がありません")
        return images

    @staticmethod
    def _url(image_hash: str, fmt: str, b: str, table: dict[int, int], default: int) -> str:
        bucket = _hash_bucket(image_hash)
        sub = ("a" if fmt == "avif" else "w") + str(1 + table.get(bucket, default))
        # gg.s(hash): 末尾3桁を16進で読んで10進表記にしたもの
        shard = str(int(image_hash[-1] + image_hash[-3:-1], 16))
        return f"https://{sub}.{DOMAIN}/{b}{shard}/{image_hash}.{fmt}"
