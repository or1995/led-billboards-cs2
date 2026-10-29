"""
Draws the toolbar icons for the LED screen props (one PNG per prefab, plus the category).

The shapes follow the real props: screen proportions, wall or poles, pole count and
thickness, grids, round faces and double-sided screens. Output goes to
LedScreens/UI/ledscreens-icons/, which the build copies next to the mod.

    python tools/make_icons.py            (needs Pillow)
"""
import colorsys
import math
import os
import random
import zlib

from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(__file__), "..", "LedScreens", "UI", "ledscreens-icons")
SIZE = 256          # final icon size
SS = 4              # supersampling factor
PAD = 14            # free border, in final pixels

# Ad panel sizes of the vanilla source props in meters (width, height), from the mod log.
WALL_SMALL = (0.77, 1.16)
WALL_MEDIUM = (3.90, 1.75)
WALL_LARGE = (8.39, 3.93)
WALL_HUGE = (12.56, 5.89)
STEEL = 0.22        # flag pole trunk
CONCRETE = 2.2      # chimney trunk

# kind: wall | stand | pole | round | roundpole
# Each entry: name, kind, panel size, columns, rows, elevation (bottom of screen), poles, pole diameter, double-sided
ICONS = [
    ("LedScreenWallSmall01", "wall", WALL_SMALL, 1, 1, 0, 0, 0, False),
    ("LedScreenWallMedium01", "wall", WALL_MEDIUM, 1, 1, 0, 0, 0, False),
    ("LedScreenWallLarge01", "wall", WALL_LARGE, 1, 1, 0, 0, 0, False),
    ("LedScreenWallHuge02", "wall", WALL_HUGE, 1, 1, 0, 0, 0, False),
    ("LedPosterWallSmall2x2", "wall", WALL_SMALL, 2, 2, 0, 0, 0, False),
    ("LedPortraitWallMedium1x3", "wall", WALL_MEDIUM, 1, 3, 0, 0, 0, False),
    ("LedTickerWallMedium3x1", "wall", WALL_MEDIUM, 3, 1, 0, 0, 0, False),
    ("LedTickerWallMedium6x1", "wall", WALL_MEDIUM, 6, 1, 0, 0, 0, False),
    ("LedVideoWallLarge2x1", "wall", WALL_LARGE, 2, 1, 0, 0, 0, False),
    ("LedVideoWallLarge2x2", "wall", WALL_LARGE, 2, 2, 0, 0, 0, False),
    ("LedVideoWallLarge3x2", "wall", WALL_LARGE, 3, 2, 0, 0, 0, False),
    ("LedFacadeLarge3x3", "wall", WALL_LARGE, 3, 3, 0, 0, 0, False),
    ("LedFacadeLarge5x3", "wall", WALL_LARGE, 5, 3, 0, 0, 0, False),
    ("LedFacadeLarge6x5", "wall", WALL_LARGE, 6, 5, 0, 0, 0, False),
    ("LedFacadeLarge2x6", "wall", WALL_LARGE, 2, 6, 0, 0, 0, False),
    ("LedScreenSmall01", "stand", (3.82, 1.72), 1, 1, 0.96, 2, 0.18, True),
    ("LedScreenSmall02", "stand", (3.82, 1.72), 1, 1, 0.25, 2, 0.18, True),
    ("LedScreenMedium02", "stand", (5.13, 2.41), 1, 1, 0.55, 2, 0.25, True),
    ("LedScreenLarge02", "stand", (9.23, 4.32), 1, 1, 0.66, 2, 0.35, True),
    ("LedScreenHuge02", "stand", (13.08, 6.13), 1, 1, 1.48, 2, 0.5, True),
    ("LedTotemSmall2x3", "pole", WALL_SMALL, 2, 3, 0.4, 1, STEEL, False),
    ("LedTotemSmall2x3Double", "pole", WALL_SMALL, 2, 3, 0.4, 1, STEEL, True),
    ("LedRoadsideLarge01", "pole", WALL_LARGE, 1, 1, 2.5, 2, STEEL, False),
    ("LedRoadsideHuge01", "pole", WALL_HUGE, 1, 1, 3.0, 3, STEEL, False),
    ("LedScreenHighPoleMedium01", "pole", WALL_MEDIUM, 1, 1, 6.0, 1, STEEL, False),
    ("LedScreenHighPoleLarge01", "pole", WALL_LARGE, 1, 1, 7.0, 2, STEEL, False),
    ("LedHighPoleLargeDouble", "pole", WALL_LARGE, 1, 1, 7.0, 2, STEEL, True),
    ("LedScreenHighPoleHuge01", "pole", WALL_HUGE, 1, 1, 9.0, 1, CONCRETE, False),
    ("LedHighPoleHugeDouble", "pole", WALL_HUGE, 1, 1, 9.0, 1, CONCRETE, True),
    ("LedHighPolePortrait1x3", "pole", WALL_LARGE, 1, 3, 8.0, 1, CONCRETE, False),
    ("LedScreenHighPoleWide01", "pole", WALL_HUGE, 2, 1, 10.0, 2, CONCRETE, False),
    ("LedHighPoleWideDouble", "pole", WALL_HUGE, 2, 1, 10.0, 2, CONCRETE, True),
    ("LedScreenHighPoleGiant01", "pole", WALL_LARGE, 3, 3, 10.0, 2, CONCRETE, False),
    ("LedHighPoleMega5x3", "pole", WALL_LARGE, 5, 3, 12.0, 3, CONCRETE, False),
    ("LedScreenHighPoleRibbon01", "pole", WALL_HUGE, 3, 1, 10.0, 3, CONCRETE, False),
    ("LedScreenRoundSmall01", "round", (0.86, 0.86), 1, 1, 0, 0, 0, False),
    ("LedScreenRoundMedium01", "round", (1.47, 1.47), 1, 1, 0, 0, 0, False),
    ("LedScreenRoundLarge01", "round", (2.69, 2.69), 1, 1, 0, 0, 0, False),
    ("LedRoundPoleMedium01", "roundpole", (1.47, 1.47), 1, 1, 2.6, 1, STEEL, True),
    ("LedRoundPoleLarge01", "roundpole", (2.69, 2.69), 1, 1, 3.2, 1, STEEL, True),
]

BEZEL = (24, 26, 30, 255)
WALL = (128, 124, 118, 255)
WALL_DARK = (98, 94, 89, 255)
STEEL_COLOR = (150, 156, 164, 255)
CONCRETE_COLOR = (176, 170, 160, 255)
GROUND = (88, 120, 70, 255)


def seed_of(name):
    return zlib.crc32(name.encode())


def screen_content(w, h, name):
    """Bright 'LED' picture: a colour gradient with a simple motif and an LED dot grid."""
    rnd = random.Random(seed_of(name))
    hue = rnd.random()
    # The gradient is smooth, so compute it small and scale it up.
    gw, gh = max(2, w // 8), max(2, h // 8)
    img = Image.new("RGBA", (gw, gh))
    px = img.load()
    for y in range(gh):
        for x in range(gw):
            t = (x / (gw - 1)) * 0.6 + (y / (gh - 1)) * 0.4
            r, g, b = colorsys.hsv_to_rgb((hue + t * 0.35) % 1.0, 0.85, 1.0 - 0.25 * (y / (gh - 1)))
            px[x, y] = (int(r * 255), int(g * 255), int(b * 255), 255)
    img = img.resize((w, h), Image.BILINEAR)

    d = ImageDraw.Draw(img)
    motif = rnd.randrange(3)
    s = min(w, h)
    if motif == 0:      # sun / disc
        cx, cy, rr = w * rnd.uniform(0.3, 0.7), h * 0.45, s * 0.28
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), fill=(255, 245, 200, 230))
    elif motif == 1:    # waves
        for k in range(3):
            pts = [(x, h * (0.55 + k * 0.13) + math.sin(x / w * math.pi * 3 + k) * s * 0.07) for x in range(0, w + 1, max(1, w // 64))]
            d.line(pts, fill=(255, 255, 255, 170 - k * 40), width=max(2, s // 14))
    else:               # bars
        n = 5
        for k in range(n):
            bh = h * rnd.uniform(0.25, 0.8)
            x0 = w * (0.1 + k * 0.8 / n)
            d.rectangle((x0, h - bh, x0 + w * 0.8 / n * 0.7, h), fill=(255, 255, 255, 150))

    # LED dot grid: dark gaps between the "pixels".
    step = max(SS * 3, s // 12)
    grid = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grid)
    for x in range(0, w, step):
        gd.line((x, 0, x, h), fill=(0, 0, 0, 60), width=max(1, step // 5))
    for y in range(0, h, step):
        gd.line((0, y, w, y), fill=(0, 0, 0, 60), width=max(1, step // 5))
    img.alpha_composite(grid)

    # Glossy highlight on the top half.
    gloss = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(gloss).polygon([(0, 0), (w, 0), (w, h * 0.35), (0, h * 0.55)], fill=(255, 255, 255, 38))
    img.alpha_composite(gloss)
    return img


def draw_screen(canvas, box, name, cols, rows, round_face):
    x0, y0, x1, y1 = [int(v) for v in box]
    w, h = x1 - x0, y1 - y0
    bezel = max(SS * 2, int(min(w, h) * 0.05))
    d = ImageDraw.Draw(canvas)
    if round_face:
        d.ellipse((x0, y0, x1, y1), fill=BEZEL)
        inner = (x0 + bezel, y0 + bezel, x1 - bezel, y1 - bezel)
        iw, ih = inner[2] - inner[0], inner[3] - inner[1]
        content = screen_content(iw, ih, name)
        mask = Image.new("L", (iw, ih), 0)
        ImageDraw.Draw(mask).ellipse((0, 0, iw - 1, ih - 1), fill=255)
        canvas.paste(content, inner[:2], mask)
        return

    d.rounded_rectangle((x0, y0, x1, y1), radius=bezel, fill=BEZEL)
    inner = (x0 + bezel, y0 + bezel, x1 - bezel, y1 - bezel)
    iw, ih = inner[2] - inner[0], inner[3] - inner[1]
    if iw > 0 and ih > 0:
        canvas.alpha_composite(screen_content(iw, ih, name), inner[:2])
    # Thin seams where the panels of a grid meet.
    for c in range(1, cols):
        x = inner[0] + iw * c / cols
        d.line((x, inner[1], x, inner[3]), fill=(16, 16, 20, 230), width=max(2, int(SS * 1.5)))
    for r in range(1, rows):
        y = inner[1] + ih * r / rows
        d.line((inner[0], y, inner[2], y), fill=(16, 16, 20, 230), width=max(2, int(SS * 1.5)))


def render(name, kind, panel, cols, rows, elevation, poles, diameter, double):
    big = SIZE * SS
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(canvas)

    sw, sh = panel[0] * cols, panel[1] * rows        # screen size, meters
    grounded = kind in ("stand", "pole", "roundpole")
    wall_margin = 0.18 * max(sw, sh) if kind in ("wall", "round") else 0
    back = 0.12 * max(sw, sh) if double else 0        # second screen peeking out behind

    scene_w = sw + 2 * wall_margin + back
    scene_h = sh + 2 * wall_margin + (elevation if grounded else 0) + back
    if grounded:
        scene_h += 0.05 * scene_h                       # room for the ground strip
    avail = big - 2 * PAD * SS
    scale = avail / max(scene_w, scene_h)

    # Centre the scene; grounded props stand on the bottom.
    ox = (big - scene_w * scale) / 2
    oy = (big - scene_h * scale) / 2
    ground_y = oy + scene_h * scale

    def to_px(mx, my_from_ground):
        return ox + mx * scale, ground_y - my_from_ground * scale

    if kind in ("wall", "round"):
        # Piece of building wall behind the screen.
        wx0, wy0 = ox, oy
        wx1, wy1 = ox + scene_w * scale, oy + scene_h * scale
        d.rounded_rectangle((wx0, wy0, wx1, wy1), radius=SS * 6, fill=WALL)
        brick = max(SS * 8, int((wy1 - wy0) / 9))
        for i, y in enumerate(range(int(wy0) + brick, int(wy1), brick)):
            d.line((wx0 + SS * 2, y, wx1 - SS * 2, y), fill=WALL_DARK, width=SS)
        sx0 = ox + wall_margin * scale
        sy0 = oy + wall_margin * scale
        # soft shadow
        shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        ImageDraw.Draw(shadow).rectangle((sx0 + SS * 6, sy0 + SS * 8, sx0 + sw * scale + SS * 6, sy0 + sh * scale + SS * 8), fill=(0, 0, 0, 90))
        canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(SS * 5)))
        draw_screen(canvas, (sx0, sy0, sx0 + sw * scale, sy0 + sh * scale), name, cols, rows, kind == "round")
        return canvas

    # Ground strip with a soft shadow.
    d.rounded_rectangle((ox - SS * 4, ground_y - SS * 3, ox + scene_w * scale + SS * 4, ground_y + SS * 5), radius=SS * 3, fill=GROUND)

    sx0 = ox
    top = elevation + sh
    pole_color = CONCRETE_COLOR if diameter >= 1 else STEEL_COLOR
    pw = max(SS * 5, diameter * scale)

    def draw_screens(offset):
        x, y = to_px(offset, top + offset)
        box = (x, y, x + sw * scale, y + sh * scale)
        draw_screen(canvas, box, name if offset == 0 else name + "back", cols, rows, kind == "roundpole")

    if double:
        # The back screen, darker, behind and up-right.
        layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        saved = canvas
        canvas = layer
        draw_screens(back)
        dark = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        dark.paste((10, 10, 20, 120), mask=layer.split()[3])
        layer.alpha_composite(dark)
        canvas = saved
        canvas.alpha_composite(layer)
        d = ImageDraw.Draw(canvas)

    # Poles: spread over 70% of the width (like the mod), from the ground to behind the screen.
    for k in range(poles):
        spread = 0 if poles == 1 else (k / (poles - 1) - 0.5) * sw * 0.7
        cx = sx0 + (sw / 2 + spread) * scale
        _, py_top = to_px(0, elevation + sh * 0.5)
        d.rectangle((cx - pw / 2, py_top, cx + pw / 2, ground_y), fill=pole_color)
        d.rectangle((cx + pw * 0.15, py_top, cx + pw / 2, ground_y), fill=tuple(int(c * 0.82) for c in pole_color[:3]) + (255,))

    draw_screens(0)
    return canvas


def category_icon():
    big = SIZE * SS
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    m = PAD * SS
    draw_screen(canvas, (m, big * 0.2, big - m, big * 0.78), "LedScreens", 1, 1, False)
    d = ImageDraw.Draw(canvas)
    cx, cy, r = big / 2, big * 0.49, big * 0.13
    d.polygon([(cx - r * 0.7, cy - r), (cx - r * 0.7, cy + r), (cx + r, cy)], fill=(255, 255, 255, 235))
    return canvas


def save(img, name):
    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    img.save(os.path.join(OUT, name + ".png"), optimize=True)


def main():
    os.makedirs(OUT, exist_ok=True)
    for entry in ICONS:
        save(render(*entry), entry[0])
    save(category_icon(), "LedScreens")
    print(f"{len(ICONS) + 1} icons written to {os.path.abspath(OUT)}")


if __name__ == "__main__":
    main()
