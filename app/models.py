"""サイト非依存のデータモデル。"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum


class Status(str, Enum):
    QUEUED = "待機中"
    FETCHING = "情報取得中"
    DOWNLOADING = "取得中"        # 画像・動画・音声のいずれも通る
    BUILDING = "PDF生成中"
    PROCESSING = "処理中"        # yt-dlp の後処理（結合・音声抽出/変換）
    DONE = "完了"
    PARTIAL = "一部失敗"
    FAILED = "失敗"
    CANCELED = "キャンセル"
    SKIPPED = "取得済み"


#: 進行中とみなすステータス
ACTIVE = {Status.QUEUED, Status.FETCHING, Status.DOWNLOADING, Status.BUILDING,
          Status.PROCESSING}


@dataclass
class SourceRef:
    """URL から解決した「どのサイトのどの作品か」。キューの同一性判定に使う。"""

    site: str          # "hitomi" / "momonga" / "pixiv" / yt-dlp の抽出器名
    gid: str           # サイト内での識別子
    url: str           # 正規化した作品ページURL
    #: "" = ギャラリー（PDF化）/ "video" / "audio"。
    #: 同じ動画を「動画として」と「音声として」別々に持てるよう key に含める。
    kind: str = ""

    @property
    def key(self) -> str:
        if self.kind:
            return f"{self.site}:{self.kind}:{self.gid}"
        return f"{self.site}:{self.gid}"


@dataclass
class ImageRef:
    """1ページ分の画像の取得先。"""

    index: int                      # 1 始まりのページ番号
    url: str
    fmt: str                        # "avif" / "webp" / "jpg" / "png"
    headers: dict = field(default_factory=dict)
    fallbacks: list[str] = field(default_factory=list)


@dataclass
class GalleryMeta:
    """作品1件のメタ情報と全ページの取得先。"""

    site: str
    gid: str
    title: str
    page_url: str
    images: list[ImageRef] = field(default_factory=list)
    artists: list[str] = field(default_factory=list)
    groups: list[str] = field(default_factory=list)
    series: list[str] = field(default_factory=list)
    characters: list[str] = field(default_factory=list)
    tags: list[str] = field(default_factory=list)
    language: str = ""
    kind: str = ""                  # doujinshi / manga / illust など
    #: 作品の公開日。PDF の作成日時に使う。取れないサイトもある。
    published: datetime | None = None
    #: アダプタが refresh() で画像URLを組み直すために保持する生データ
    raw: dict = field(default_factory=dict)

    @property
    def key(self) -> str:
        return f"{self.site}:{self.gid}"
