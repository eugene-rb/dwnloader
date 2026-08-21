"""サイトアダプタの共通インターフェース。"""

from __future__ import annotations

from dataclasses import dataclass

import requests

from ..config import Settings
from ..models import GalleryMeta, SourceRef
from ..net import RateLimiter


class SiteError(Exception):
    """このサイトから取得できなかった。"""


class AuthRequired(SiteError):
    """ログイン情報（Cookie）が必要。"""


class Unsupported(SiteError):
    """PDF 化の対象外（うごイラ・動画など）。"""


@dataclass
class Context:
    """アダプタに渡す実行環境。"""

    session: requests.Session
    settings: Settings
    cancel: object = None          # threading.Event 互換

    @property
    def timeout(self) -> float:
        return float(self.settings["timeout"])

    @property
    def retries(self) -> int:
        return int(self.settings["retries"])


class SiteAdapter:
    """URL の判定と作品情報の取得を担う。"""

    name: str = ""
    label: str = ""
    #: 画像取得の同時実行数の上限（設定値をこの値で頭打ちにする）
    max_image_workers: int = 8
    #: リクエスト間隔の下限（秒）
    min_interval: float = 0.0

    def __init__(self) -> None:
        self.limiter = RateLimiter(self.min_interval)

    def match(self, url: str) -> SourceRef | None:
        """URL が自サイトのものなら SourceRef を返す。"""
        raise NotImplementedError

    def fetch(self, ref: SourceRef, ctx: Context) -> GalleryMeta:
        """メタ情報と全ページの取得先を組み立てる。"""
        raise NotImplementedError

    def refresh(self, meta: GalleryMeta, ctx: Context) -> bool:
        """画像URLが失効していた場合に作り直す。作り直したら True。

        hitomi のように URL の一部が定期的に更新されるサイト向け。
        既定では何もしない。
        """
        return False
