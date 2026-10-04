"""Gauge-face atlases (4 x 2 cells of 256 px) for the cockpits, drawn with PIL.

Cells: 0 speedometer, 1 tachometer, 2 fuel, 3 water temp, 4 oil pressure, 5 boost / volts, 6 clock, 7 digital panel.
Outside each dial is transparent (the game draws textured cockpit tags with alpha cutout).
Two atlases per car: <Car>_gauges.png (km/h) and <Car>_gauges_mph.png (mph); DriverCam shows the one matching the game's
unit setting. The speedometer and tachometer have no painted needle: their needles are geometry that DriverCam turns.
Scales come from specs.DIALS, which build_interior.py also uses for the needle ranges.
The digital style (the W8) gets a glyph strip (0-9 and a blank) and the rpm-bar texels instead (specs.GLYPH, BAR_TEXELS).
python gauges.py <out_dir>
"""
import math, os, sys
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from specs import DIALS, SWEEP, NEEDLE, GLYPH, BAR_TEXELS, DIGITAL, minor_ticks

CELL = 256

STYLES = {
    # face, ticks/numbers, needle, redline, font scale
    "classic": dict(face=(14, 14, 16), ink=(235, 235, 225), needle=(240, 240, 240), red=(210, 40, 30)),
    "jdm":     dict(face=(10, 10, 12), ink=(245, 245, 245), needle=(255, 70, 40), red=(230, 30, 30)),
    "italian": dict(face=(12, 12, 12), ink=(255, 170, 40), needle=(255, 255, 255), red=(255, 40, 30)),
    "american":dict(face=(16, 16, 20), ink=(230, 230, 230), needle=(255, 120, 30), red=(220, 30, 30)),
    "german":  dict(face=(8, 8, 8), ink=(250, 250, 250), needle=(255, 120, 0), red=(230, 30, 30)),
    "eighties":dict(face=(12, 12, 14), ink=(200, 230, 255), needle=(255, 80, 40), red=(255, 50, 50)),
    "digital": dict(face=(6, 10, 8), ink=(80, 255, 140), needle=(80, 255, 140), red=(255, 70, 50)),
}
for _k, _st in STYLES.items(): _st["needle"] = tuple(int(round(c * 255)) for c in NEEDLE[_k])


def font(size):
    for f in ("arialbd.ttf", "arial.ttf", "DejaVuSans-Bold.ttf"):
        try: return ImageFont.truetype(f, size)
        except OSError: pass
    return ImageFont.load_default()


def dial(d, cx, cy, r, st, lo, hi, step, label, red_from=None, sweep=SWEEP, minor=2, needle_at=0.0, draw_needle=True):
    a0, a1 = sweep
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=st["face"] + (255,))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(60, 60, 64, 255), width=4)
    n = int(round((hi - lo) / step))
    assert abs(n * step - (hi - lo)) < 1e-6, f"dial {lo}-{hi} is not a whole number of {step} steps"
    if red_from is not None:
        ta = (red_from - lo) / (hi - lo)
        aa, ab = a0 + (a1 - a0) * ta, a1
        # PIL arcs go clockwise from 3 o'clock in degrees; ours are maths angles (counter-clockwise)
        d.arc([cx - r * 0.86, cy - r * 0.86, cx + r * 0.86, cy + r * 0.86], start=-aa, end=-ab, fill=st["red"] + (255,), width=int(r * 0.07))
    f = font(int(r * 0.2))
    for i in range(n * minor + 1):
        t = i / (n * minor)
        a = math.radians(a0 + (a1 - a0) * t)
        major = i % minor == 0
        r0 = r * (0.74 if major else 0.8)
        x0, y0 = cx + math.cos(a) * r0, cy - math.sin(a) * r0
        x1, y1 = cx + math.cos(a) * r * 0.9, cy - math.sin(a) * r * 0.9
        d.line([x0, y0, x1, y1], fill=st["ink"] + (255,), width=4 if major else 2)
        if major:
            v = lo + (hi - lo) * t
            txt = f"{v:g}"
            tx, ty = cx + math.cos(a) * r * 0.55, cy - math.sin(a) * r * 0.55
            bb = d.textbbox((0, 0), txt, font=f)
            d.text((tx - (bb[2] - bb[0]) / 2, ty - (bb[3] - bb[1]) / 2 - bb[1]), txt, font=f, fill=st["ink"] + (255,))
    fl = font(int(r * 0.13))
    bb = d.textbbox((0, 0), label, font=fl)
    d.text((cx - (bb[2] - bb[0]) / 2, cy + r * 0.35), label, font=fl, fill=st["ink"] + (200,))
    if not draw_needle: return   # the speedometer / tachometer needle and hub cap are geometry (build_interior.needle)
    # needle at rest
    a = math.radians(a0 + (a1 - a0) * needle_at)
    d.line([cx - math.cos(a) * r * 0.12, cy + math.sin(a) * r * 0.12, cx + math.cos(a) * r * 0.78, cy - math.sin(a) * r * 0.78],
           fill=st["needle"] + (255,), width=6)
    d.ellipse([cx - r * 0.09, cy - r * 0.09, cx + r * 0.09, cy + r * 0.09], fill=(30, 30, 30, 255), outline=(90, 90, 90, 255), width=2)


def digital(d, x0, y0, st, unit):
    """Cell 7: the W8's screen, background and unit label only; DriverCam draws the digits and bars as quads."""
    d.rounded_rectangle([x0 + 6, y0 + 40, x0 + CELL - 6, y0 + CELL - 40], 14, fill=st["face"] + (255,), outline=(50, 60, 55, 255), width=3)
    px = x0 + CELL / 2 + DIGITAL["unit_x"] * 0.97 * CELL / 2
    py = y0 + CELL / 2 - DIGITAL["unit_y"] * 0.6 * CELL / 2
    fs = font(24)
    d.text((px, py), unit, font=fs, fill=st["ink"] + (255,))
    fr = font(13)
    d.text((x0 + CELL - 52, y0 + 64), "RPM", font=fr, fill=st["ink"] + (150,))


def glyphs(d, st):
    """The digital readout's glyphs 0-9 and a blank (index 10), ink on transparent, along the top of atlas row 0."""
    g = GLYPH
    f = font(int(g["h"] * 1.02))
    for k in range(g["count"] - 1):   # the last one stays blank
        x, y = g["x0"] + k * g["pitch"], g["y0"]
        txt = str(k)
        bb = d.textbbox((0, 0), txt, font=f)
        tw, th = bb[2] - bb[0], bb[3] - bb[1]
        d.text((x + (g["w"] - tw) / 2 - bb[0], y + (g["h"] - th) / 2 - bb[1]), txt, font=f, fill=st["ink"] + (255,))


def bar_texels(d, st):
    """Solid patches for the rpm bars: unlit, lit, red-lit (the quads sample the middle of each)."""
    b = BAR_TEXELS
    dim = tuple(int(c * 0.22) for c in st["ink"])
    for k, c in enumerate((dim, st["ink"], st["red"])):
        x = b["x0"] + k * b["pitch"]
        d.rectangle([x, b["y0"], x + b["w"] - 1, b["y0"] + b["h"] - 1], fill=c + (255,))


def atlas(car, out, unit):
    """One car's atlas for one speed unit ("km/h" or "mph")."""
    spec = DIALS[car]
    st = STYLES[spec["style"]]
    smax, sstep = spec["kmh"] if unit == "km/h" else spec["mph"]
    tach_max, red = spec["tach"]
    img = Image.new("RGBA", (CELL * 4, CELL * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = lambda i: (CELL * (i % 4) + CELL // 2, CELL * (i // 4) + CELL // 2)
    r = CELL // 2 - 6
    if spec["style"] == "digital":
        glyphs(d, st); bar_texels(d, st)   # cells 0-2 and 6 are unused by the W8's layout
    else:
        dial(d, *c(0), r, st, 0, smax, sstep, unit, minor=minor_ticks(sstep), draw_needle=False)
        dial(d, *c(1), r, st, 0, tach_max, 1, "x1000 rpm", red_from=red, minor=5, draw_needle=False)
        dial(d, *c(2), r, st, 0, 1, 0.5, "FUEL", sweep=(150, 30), minor=2, needle_at=0.7)
    dial(d, *c(3), r, st, 50, 130, 40, "TEMP", red_from=115, sweep=(150, 30), minor=4, needle_at=0.45)
    dial(d, *c(4), r, st, 0, 8, 2, "OIL", sweep=(150, 30), minor=2, needle_at=0.5)
    dial(d, *c(5), r, st, 8, 16, 4, "VOLT", sweep=(150, 30), minor=4, needle_at=0.55)
    if spec["style"] != "digital":
        # clock
        cx, cy = c(6); d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=st["face"] + (255,), outline=(60, 60, 64, 255), width=4)
        for h in range(12):
            a = math.radians(90 - h * 30)
            d.line([cx + math.cos(a) * r * 0.75, cy - math.sin(a) * r * 0.75, cx + math.cos(a) * r * 0.9, cy - math.sin(a) * r * 0.9], fill=st["ink"] + (255,), width=5)
        d.line([cx, cy, cx + r * 0.45, cy - r * 0.2], fill=st["ink"] + (255,), width=7)
        d.line([cx, cy, cx - r * 0.1, cy - r * 0.7], fill=st["ink"] + (255,), width=5)
    digital(d, CELL * 3, CELL, st, unit)
    img.save(out)



if __name__ == "__main__":
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for car in DIALS:
        atlas(car, os.path.join(out, f"{car}_gauges.png"), "km/h")
        atlas(car, os.path.join(out, f"{car}_gauges_mph.png"), "mph")
    print("ok")
