"""画像バイト列から PDF を組み立てる。

img2pdf は JPEG/PNG をそのまま埋め込めるが avif/webp は受け付けず、
アルファ付き画像も拒否する。そのため一度 Pillow で復号して JPEG に
そろえてから渡す。JPEG 化は一時ディレクトリ上で行い、常時メモリに
全ページを載せないようにする。
"""

from __future__ import annotations

import io
import re
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from PIL import Image

try:
    import img2pdf
except ImportError:  # pragma: no cover
    img2pdf = None

# Pillow の巨大画像ガードは同人誌の見開きで引っかかることがあるので緩める
Image.MAX_IMAGE_PIXELS = 512_000_000


class PdfError(Exception):
    pass


def normalize_to_jpeg(data: bytes, dest: Path, quality: int = 90) -> Path:
    """任意形式の画像を RGB JPEG にして dest へ書き出す。"""
    with Image.open(io.BytesIO(data)) as im:
        im.load()

        if im.mode in ("RGBA", "LA") or (im.mode == "P" and "transparency" in im.info):
            rgba = im.convert("RGBA")
            canvas = Image.new("RGB", rgba.size, (255, 255, 255))
            canvas.paste(rgba, mask=rgba.split()[-1])
            out = canvas
        elif im.mode != "RGB":
            out = im.convert("RGB")
        else:
            out = im

        out.save(dest, format="JPEG", quality=quality, optimize=True,
                 subsampling=0 if quality >= 95 else 2)
        if out is not im:
            out.close()
    return dest


def make_thumbnail(jpeg_path: Path, width: int = 220) -> bytes:
    """表紙用の小さな JPEG を作る。1ページ目から作るので追加の通信は要らない。"""
    with Image.open(jpeg_path) as im:
        im.load()
        ratio = width / max(1, im.width)
        size = (width, max(1, round(im.height * ratio)))
        thumb = im.convert("RGB").resize(size, Image.Resampling.LANCZOS)
        buf = io.BytesIO()
        thumb.save(buf, format="JPEG", quality=82, optimize=True)
        thumb.close()
        return buf.getvalue()


def _ascii_only(text: str, fallback: str = "img2pdf") -> str:
    """ASCII に落とす。

    img2pdf は producer を XMP パケットへ ascii で書き込むため、非ASCII が
    1文字でも混ざると例外になる（他の項目は UTF-16 で入るので問題ない）。
    """
    try:
        text.encode("ascii")
        return text
    except UnicodeEncodeError:
        stripped = text.encode("ascii", "ignore").decode("ascii")
        return re.sub(r"\s{2,}", " ", stripped).strip() or fallback


@dataclass
class PdfMeta:
    """PDF の文書情報。ビューアやエクスプローラーの検索に効く。"""

    title: str = ""
    author: str = ""
    subject: str = ""
    keywords: list[str] = field(default_factory=list)
    creator: str = ""
    created: datetime | None = None

    def img2pdf_kwargs(self) -> dict:
        kwargs: dict = {}
        if self.title:
            kwargs["title"] = self.title
        if self.author:
            kwargs["author"] = self.author
        if self.subject:
            kwargs["subject"] = self.subject
        if self.keywords:
            kwargs["keywords"] = list(self.keywords)
        if self.creator:
            kwargs["creator"] = self.creator
            kwargs["producer"] = _ascii_only(self.creator)
        if self.created is not None:
            kwargs["creationdate"] = self.created
        return kwargs

    def pillow_kwargs(self) -> dict:
        kwargs: dict = {}
        if self.title:
            kwargs["title"] = self.title
        if self.author:
            kwargs["author"] = self.author
        if self.subject:
            kwargs["subject"] = self.subject
        if self.keywords:
            kwargs["keywords"] = ", ".join(self.keywords)
        if self.creator:
            kwargs["creator"] = self.creator
            kwargs["producer"] = self.creator
        return kwargs


def build_pdf(jpeg_paths: list[Path], out_path: Path, meta: PdfMeta | None = None,
              warn=None) -> None:
    """JPEG の並びを1つの PDF にまとめる。

    warn を渡すと、img2pdf が失敗して Pillow に切り替わった際に理由を通知する。
    ここを黙って握りつぶすと、劣化やメタデータ欠落に気づけなくなる。
    """
    if not jpeg_paths:
        raise PdfError("PDF に入れる画像がありません")

    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.with_suffix(out_path.suffix + ".part")
    paths = [str(p) for p in jpeg_paths]

    try:
        if img2pdf is not None:
            # JPEG を再エンコードせずそのまま埋め込むので速く、劣化もしない
            with open(tmp, "wb") as fh:
                fh.write(img2pdf.convert(paths, **(meta.img2pdf_kwargs() if meta else {})))
        else:
            _build_pdf_pillow(jpeg_paths, tmp, meta)
        tmp.replace(out_path)
    except Exception as exc:
        tmp.unlink(missing_ok=True)
        if img2pdf is not None:
            # img2pdf が受け付けない画像やメタデータが混ざっていた場合の保険
            try:
                _build_pdf_pillow(jpeg_paths, tmp, meta)
                tmp.replace(out_path)
                if warn is not None:
                    warn(f"PDF生成を代替方式に切り替えました（{type(exc).__name__}: {exc}）")
                return
            except Exception:
                tmp.unlink(missing_ok=True)
        raise PdfError(f"PDF の生成に失敗しました: {exc}") from exc


def _build_pdf_pillow(jpeg_paths: list[Path], out_path: Path,
                      meta: PdfMeta | None = None) -> None:
    first = Image.open(jpeg_paths[0])
    first.load()
    rest = []
    try:
        for p in jpeg_paths[1:]:
            im = Image.open(p)
            im.load()
            rest.append(im)
        first.save(out_path, format="PDF", save_all=True, append_images=rest,
                   resolution=96.0, **(meta.pillow_kwargs() if meta else {}))
    finally:
        first.close()
        for im in rest:
            im.close()
