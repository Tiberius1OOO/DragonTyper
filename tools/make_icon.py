from PIL import Image
import math
import os

src = r"C:\Users\Tiberia\.cursor\projects\d-Developement-Lokal-DragonTyper\assets\1-d6ce7419-f142-4229-90e5-db8d61a43f6b.png"
img = Image.open(src).convert("RGBA")
w, h = img.size
side = min(w, h)
left = (w - side) // 2
top = (h - side) // 2
img = img.crop((left, top, left + side, top + side))
img = img.resize((512, 512), Image.Resampling.LANCZOS)
pixels = img.load()
cx = cy = 255.5
r_outer = 252.0
for y in range(512):
    for x in range(512):
        dx = x - cx
        dy = y - cy
        dist = math.sqrt(dx * dx + dy * dy)
        r, g, b, a = pixels[x, y]
        if dist > r_outer:
            fade = max(0.0, 1.0 - (dist - r_outer) / 3.0)
            if fade <= 0:
                pixels[x, y] = (0, 0, 0, 0)
            else:
                pixels[x, y] = (r, g, b, int(a * fade))
        elif dist > r_outer - 1 and r < 20 and g < 20 and b < 20:
            pixels[x, y] = (0, 0, 0, 0)

root = r"D:\Developement\Lokal\DragonTyper"
paths = [
    os.path.join(root, "images"),
    os.path.join(root, "DragonTyper", "images"),
]
for path in paths:
    os.makedirs(path, exist_ok=True)
    img.save(os.path.join(path, "icon.png"))
    img.resize((128, 128), Image.Resampling.LANCZOS).save(os.path.join(path, "logo.png"))

print("ok", img.getpixel((0, 0)), img.getpixel((256, 8)))
