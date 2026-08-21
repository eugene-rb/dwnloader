"""サイトアダプタのレジストリ。"""

from __future__ import annotations

import re
from urllib.parse import urlsplit

from ..models import SourceRef
from .base import AuthRequired, Context, SiteAdapter, SiteError, Unsupported
from .hitomi import HitomiAdapter
from .momonga import MomongaAdapter
from .pixiv import PixivAdapter

__all__ = [
    "ADAPTERS", "AuthRequired", "Context", "SiteAdapter", "SiteError", "Unsupported",
    "adapter_for", "resolve", "extract_all", "find_urls", "is_url",
]

ADAPTERS: dict[str, SiteAdapter] = {
    a.name: a for a in (HitomiAdapter(), MomongaAdapter(), PixivAdapter())
}

#: テキストからURLらしき部分を切り出す
_URL_IN_TEXT = re.compile(r"https?://[^\s<>\"'\]\)]+", re.I)


def find_urls(text: str) -> list[str]:
    """テキストからURLらしき部分を全部切り出す（重複除去なし）。

    ギャラリー系サイトの判定にも、yt-dlp向けの判定にも同じ抽出結果を使う。
    ここは「候補を切り出す」だけの役割で、それが本当にURLと呼べるかは
    is_url() が別途判定する（hitomi.la/... のようなスキーム無しの貼り付けも
    ギャラリー側の正規表現は素で拾えるので、ここでは弾かない）。
    """
    text = text or ""
    found = _URL_IN_TEXT.findall(text)
    if not found and text.strip():
        found = [text.strip()]
    return found


def is_url(text: str) -> bool:
    """文字列が実際にURLと呼べるかを判定する（find_urls とは独立した判定）。

    scheme が http/https であることに加えて、ホスト名がドット区切りの
    ドメインらしい形（または localhost）を持つことを求める。空白を含む
    ただの文章の断片や、スキームの無い断片が誤ってURL扱いされるのを防ぐ。
    yt-dlp へ回す前の最終ゲートとして使う想定（ギャラリー側の正規表現照合は
    スキーム無しの貼り付けも受け付けるため、ここでは絞り込みすぎない）。
    """
    text = (text or "").strip()
    if not text or any(ch.isspace() for ch in text):
        return False
    try:
        parts = urlsplit(text)
    except ValueError:
        return False
    if parts.scheme.lower() not in ("http", "https"):
        return False
    host = (parts.hostname or "").lower()
    if not host:
        return False
    return host == "localhost" or "." in host


def adapter_for(site: str) -> SiteAdapter:
    try:
        return ADAPTERS[site]
    except KeyError:
        raise SiteError(f"未知のサイトです: {site}") from None


def resolve(url: str) -> SourceRef | None:
    """1本のURLを SourceRef に解決する。対応外なら None。"""
    url = (url or "").strip()
    if not url:
        return None
    for adapter in ADAPTERS.values():
        ref = adapter.match(url)
        if ref is not None:
            return ref
    return None


def extract_all(text: str) -> list[SourceRef]:
    """任意のテキストから対応URLを全部拾う（重複は除く）。"""
    refs: list[SourceRef] = []
    seen: set[str] = set()
    for cand in find_urls(text):
        ref = resolve(cand)
        if ref is not None and ref.key not in seen:
            seen.add(ref.key)
            refs.append(ref)
    return refs
