"""pixiv アダプタ。

公開作品は未ログインでも ajax API から取得できる。R-18 作品は
PHPSESSID クッキーが必要で、うごイラ（illustType=2）は PDF 化の対象外。
"""

from __future__ import annotations

import re

from ..models import GalleryMeta, ImageRef, SourceRef
from ..net import get_with_retry
from ..util import parse_datetime
from .base import AuthRequired, Context, SiteAdapter, SiteError, Unsupported

_URL_RE = re.compile(r"pixiv\.net/(?:[a-z]{2}/)?artworks/(\d+)", re.I)
_LEGACY_RE = re.compile(r"pixiv\.net/member_illust\.php\?[^\s]*illust_id=(\d+)", re.I)

_TYPE_ILLUST = 0
_TYPE_MANGA = 1
_TYPE_UGOIRA = 2


class PixivAdapter(SiteAdapter):
    name = "pixiv"
    label = "pixiv.net"
    # pixiv は他の2サイトより明確に絞られるので控えめに
    max_image_workers = 2
    min_interval = 1.0

    def match(self, url: str) -> SourceRef | None:
        m = _URL_RE.search(url) or _LEGACY_RE.search(url)
        if not m:
            return None
        pid = m.group(1)
        return SourceRef(self.name, pid, f"https://www.pixiv.net/artworks/{pid}")

    def _api(self, path: str, ref: SourceRef, ctx: Context) -> dict:
        headers = {
            "Referer": ref.url,
            "Accept": "application/json",
            "X-Requested-With": "XMLHttpRequest",
        }
        cookie = (ctx.settings["pixiv_cookie"] or "").strip()
        if cookie:
            # 生の PHPSESSID 値でも `PHPSESSID=...` 形式でも受け付ける
            headers["Cookie"] = cookie if "=" in cookie else f"PHPSESSID={cookie}"

        resp = get_with_retry(
            ctx.session, f"https://www.pixiv.net/ajax/{path}",
            headers=headers, timeout=ctx.timeout, retries=ctx.retries,
            limiter=self.limiter, cancel=ctx.cancel,
        )
        if resp.status_code in (401, 403):
            raise AuthRequired("この作品にはログインが必要です。設定で pixiv の PHPSESSID を登録してください。")
        if resp.status_code == 404:
            raise SiteError("作品が見つかりません（非公開・削除済みの可能性があります）")
        if resp.status_code != 200:
            raise SiteError(f"pixiv API エラー (HTTP {resp.status_code})")

        try:
            data = resp.json()
        except ValueError as exc:
            raise SiteError(f"pixiv API の応答を解釈できません: {exc}") from exc

        if data.get("error"):
            message = (data.get("message") or "").strip()
            if not cookie:
                raise AuthRequired(
                    f"取得できませんでした（{message or 'ログインが必要な作品の可能性があります'}）。"
                    "設定で pixiv の PHPSESSID を登録してください。"
                )
            raise SiteError(message or "pixiv API がエラーを返しました")
        return data.get("body") or {}

    def fetch(self, ref: SourceRef, ctx: Context) -> GalleryMeta:
        info = self._api(f"illust/{ref.gid}", ref, ctx)

        illust_type = info.get("illustType")
        if illust_type == _TYPE_UGOIRA:
            raise Unsupported("うごイラはPDF化できないためスキップします")

        pages = self._api(f"illust/{ref.gid}/pages", ref, ctx)
        if not isinstance(pages, list) or not pages:
            raise SiteError("ページ情報を取得できません")

        images: list[ImageRef] = []
        for i, page in enumerate(pages, start=1):
            urls = page.get("urls") or {}
            original = urls.get("original")
            if not original:
                continue
            ext = original.rsplit(".", 1)[-1].lower()
            fallbacks = [u for u in (urls.get("regular"), urls.get("small")) if u]
            images.append(ImageRef(
                index=i,
                url=original,
                fmt=ext if ext in {"jpg", "jpeg", "png", "webp", "avif"} else "jpg",
                # 画像配信は Referer を見るので必ず付ける
                headers={"Referer": "https://www.pixiv.net/"},
                fallbacks=fallbacks,
            ))

        if not images:
            raise SiteError("ダウンロードできる画像がありません")

        tags = []
        for tag in ((info.get("tags") or {}).get("tags") or []):
            name = tag.get("tag") or ""
            if name:
                tags.append(name)

        author = (info.get("userName") or "").strip()
        return GalleryMeta(
            site=self.name,
            gid=str(info.get("illustId") or ref.gid),
            title=(info.get("illustTitle") or info.get("title") or f"pixiv-{ref.gid}").strip(),
            page_url=ref.url,
            images=images,
            artists=[author] if author else [],
            tags=tags[:20],
            language="",
            kind="manga" if illust_type == _TYPE_MANGA else "illust",
            published=parse_datetime(info.get("createDate") or info.get("uploadDate")),
        )
