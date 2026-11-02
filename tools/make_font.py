"""Fabrique l'atlas de police du programme de bureau (DejaVu Sans, licence libre) : font.bin"""
import struct, sys
from PIL import Image, ImageDraw, ImageFont
size = int(sys.argv[2]) if len(sys.argv) > 2 else 18
font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', size)
chars = [c for c in range(32, 256) if c < 127 or c >= 160] + [0x2026, 0x2192, 0x2190, 0x2014, 0x2019, 0x00D7]
W = 512; x = y = 2; rowh = 0; glyphs = []
img = Image.new('L', (W, 512), 0); d = ImageDraw.Draw(img)
asc, desc = font.getmetrics()
for c in chars:
    ch = chr(c); l, t, r, b = font.getbbox(ch); w, h = max(1, r - l), max(1, b - t)
    adv = round(font.getlength(ch))
    if x + w + 2 > W: x = 2; y += rowh + 2; rowh = 0
    d.text((x - l, y - t), ch, font=font, fill=255)
    glyphs.append((c, x, y, w, h, l, t, adv)); x += w + 2; rowh = max(rowh, h)
H = y + rowh + 2; img = img.crop((0, 0, W, H))
with open(sys.argv[1], 'wb') as f:
    f.write(b'FD3F'); f.write(struct.pack('<HHHHH', W, H, asc + desc, asc, len(glyphs)))
    for g in glyphs: f.write(struct.pack('<HHHHHhhH', *g))
    f.write(img.tobytes())
print(len(glyphs), 'glyphes', W, 'x', H)
