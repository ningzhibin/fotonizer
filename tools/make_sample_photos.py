"""Generate synthetic test photos (with EXIF) for Fotonizer end-to-end checks."""
from PIL import Image, ImageDraw, ImageFilter, ImageEnhance
import os, math, random, shutil, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "sample-data", "source", "DCIM", "100CANON")
SRC2 = os.path.join(ROOT, "sample-data", "source", "DCIM", "101CANON")


def scene(width=1600, height=1200, seed=7, blur=0.0, brightness=1.0, portrait=False):
    random.seed(seed)
    if portrait:
        width, height = height, width
    img = Image.new("RGB", (width, height))
    d = ImageDraw.Draw(img)
    for y in range(height):
        t = y / max(1, height - 1)
        r = int(28 + 150 * t + 25 * math.sin(t * 3.1))
        g = int(60 + 110 * t)
        b = int(120 + 90 * (1 - t))
        d.line([(0, y), (width, y)], fill=(min(r, 255), min(g, 255), min(b, 255)))

    # sun
    cx, cy = int(width * 0.72), int(height * 0.30)
    for radius in range(120, 0, -6):
        alpha = 1 - radius / 120
        d.ellipse([cx - radius, cy - radius, cx + radius, cy + radius],
                  fill=(255, int(200 + 40 * alpha), int(120 + 80 * alpha)))

    # mountains
    d.polygon([(0, height), (int(width * 0.18), int(height * 0.55)),
               (int(width * 0.38), int(height * 0.78)), (int(width * 0.55), int(height * 0.58)),
               (int(width * 0.78), int(height * 0.82)), (width, int(height * 0.62)), (width, height)],
              fill=(30, 63, 70))
    d.polygon([(0, height), (int(width * 0.25), int(height * 0.72)),
               (int(width * 0.45), int(height * 0.9)), (int(width * 0.7), int(height * 0.7)),
               (width, height)], fill=(20, 42, 50))

    # water reflection
    for i in range(60):
        y = random.randint(int(height * 0.72), height - 1)
        x = random.randint(0, width)
        length = random.randint(20, 160)
        d.line([(x, y), (min(width, x + length), y)], fill=(120, 170, 190), width=1)

    # foreground detail: grass / branches for sharpness signal
    for _ in range(2000):
        x = random.randint(0, width - 1)
        y = random.randint(int(height * 0.82), height - 1)
        h = random.randint(3, 14)
        d.line([(x, y), (x + random.randint(-2, 2), y - h)], fill=(20, random.randint(50, 110), 30), width=1)

    img = ImageEnhance.Brightness(img).enhance(brightness)
    if blur > 0:
        img = img.filter(ImageFilter.GaussianBlur(blur))
    return img


def save(path, img, captured, model="EOS R6", make="Canon", orientation=1, subsec=None):
    exif = Image.Exif()
    exif[271] = make
    exif[272] = model
    exif[274] = orientation
    exif[305] = "Fotonizer sample generator"
    exif[36867] = captured  # DateTimeOriginal
    exif[36868] = captured
    if subsec:
        exif[37521] = subsec
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, quality=92, exif=exif)
    ts = __import__("datetime").datetime.strptime(captured, "%Y:%m:%d %H:%M:%S").timestamp()
    os.utime(path, (ts, ts))
    print("wrote", path, img.size)


def main():
    if os.path.isdir(os.path.join(ROOT, "sample-data")):
        shutil.rmtree(os.path.join(ROOT, "sample-data"))
    os.makedirs(SRC, exist_ok=True)

    save(os.path.join(SRC, "IMG_0001.JPG"), scene(seed=11, blur=0.2), "2024:05:01 09:00:00")
    save(os.path.join(SRC, "IMG_0002.JPG"), scene(seed=12, blur=1.0, brightness=0.45),
         "2024:05:01 09:05:00", subsec="250")
    save(os.path.join(SRC, "IMG_0003.JPG"), scene(seed=13, portrait=True), "2024:05:01 09:10:00",
         orientation=6)

    # burst: same scene, increasing blur; the sharpest one should win
    for i, blur in enumerate([0.0, 0.8, 1.6, 2.4, 3.2]):
        save(os.path.join(SRC2, f"BURST_{i + 1:02d}.JPG"), scene(seed=21, blur=blur),
             "2024:05:01 10:15:00", subsec=f"{i * 100 + 50:03d}")

    # duplicate content, different name
    shutil.copy2(os.path.join(SRC, "IMG_0001.JPG"), os.path.join(SRC2, "IMG_0001_COPY.JPG"))

    # a fake video (Fotonizer should copy it, never decode it)
    with open(os.path.join(SRC, "CLIP0001.MP4"), "wb") as f:
        f.write(b"\x00\x00\x00\x18ftypmp42" + os.urandom(64 * 1024))

    print("sample data ready at", os.path.join(ROOT, "sample-data"))


if __name__ == "__main__":
    sys.exit(main())
