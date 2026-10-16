"""Generate the project's original N speech-bubble icon (requires Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SIZE = 512
image = Image.new('RGBA', (SIZE, SIZE))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((20, 20, 492, 492), radius=116, fill='#4856D6')
draw.rounded_rectangle((104, 112, 408, 360), radius=65, fill='white')
draw.polygon(((128, 314), (128, 414), (235, 339)), fill='white')
draw.line(((185, 303), (185, 177), (326, 303), (326, 177)), fill='#4856D6', width=30, joint='curve')
image.save(ROOT / 'assets/icon.png')
image.save(ROOT / 'assets/icon.ico', sizes=[(x, x) for x in [16, 24, 32, 48, 64, 128, 256]])
badged = image.copy()
draw = ImageDraw.Draw(badged)
draw.ellipse((294, 294, 504, 504), fill='white')
draw.ellipse((312, 312, 486, 486), fill='#EF3043')
badged.save(ROOT / 'assets/icon-unread.png')
badged.save(ROOT / 'assets/icon-unread.ico', sizes=[(x, x) for x in [16, 24, 32, 48, 64, 128, 256]])
