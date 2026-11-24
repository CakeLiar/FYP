"""Builds labelled demo pictures, GIFs and a video from the frames saved by ContactLabDemoRecorder.

Usage: python3 compose_demo.py <recorder output folder>
"""
import csv
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageFilter

ROOT = Path(sys.argv[1])
OUT = ROOT / "pictures"
OUT.mkdir(exist_ok=True)
W, H = 1920, 1080
BAR = 96                      # Footer height. The frame is fitted above it.
BG = (10, 11, 15)
FONT = "/System/Library/Fonts/Helvetica.ttc"
PINK, GREY, WHITE, DIM = (255, 96, 220), (215, 215, 215), (240, 242, 246), (150, 156, 168)

TITLES = {
    "lab1": "Lab 1 · Forward reach (Z)",
    "lab2": "Lab 2 · Lateral reach (X)",
    "lab3": "Lab 3 · Vertical reach (Y)",
    "lab4": "Lab 4 · Two-hand box (X, both hands)",
    "lab5": "Lab 5 · Hidden object (free 3D search)",
}
NOTES = {
    "lab1": "The target stays 25 cm away. At gain 1.4 the real fingertip travels 17.9 cm.",
    "lab2": "Same rule, sideways. The yellow line is the gap between real and virtual fingertip.",
    "lab3": "Same rule, upwards, against gravity.",
    "lab4": "Each hand has its own home. The box turns green when both virtual fingertips touch it.",
    "lab5": "The object is invisible. The fingertip sweeps, misses, then finds it; the picture shows it revealed.",
}
# Label offsets in output pixels: (dx, dy) from the point to the text anchor, and the anchor side.
LABELS = {
    "lab1": {"home": (70, 10, "l"), "target": (-95, -40, "r"), "real": (60, 30, "l"), "virtual": (-60, 10, "r")},
    "lab2": {"home": (0, 95, "m"), "target": (0, -135, "m"), "real": (0, 70, "m"), "virtual": (-40, -60, "r")},
    "lab3": {"home": (60, 10, "l"), "target": (105, -10, "l"), "real": (60, 10, "l"), "virtual": (-60, 0, "r")},
    "lab4": {"home": (0, 75, "m"), "home2": (0, 75, "m"), "target": (0, -235, "m"), "real": (0, -60, "m"), "real2": (0, -60, "m")},
    "lab5": {"home": (65, 10, "l"), "target": (105, -35, "l"), "real": (-70, 55, "r"), "virtual": (90, 20, "l")},
}
TEXT = {
    "home": "home", "home2": "home", "target": "target: green + beep at virtual contact",
    "real": "real fingertip", "real2": "real fingertip", "virtual": "virtual fingertip",
}


def font(size):
    return ImageFont.truetype(FONT, size)


def load(lab, frame):
    """A frame fitted above the footer, on the scene's own background colour."""
    src = Image.open(ROOT / lab / f"{int(frame):04d}.png").convert("RGB")
    scale = (H - BAR) / src.height
    fitted = src.resize((round(src.width * scale), H - BAR), Image.LANCZOS)
    canvas = Image.new("RGB", (W, H), BG)
    canvas.paste(fitted, ((W - fitted.width) // 2, 0))
    return canvas, scale, (W - fitted.width) // 2


def point(row, key, scale, left):
    x, y = row.get(key + "_x"), row.get(key + "_y")
    return (float(x) * scale + left, float(y) * scale) if x and y else None


def near(points, radius):
    """A mask of everything within radius of the points and of the line between them."""
    mask = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(mask)
    pts = [p for p in points if p]
    for p in pts:
        d.ellipse([p[0] - radius, p[1] - radius, p[0] + radius, p[1] + radius], fill=255)
    if len(pts) == 2:
        d.line(pts, fill=255, width=int(radius * 2))
    return mask


def marker_pixels(im):
    """Pixels in the fingertip colours: pink (virtual), grey (real), yellow (the line between)."""
    r, g, b = im.split()
    pink = ImageChops.multiply(ImageChops.multiply(r.point(lambda v: 255 * (v > 170)), g.point(lambda v: 255 * (v < 150))), b.point(lambda v: 255 * (v > 140)))
    spread = ImageChops.difference(r, b)
    grey = ImageChops.multiply(r.point(lambda v: 255 * (v > 120)), ImageChops.multiply(spread.point(lambda v: 255 * (v < 25)), ImageChops.difference(r, g).point(lambda v: 255 * (v < 25))))
    yellow = ImageChops.multiply(ImageChops.multiply(r.point(lambda v: 255 * (v > 170)), g.point(lambda v: 255 * (v > 160))), b.point(lambda v: 255 * (v < 130)))
    return ImageChops.lighter(ImageChops.lighter(pink, grey), yellow)


def label(draw, xy, text, offset, colour):
    dx, dy, side = offset
    f = font(26)
    tx, ty = xy[0] + dx, xy[1] + dy
    anchor = {"l": "lm", "r": "rm", "m": "mm"}[side]
    draw.line([xy, (tx, ty)], fill=(110, 116, 128), width=2)
    box = draw.textbbox((tx, ty), text, font=f, anchor=anchor)
    draw.rectangle([box[0] - 8, box[1] - 5, box[2] + 8, box[3] + 5], fill=(10, 11, 15))
    draw.text((tx, ty), text, font=f, fill=colour, anchor=anchor)


def footer(im, title, note, gain):
    d = ImageDraw.Draw(im)
    d.rectangle([0, H - BAR, W, H], fill=(16, 18, 23))
    d.text((40, H - 66), title, font=font(34), fill=WHITE, anchor="lm")
    d.text((40, H - 26), note, font=font(22), fill=DIM, anchor="lm")
    if gain:
        d.text((W - 40, H - 66), f"gain {gain}", font=font(34), fill=WHITE, anchor="rm")
        d.text((W - 40, H - 26), "fingertips drawn for the demo; hidden in trials", font=font(22), fill=DIM, anchor="rm")


def main():
    rows = list(csv.DictReader(open(ROOT / "frames.csv")))
    by_lab = {}
    for r in rows:
        by_lab.setdefault(r["lab"], []).append(r)

    for lab, title in TITLES.items():
        frames = by_lab[lab]
        stills = {r["still"].split("_", 2)[2]: r for r in frames if r["still"]}
        final = stills.get("contact_object_shown") or stills["contact"]
        start, end = int(stills["home"]["frame"]), int(stills["contact"]["frame"])
        last, scale, left = load(lab, final["frame"])
        base = last.copy()

        # Earlier fingertip positions, faded in, so one picture shows the whole movement.
        # The first frames are skipped: there the fingertip is still half inside the home marker.
        ghosts = [r for r in frames if start + 9 < int(r["frame"]) < end - 3 and (int(r["frame"]) - start) % 5 == 0]
        for i, r in enumerate(ghosts):
            g, _, _ = load(lab, r["frame"])
            alpha = 0.3 + 0.45 * (i + 1) / len(ghosts)
            region = Image.new("L", (W, H), 0)
            for a, b in (("real", "virtual"), ("real2", "virtual2")):
                region = ImageChops.lighter(region, near([point(r, a, scale, left), point(r, b, scale, left)], 60))
            mask = ImageChops.multiply(region, marker_pixels(g)).filter(ImageFilter.GaussianBlur(0.8))
            base.paste(g, (0, 0), mask.point(lambda v: int(v * alpha)))
        # The final fingertips go back on top of the trail.
        region = Image.new("L", (W, H), 0)
        for a, b in (("real", "virtual"), ("real2", "virtual2")):
            region = ImageChops.lighter(region, near([point(final, a, scale, left), point(final, b, scale, left)], 60))
        base.paste(last, (0, 0), ImageChops.multiply(region, marker_pixels(last)))

        d = ImageDraw.Draw(base)
        mid = stills.get("reach") or stills.get("search")
        for key, off in LABELS[lab].items():
            row = mid if key == "virtual" else final
            p = point(row, key, scale, left)
            if p:
                label(d, p, TEXT[key], off, PINK if key == "virtual" else GREY if key.startswith("real") else WHITE)
        footer(base, title, NOTES[lab], final["gain"])
        base.save(OUT / f"{lab}.png")

        # Animation: every second frame at half size.
        seq = [Image.open(ROOT / lab / f"{int(r['frame']):04d}.png").convert("RGB").resize((960, 540), Image.LANCZOS) for r in frames[::2]]
        seq[0].save(OUT / f"{lab}.gif", save_all=True, append_images=seq[1:], duration=66, loop=0, optimize=True)

    panel = [r for r in by_lab["panel"] if r["still"] == "0_panel_gain_set"][0]
    im, _, _ = load("panel", panel["frame"])
    footer(im, "Control panel · poked with a fingertip", "Lab switch, beep origin, hand markers and the gain slider, in every lab.", None)
    im.save(OUT / "panel.png")

    # One sheet with everything, and one video of all labs.
    names = ["panel", "lab1", "lab2", "lab3", "lab4", "lab5"]
    sheet = Image.new("RGB", (W, H * 3 // 2), (0, 0, 0))
    for i, n in enumerate(names):
        tile = Image.open(OUT / f"{n}.png").resize((W // 2, H // 2), Image.LANCZOS)
        sheet.paste(tile, ((i % 2) * W // 2, (i // 2) * H // 2))
    sheet.save(OUT / "all_labs.png")

    # Paths in the list are relative, so a quote in the folder name cannot break it.
    listing = ROOT / "video_frames.txt"
    with open(listing, "w") as f:
        for lab in ["panel"] + list(TITLES):
            for r in by_lab[lab]:
                f.write(f"file '{lab}/{int(r['frame']):04d}.png'\nduration 0.0333\n")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", "video_frames.txt",
                    "-vf", "scale=1920:1080:flags=lanczos,format=yuv420p", "-r", "30", "-c:v", "libx264", "-crf", "18",
                    "pictures/contact_labs_demo.mp4"], cwd=ROOT, check=False)
    listing.unlink(missing_ok=True)


main()
