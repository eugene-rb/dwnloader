"""momon-ga.com アダプタ。

作品ページの img タグに全ページの直リンクが並んでいる。ただし関連作品の
サムネイルも同じ形で混ざるため、作品IDで絞り込んでから連番を組み立てる。
"""

from __future__ import annotations

import collections
import html as html_mod
import re

from ..models import GalleryMeta, ImageRef, SourceRef
from ..net import get_with_retry
from .base import Context, SiteAdapter, SiteError

_URL_RE = re.compile(r"momon-ga\.com/([a-z]+)/(mo[\w\-]+)", re.I)
_IMG_RE = re.compile(
    r'<img[^>]+?(?:data-src|data-original|src)=[\'"]'
    r'(https?://[^\'"]+?/galleries/(\d+)/(\d+)\.(\w+))[\'"]',
    re.I,
)
_TITLE_RE = re.compile(r"<h1[^>]*>(.*?)</h1>", re.S)
_TAG_LINK_RE = r'<a[^>]+href=[\'"]https?://momon-ga\.com/{}/[^\'"]+[\'"][^>]*>(.*?)</a>'

#: 末尾のページが lazy-load で抜けている場合に何枚まで追試するか
_PROBE_AHEAD = 3


def _text(fragment: str) -> str:
    return html_mod.unescape(re.sub(r"<[^>]+>", "", fragment)).strip()


def _links(page: str, section: str, limit: int = 12) -> list[str]:
    found = re.findall(_TAG_LINK_RE.format(section), page, re.S)
    out, seen = [], set()
    for f in found:
        v = _text(f)
        if v and v not in seen:
            seen.add(v)
            out.append(v)
        if len(out) >= limit:
            break
    return out


class MomongaAdapter(SiteAdapter):
    name = "momonga"
    label = "momon-ga.com"
    max_image_workers = 5
    min_interval = 0.2

    def match(self, url: str) -> SourceRef | None:
        m = _URL_RE.search(url)
        if not m:
            return None
        section, slug = m.group(1).lower(), m.group(2)
        if section in {"tag", "parody", "group", "cartoonist", "character"}:
            return None
        return SourceRef(self.name, slug, f"https://momon-ga.com/{section}/{slug}/")

    def fetch(self, ref: SourceRef, ctx: Context) -> GalleryMeta:
        resp = get_with_retry(
            ctx.session, ref.url,
            headers={"Referer": "https://momon-ga.com/"},
            timeout=ctx.timeout, retries=ctx.retries,
            limiter=self.limiter, cancel=ctx.cancel,
        )
        if resp.status_code == 404:
            raise SiteError(f"作品ページが見つかりません: {ref.url}")
        if resp.status_code != 200:
            raise SiteError(f"作品ページを取得できません (HTTP {resp.status_code})")

        resp.encoding = resp.apparent_encoding or "utf-8"
        page = resp.text

        matches = _IMG_RE.findall(page)
        if not matches:
            raise SiteError("ページ画像が見つかりません（サイト構造が変わった可能性があります）")

        slug_number = re.search(r"mo(\d+)", ref.gid)
        gallery_id = self._pick_gallery_id(matches, slug_number.group(1) if slug_number else None)

        # 同じページ番号が複数回出るので番号で一意化する
        by_index: dict[int, tuple[str, str]] = {}
        for url, gid, num, ext in matches:
            if gid != gallery_id:
                continue
            n = int(num)
            by_index.setdefault(n, (url, ext.lower()))

        if not by_index:
            raise SiteError("ページ画像が見つかりません")

        images = [
            ImageRef(index=n, url=by_index[n][0], fmt=by_index[n][1],
                     headers={"Referer": ref.url})
            for n in sorted(by_index)
        ]
        images = self._probe_tail(images, ctx, ref.url)

        title = ""
        mt = _TITLE_RE.search(page)
        if mt:
            title = _text(mt.group(1))

        return GalleryMeta(
            site=self.name,
            gid=gallery_id,
            title=title or f"momonga-{gallery_id}",
            page_url=ref.url,
            images=images,
            artists=_links(page, "cartoonist"),
            groups=_links(page, "group"),
            series=_links(page, "parody", limit=4),
            characters=_links(page, "character", limit=8),
            tags=_links(page, "tag", limit=20),
            language="日本語",
            kind="doujinshi" if "/fanzine/" in ref.url else "magazine",
        )

    @staticmethod
    def _pick_gallery_id(matches, slug_number: str | None) -> str:
        """関連作品のサムネを排除して本編の作品IDを決める。"""
        counts = collections.Counter(gid for _, gid, _, _ in matches)
        if slug_number and slug_number in counts:
            return slug_number
        return counts.most_common(1)[0][0]

    def _probe_tail(self, images: list[ImageRef], ctx: Context, referer: str) -> list[ImageRef]:
        """末尾が lazy-load で欠けている場合に備えて次の番号を試す。"""
        if not images:
            return images
        last = images[-1]
        # 1..N の連番になっていなければ推測が危ういので触らない
        if [im.index for im in images] != list(range(1, len(images) + 1)):
            return images

        n = last.index
        for _ in range(_PROBE_AHEAD):
            nxt = n + 1
            url = re.sub(r"/\d+\.(\w+)$", rf"/{nxt}.\1", last.url)
            try:
                resp = ctx.session.head(
                    url, headers={"Referer": referer}, timeout=ctx.timeout, allow_redirects=True
                )
            except Exception:
                break
            if resp.status_code != 200 or "image" not in (resp.headers.get("content-type") or ""):
                break
            images.append(ImageRef(index=nxt, url=url, fmt=last.fmt,
                                   headers={"Referer": referer}))
            n = nxt
            last = images[-1]
        return images
