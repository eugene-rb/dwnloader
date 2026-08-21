"""ファイル名生成まわりのユーティリティ。"""

from __future__ import annotations

import os
import re
import unicodedata
from datetime import datetime, timedelta, timezone
from pathlib import Path

# 時差の書き方はサイトごとに揺れる（"-05" / "+09:00" / "Z" / 無し）。
# 落とすと PDF の作成日時が最大14時間ずれるので、あるものは必ず拾う。
_DATETIME_HEAD = re.compile(
    r"(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?"
    r"\s*(?:(Z)|([+-])(\d{2}):?(\d{2})?)?"
)
_DATE_ONLY = re.compile(r"(\d{4})-(\d{2})-(\d{2})")


def _tzinfo(zulu, sign, hours, minutes) -> timezone | None:
    if zulu:
        return timezone.utc
    if not sign:
        # 時差が書かれていなければ素朴な日時のまま返す（現地時間として扱われる）
        return None
    offset = timedelta(hours=int(hours), minutes=int(minutes or 0))
    return timezone(-offset if sign == "-" else offset)


def parse_datetime(value) -> datetime | None:
    """サイトが返す日付文字列を datetime にする。解釈できなければ None。"""
    if not isinstance(value, str) or not value.strip():
        return None

    m = _DATETIME_HEAD.search(value)
    if m:
        y, mo, d, h, mi, s, zulu, sign, oh, om = m.groups()
        try:
            return datetime(int(y), int(mo), int(d), int(h), int(mi), int(s or 0),
                            tzinfo=_tzinfo(zulu, sign, oh, om))
        except ValueError:
            return None

    m = _DATE_ONLY.search(value)
    if m:
        try:
            return datetime(int(m.group(1)), int(m.group(2)), int(m.group(3)))
        except ValueError:
            return None
    return None

# Windows で使えない文字。全角に置換して情報を落とさないようにする。
_TRANSLATE = str.maketrans({
    "<": "＜", ">": "＞", ":": "：", '"': "”",
    "/": "／", "\\": "＼", "|": "｜", "?": "？", "*": "＊",
})

_CONTROL = re.compile(r"[\x00-\x1f\x7f]")

# CON, PRN などの予約デバイス名（拡張子を付けても使えない）
_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}


def sanitize_filename(name: str, max_len: int = 120, fallback: str = "untitled") -> str:
    """Windows のファイル名として安全な文字列にする。"""
    name = unicodedata.normalize("NFC", name or "")
    name = _CONTROL.sub("", name).translate(_TRANSLATE)
    name = re.sub(r"\s+", " ", name).strip()
    # 末尾のドットと空白は Windows が黙って削るので先に落とす
    name = name.rstrip(" .")

    if len(name) > max_len:
        name = name[:max_len].rstrip(" .")

    if not name or name.upper() in _RESERVED:
        return fallback
    return name


def unique_path(path: Path) -> Path:
    """同名ファイルがあれば ` (2)`, ` (3)` … を付けて衝突を避ける。"""
    if not path.exists():
        return path
    stem, suffix, parent = path.stem, path.suffix, path.parent
    for i in range(2, 1000):
        cand = parent / f"{stem} ({i}){suffix}"
        if not cand.exists():
            return cand
    return parent / f"{stem} ({os.getpid()}){suffix}"


def format_filename(template: str, meta, max_len: int = 120) -> str:
    """テンプレートからPDFのファイル名（拡張子なし）を組み立てる。"""
    values = {
        "title": meta.title,
        "id": meta.gid,
        "site": meta.site,
        "artist": "、".join(meta.artists) or "unknown",
        "group": "、".join(meta.groups) or "",
        "series": "、".join(meta.series) or "",
        "pages": str(len(meta.images)),
        "lang": meta.language or "",
    }
    try:
        name = template.format(**values)
    except (KeyError, IndexError, ValueError):
        name = f"{meta.title} ({meta.site}-{meta.gid})"

    # 空トークンが残した空の括弧と余分な区切りを掃除する
    name = re.sub(r"[\[\(（【]\s*[\]\)）】]", "", name)
    name = re.sub(r"\s{2,}", " ", name).strip(" -_")
    return sanitize_filename(name, max_len=max_len, fallback=f"{meta.site}-{meta.gid}")


def human_size(num: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if abs(num) < 1024.0:
            return f"{num:.0f} {unit}" if unit == "B" else f"{num:.1f} {unit}"
        num /= 1024.0
    return f"{num:.1f} TB"
