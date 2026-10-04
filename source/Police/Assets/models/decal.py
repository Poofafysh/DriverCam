"""Door decal for the police car models: 'POLICE' with a stripe and a small badge, transparent elsewhere (alpha cutout).
python decal.py <out.png>
"""
import sys
from PIL import Image, ImageDraw, ImageFont

W, H = 1024, 256

def font(size):
    for f in ("arialbd.ttf", "impact.ttf", "DejaVuSans-Bold.ttf"):
        try: return ImageFont.truetype(f, size)
        except OSError: pass
    return ImageFont.load_default()

img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
# stripe under the lettering
d.rectangle([0, 176, W, 204], fill=(20, 60, 170, 255))
d.rectangle([0, 210, W, 218], fill=(20, 60, 170, 255))
# badge (a seven-point star in a ring) on the left
cx, cy, r = 110, 100, 72
d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(210, 170, 40, 255), outline=(20, 20, 20, 255), width=6)
import math
pts = []
for k in range(14):
    a = math.pi / 2 + k * math.pi / 7
    rr = r * (0.78 if k % 2 == 0 else 0.4)
    pts.append((cx + math.cos(a) * rr, cy - math.sin(a) * rr))
d.polygon(pts, fill=(250, 220, 90, 255), outline=(30, 30, 30, 255))
# lettering
f = font(150)
txt = "POLICE"
bb = d.textbbox((0, 0), txt, font=f)
x = 220 + (W - 220 - (bb[2] - bb[0])) / 2
d.text((x + 6, 18 - bb[1] + 6), txt, font=f, fill=(0, 0, 0, 120))      # soft shadow
d.text((x, 18 - bb[1]), txt, font=f, fill=(15, 15, 18, 255))
img.save(sys.argv[1])
print("ok")
