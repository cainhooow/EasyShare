from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "EasyShare" / "Assets"
SOURCE = ASSETS / "Brand" / "EasyShareMark-v2.png"

ORANGE = (249, 115, 22, 255)
GRAPHITE = (31, 41, 55, 255)
WHITE = (255, 255, 255, 255)


def flattened_mark() -> Image.Image:
    source = Image.open(SOURCE).convert("RGBA")
    pixels = source.load()
    for y in range(source.height):
        for x in range(source.width):
            r, g, b, a = pixels[x, y]
            if a == 0:
                continue
            pixels[x, y] = ORANGE if r > 100 and r > g * 1.35 else GRAPHITE

    bbox = source.getbbox()
    if bbox is None:
        raise RuntimeError("Generated brand mark is empty.")

    cropped = source.crop(bbox)
    canvas = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    max_side = 820
    scale = min(max_side / cropped.width, max_side / cropped.height)
    size = (max(1, round(cropped.width * scale)), max(1, round(cropped.height * scale)))
    cropped = cropped.resize(size, Image.Resampling.LANCZOS)
    canvas.alpha_composite(cropped, ((1024 - size[0]) // 2, (1024 - size[1]) // 2))
    return canvas


def recolor(image: Image.Image, color: tuple[int, int, int, int]) -> Image.Image:
    result = Image.new("RGBA", image.size, color)
    result.putalpha(image.getchannel("A"))
    return result


def fit_mark(mark: Image.Image, size: tuple[int, int], padding_ratio: float = 0.12) -> Image.Image:
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    padding = round(min(size) * padding_ratio)
    target = (max(1, size[0] - 2 * padding), max(1, size[1] - 2 * padding))
    scale = min(target[0] / mark.width, target[1] / mark.height)
    resized = mark.resize((round(mark.width * scale), round(mark.height * scale)), Image.Resampling.LANCZOS)
    canvas.alpha_composite(resized, ((size[0] - resized.width) // 2, (size[1] - resized.height) // 2))
    return canvas


def font(size: int) -> ImageFont.FreeTypeFont:
    candidates = [
        Path(r"C:\Windows\Fonts\seguisb.ttf"),
        Path(r"C:\Windows\Fonts\segoeuib.ttf"),
        Path(r"C:\Windows\Fonts\segoeui.ttf"),
    ]
    path = next((candidate for candidate in candidates if candidate.exists()), None)
    if path is None:
        raise RuntimeError("Segoe UI font was not found.")
    return ImageFont.truetype(str(path), size)


def wordmark(mark: Image.Image, size: tuple[int, int], mark_ratio: float) -> Image.Image:
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    mark_size = round(size[1] * mark_ratio)
    compact = fit_mark(mark, (mark_size, mark_size), 0.07)
    mark_x = round(size[0] * 0.02)
    canvas.alpha_composite(compact, (mark_x, (size[1] - mark_size) // 2))

    draw = ImageDraw.Draw(canvas)
    text_font = font(round(size[1] * 0.38))
    x = mark_x + mark_size + round(size[1] * 0.04)
    baseline_box = draw.textbbox((0, 0), "EasyShare", font=text_font)
    y = (size[1] - (baseline_box[3] - baseline_box[1])) // 2 - baseline_box[1]
    draw.text((x, y), "Easy", font=text_font, fill=GRAPHITE)
    easy_width = draw.textlength("Easy", font=text_font)
    draw.text((x + easy_width, y), "Share", font=text_font, fill=ORANGE)
    return canvas


def main() -> None:
    mark = flattened_mark()
    mark.save(ASSETS / "Brand" / "EasyShareMark-Master.png")
    recolor(mark, GRAPHITE).save(ASSETS / "Brand" / "EasyShareMark-Monochrome-Dark.png")
    recolor(mark, WHITE).save(ASSETS / "Brand" / "EasyShareMark-Monochrome-Light.png")

    fit_mark(mark, (512, 512), 0.06).save(ASSETS / "LogoIcon.png")
    wordmark(mark, (900, 260), 0.82).save(ASSETS / "LogoText.png")
    fit_mark(mark, (50, 50), 0.08).save(ASSETS / "StoreLogo.png")
    fit_mark(mark, (300, 300), 0.08).save(ASSETS / "Square150x150Logo.scale-200.png")
    fit_mark(mark, (88, 88), 0.05).save(ASSETS / "Square44x44Logo.scale-200.png")
    fit_mark(mark, (48, 48), 0.05).save(ASSETS / "LockScreenLogo.scale-200.png")
    fit_mark(mark, (24, 24), 0.02).save(ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png")
    fit_mark(recolor(mark, WHITE), (48, 48), 0.04).save(
        ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png"
    )
    wordmark(mark, (620, 300), 0.72).save(ASSETS / "Wide310x150Logo.scale-200.png")
    wordmark(mark, (1240, 600), 0.72).save(ASSETS / "SplashScreen.scale-200.png")

    icon = fit_mark(mark, (256, 256), 0.04)
    icon.save(
        ASSETS / "AppIcon.ico",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
