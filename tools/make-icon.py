"""Draws PingTool/PingTool.ico: a dark rounded square with a green ping pulse (a flat line, one spike) and a dot.
Run:  python3 tools/make-icon.py   (needs Pillow). The .ico is committed; this script is how it was made."""
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
BACK, EDGE, GREEN = (48, 52, 58, 255), (88, 96, 108, 255), (80, 220, 100, 255)

def draw(size: int) -> Image.Image:
    s = size * 8                                   # drawn big, then reduced: smooth edges at every size
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pad = s * 0.04
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.22, fill=BACK, outline=EDGE, width=max(2, int(s * 0.025)))
    w = max(3, int(s * (0.085 if size >= 32 else 0.13)))      # thicker line on small icons so it stays visible
    y = s * 0.58
    pts = [(s * 0.16, y), (s * 0.36, y), (s * 0.45, s * 0.28), (s * 0.55, s * 0.78), (s * 0.63, y), (s * 0.70, y)]
    d.line(pts, fill=GREEN, width=w, joint="curve")
    r = w * 0.95
    d.ellipse([s * 0.80 - r, y - r, s * 0.80 + r, y + r], fill=GREEN)
    return im.resize((size, size), Image.LANCZOS)

frames = [draw(n) for n in SIZES]
frames[-1].save("PingTool/PingTool.ico", format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
print("wrote PingTool/PingTool.ico", SIZES)
