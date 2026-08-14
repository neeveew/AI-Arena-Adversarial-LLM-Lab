"""Generate the checked-in AI Arena - Lite raster asset family.

The supplied emblem is the authoritative artwork. Derivatives are rendered in
premultiplied-alpha space so transparent matte colours cannot bleed into the
small Windows icon frames.
"""

from __future__ import annotations

import argparse
from hashlib import sha256
from io import BytesIO
from pathlib import Path
from struct import pack

from PIL import Image, PngImagePlugin


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
ASSET_ROOT = REPOSITORY_ROOT / "src" / "AIArena.Wpf" / "Assets"
DOCS_ASSET_ROOT = REPOSITORY_ROOT / "docs" / "assets"
SOURCE = ASSET_ROOT / "ai-arena-emblem-lite-green.png"
EXPECTED_SOURCE_SHA256 = (
    "805b370ff6a6479c461a9d716486b0a56a2c23ae29790e74853159cfaa9bf525"
)
ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)
ICON_VISIBLE_SPANS = {
    16: 14,
    24: 22,
    32: 30,
    48: 44,
    64: 58,
    128: 116,
    256: 224,
}


def alpha_safe_resize(image: Image.Image, size: tuple[int, int]) -> Image.Image:
    """Resize with Lanczos while RGB is premultiplied by alpha."""

    rendered = (
        image.convert("RGBa")
        .resize(size, Image.Resampling.LANCZOS)
        .convert("RGBA")
    )
    return neutralize_semtransparent_magenta(rendered)


def neutralize_semtransparent_magenta(image: Image.Image) -> Image.Image:
    """Remove the extraction matte from antialiased output pixels only."""

    clean = image.copy()
    pixels = clean.load()
    for y in range(clean.height):
        for x in range(clean.width):
            red, green, blue, opacity = pixels[x, y]
            if 0 < opacity < 255 and red * 4 > green * 5 and blue * 4 > green * 5:
                pixels[x, y] = (min(red, green), green, min(blue, green), opacity)
    return clean


def normalized_visible_art(image: Image.Image) -> Image.Image:
    rgba = image.convert("RGBA")
    alpha = rgba.getchannel("A")
    bounds = alpha.getbbox()
    if bounds is None:
        raise ValueError("The authoritative emblem has no visible pixels.")

    # RGB values beneath fully transparent pixels are irrelevant when rendered,
    # but clearing them makes later resampling deterministic and matte-safe. The
    # supplied transparency extraction also retains a few magenta-key colours in
    # semitransparent edge pixels. Neutralize only that edge condition; opaque
    # authored highlights remain byte-for-byte faithful to the source.
    clean = Image.new("RGBA", rgba.size, (0, 0, 0, 0))
    source_pixels = rgba.load()
    clean_pixels = clean.load()
    for y in range(rgba.height):
        for x in range(rgba.width):
            red, green, blue, opacity = source_pixels[x, y]
            if opacity == 0:
                continue
            if opacity < 255 and red * 4 > green * 5 and blue * 4 > green * 5:
                red = min(red, green)
                blue = min(blue, green)
            clean_pixels[x, y] = (red, green, blue, opacity)
    return clean.crop(bounds)


def square_derivative(
    visible_art: Image.Image,
    canvas_size: int,
    visible_fraction: float,
) -> Image.Image:
    target_span = round(canvas_size * visible_fraction)
    scale = min(target_span / visible_art.width, target_span / visible_art.height)
    rendered_size = (
        max(1, round(visible_art.width * scale)),
        max(1, round(visible_art.height * scale)),
    )
    rendered = alpha_safe_resize(visible_art, rendered_size)
    canvas = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    origin = (
        (canvas_size - rendered.width) // 2,
        (canvas_size - rendered.height) // 2,
    )
    canvas.alpha_composite(rendered, origin)
    return canvas


def png_bytes(image: Image.Image) -> bytes:
    metadata = PngImagePlugin.PngInfo()
    metadata.add(b"sRGB", b"\x00")
    stream = BytesIO()
    image.save(
        stream,
        format="PNG",
        compress_level=9,
        optimize=True,
        dpi=(96, 96),
        pnginfo=metadata,
    )
    return stream.getvalue()


def ico_bytes(icon_frames: dict[int, Image.Image]) -> bytes:
    frames = [png_bytes(icon_frames[size]) for size in ICON_SIZES]
    header_size = 6 + (16 * len(frames))
    offset = header_size
    directory = bytearray()
    for size, frame in zip(ICON_SIZES, frames, strict=True):
        dimension = 0 if size == 256 else size
        directory.extend(
            pack(
                "<BBBBHHII",
                dimension,
                dimension,
                0,
                0,
                1,
                32,
                len(frame),
                offset,
            )
        )
        offset += len(frame)

    return pack("<HHH", 0, 1, len(frames)) + bytes(directory) + b"".join(frames)


def describe(path: Path, data: bytes) -> str:
    return f"{path.relative_to(REPOSITORY_ROOT)}  {len(data)} bytes  {sha256(data).hexdigest().upper()}"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify checked-in derivatives byte-for-byte without writing files",
    )
    arguments = parser.parse_args()

    source_bytes = SOURCE.read_bytes()
    source_hash = sha256(source_bytes).hexdigest()
    if source_hash != EXPECTED_SOURCE_SHA256:
        raise ValueError(
            "Authoritative Lite emblem changed; review the new source and update "
            "EXPECTED_SOURCE_SHA256 intentionally before regenerating assets."
        )

    with Image.open(BytesIO(source_bytes)) as source_image:
        if source_image.mode != "RGBA" or source_image.getchannel("A").getextrema() != (0, 255):
            raise ValueError("Authoritative Lite emblem must be RGBA with transparent and opaque pixels.")
        visible_art = normalized_visible_art(source_image)

    icon_master = square_derivative(visible_art, 1024, 0.875)
    navigation_emblem = square_derivative(visible_art, 256, 0.93)
    docs_emblem = square_derivative(visible_art, 512, 0.90)
    icon_frames = {
        size: square_derivative(visible_art, size, ICON_VISIBLE_SPANS[size] / size)
        for size in ICON_SIZES
    }
    guide_icon = icon_frames[48]

    outputs = (
        (ASSET_ROOT / "ai-arena-icon.png", png_bytes(icon_master)),
        (ASSET_ROOT / "ai-arena-icon.ico", ico_bytes(icon_frames)),
        (ASSET_ROOT / "ai-arena-guide-icon.png", png_bytes(guide_icon)),
        (ASSET_ROOT / "ai-arena-emblem-lite.png", png_bytes(navigation_emblem)),
        (DOCS_ASSET_ROOT / "ai-arena-lite-emblem.png", png_bytes(docs_emblem)),
    )
    if arguments.check:
        drifted = [path for path, expected in outputs if not path.is_file() or path.read_bytes() != expected]
        if drifted:
            formatted = ", ".join(str(path.relative_to(REPOSITORY_ROOT)) for path in drifted)
            raise SystemExit(f"Brand asset derivatives are missing or stale: {formatted}")
    else:
        for path, data in outputs:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)

    print(describe(SOURCE, source_bytes))
    for path, data in outputs:
        print(describe(path, data))
    print("ICO frames: " + ", ".join(str(size) for size in ICON_SIZES))
    if arguments.check:
        print("Brand asset derivatives are current.")


if __name__ == "__main__":
    main()
