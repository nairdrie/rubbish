#!/usr/bin/env python3
"""Draws Rubbish's placeholder app icon: a derpy trash bag with googly eyes and a tongue.

Pure Python (no Pillow): shapes are signed distance fields with analytic anti-aliasing, written as an RGBA PNG.
Usage: python3 Tools/playtest/make_icon.py [out.png] [size]
Default output: Assets/Art/Icon/RubbishIcon.png at 1024 px (Unity scales it down for the Windows .ico).
"""
import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.join(HERE, "..", "..", "Assets", "Art", "Icon", "RubbishIcon.png")


# ----------------------------------------------------------------------------- SDF helpers (unit square: 0..1)

def length(x, y):
    return math.sqrt(x * x + y * y)


def circle(cx, cy, r):
    return lambda x, y: length(x - cx, y - cy) - r


def ellipse(cx, cy, rx, ry, rot=0.0):
    c, s = math.cos(rot), math.sin(rot)

    def f(x, y):
        dx, dy = x - cx, y - cy
        u, v = dx * c + dy * s, -dx * s + dy * c
        k = length(u / rx, v / ry)
        return (k - 1.0) * min(rx, ry)
    return f


def rbox(cx, cy, hx, hy, r):
    def f(x, y):
        qx, qy = abs(x - cx) - hx + r, abs(y - cy) - hy + r
        return length(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - r
    return f


def smin(a, b, k):
    h = max(k - abs(a - b), 0.0) / k
    return min(a, b) - h * h * k * 0.25


def union_smooth(k, *fs):
    def f(x, y):
        d = fs[0](x, y)
        for g in fs[1:]:
            d = smin(d, g(x, y), k)
        return d
    return f


def intersect(a, b):
    return lambda x, y: max(a(x, y), b(x, y))


def subtract(a, b):
    return lambda x, y: max(a(x, y), -b(x, y))


def arc(cx, cy, r, a0, a1, w):
    """A stroke along a circle from angle a0 to a1 (radians, y down), half-width w, round caps."""
    def f(x, y):
        ang = math.atan2(y - cy, x - cx)
        lo, hi = min(a0, a1), max(a0, a1)
        if lo <= ang <= hi:
            return abs(length(x - cx, y - cy) - r) - w
        p0 = (cx + r * math.cos(a0), cy + r * math.sin(a0))
        p1 = (cx + r * math.cos(a1), cy + r * math.sin(a1))
        return min(length(x - p0[0], y - p0[1]), length(x - p1[0], y - p1[1])) - w
    return f


def wavy(cx, y0, y1, amp, freq, w):
    """A vertical wavy stroke x = cx + amp*sin(freq*(y-y0)) from y0 to y1, half-width w (stink lines)."""
    def f(x, y):
        yc = min(max(y, y0), y1)
        fx = cx + amp * math.sin(freq * (yc - y0))
        slope = amp * freq * math.cos(freq * (yc - y0))
        dx = (x - fx) / math.sqrt(1 + slope * slope)
        return length(dx, y - yc) - w
    return f


def hexcol(h, a=1.0):
    h = h.lstrip("#")
    return (int(h[0:2], 16) / 255.0, int(h[2:4], 16) / 255.0, int(h[4:6], 16) / 255.0, a)


# ----------------------------------------------------------------------------- the drawing

INK = hexcol("1B1F1C")


def layers():
    """(sdf, colour or colour(x, y), bbox (x0, y0, x1, y1) in unit space, outline width) from back to front."""
    L = []

    # Badge: warm yellow rounded square with a soft radial glow.
    def badge_col(x, y):
        t = min(1.0, length(x - 0.42, y - 0.36) / 0.75)
        a, b = hexcol("FFE066"), hexcol("FFB627")
        return tuple(a[i] + (b[i] - a[i]) * t for i in range(3)) + (1.0,)
    L.append((rbox(0.5, 0.5, 0.47, 0.47, 0.16), badge_col, (0.0, 0.0, 1.0, 1.0), 0.018))

    # Little curb shadow under the bag.
    L.append((ellipse(0.5, 0.875, 0.30, 0.045), hexcol("C97E10", 0.55), (0.15, 0.80, 0.85, 0.95), 0.0))

    # The bag: a lumpy blob (smooth union of circles) with two knot ears on top.
    body = union_smooth(0.10,
                        circle(0.50, 0.62, 0.27),
                        circle(0.33, 0.70, 0.17),
                        circle(0.68, 0.69, 0.18),
                        ellipse(0.50, 0.46, 0.20, 0.17),
                        ellipse(0.50, 0.32, 0.07, 0.06))

    def bag_col(x, y):
        t = max(0.0, min(1.0, (y - 0.25) / 0.65))
        a, b = hexcol("4F6B55"), hexcol("26352B")
        return tuple(a[i] + (b[i] - a[i]) * t for i in range(3)) + (1.0,)

    # Knot flaps: two floppy ears sticking up out of the tie.
    ear_l = union_smooth(0.04, ellipse(0.405, 0.185, 0.10, 0.045, rot=-0.85), circle(0.36, 0.13, 0.04))
    ear_r = union_smooth(0.04, ellipse(0.615, 0.175, 0.11, 0.045, rot=0.65), circle(0.675, 0.12, 0.042))
    L.append((ear_l, hexcol("466150"), (0.26, 0.05, 0.53, 0.30), 0.016))
    L.append((ear_r, hexcol("466150"), (0.47, 0.04, 0.78, 0.29), 0.016))
    L.append((body, bag_col, (0.10, 0.22, 0.92, 0.92), 0.020))
    # Knot band.
    # Red twist tie round the neck.
    L.append((rbox(0.505, 0.275, 0.075, 0.022, 0.018), hexcol("E8433A"), (0.40, 0.22, 0.61, 0.33), 0.012))
    L.append((ellipse(0.585, 0.30, 0.014, 0.04, rot=-0.5), hexcol("E8433A"), (0.54, 0.24, 0.63, 0.36), 0.010))

    # Shine: two glossy streaks on the left.
    L.append((arc(0.52, 0.60, 0.25, math.radians(158), math.radians(200), 0.016), hexcol("FFFFFF", 0.55),
              (0.20, 0.45, 0.40, 0.75), 0.0))
    L.append((circle(0.285, 0.47, 0.016), hexcol("FFFFFF", 0.55), (0.25, 0.43, 0.32, 0.51), 0.0))

    # Googly eyes, mismatched sizes, pupils looking in different directions.
    L.append((circle(0.40, 0.50, 0.105), hexcol("FFFFFF"), (0.27, 0.37, 0.53, 0.63), 0.016))
    L.append((circle(0.625, 0.47, 0.078), hexcol("FFFFFF"), (0.53, 0.37, 0.72, 0.57), 0.016))
    L.append((circle(0.43, 0.545, 0.048), INK, (0.37, 0.48, 0.49, 0.61), 0.0))
    L.append((circle(0.605, 0.44, 0.036), INK, (0.56, 0.39, 0.65, 0.49), 0.0))
    L.append((circle(0.445, 0.528, 0.014), hexcol("FFFFFF"), (0.42, 0.50, 0.47, 0.55), 0.0))
    L.append((circle(0.615, 0.428, 0.011), hexcol("FFFFFF"), (0.59, 0.41, 0.64, 0.45), 0.0))

    # Goofy open grin with a buck tooth and a tongue flopping out to the right.
    L.append((intersect(ellipse(0.53, 0.645, 0.135, 0.095), lambda x, y: 0.645 - y), hexcol("5A1A22"),
              (0.38, 0.62, 0.68, 0.76), 0.016))
    tongue = union_smooth(0.03, ellipse(0.585, 0.715, 0.055, 0.06, rot=0.35), circle(0.56, 0.69, 0.045))
    L.append((tongue, hexcol("FF7A93"), (0.49, 0.64, 0.66, 0.80), 0.013))
    L.append((ellipse(0.588, 0.72, 0.006, 0.03, rot=0.35), hexcol("D9506B"), (0.56, 0.68, 0.62, 0.76), 0.0))
    L.append((rbox(0.49, 0.665, 0.026, 0.026, 0.008), hexcol("FFFFFF"), (0.45, 0.64, 0.53, 0.70), 0.010))
    # Mouth outline top edge (covers the tongue root).
    L.append((lambda x, y: max(abs(y - 0.645) - 0.008, abs(x - 0.53) - 0.13), INK, (0.39, 0.63, 0.67, 0.66), 0.0))

    # A fly buzzing about, plus a stink squiggle.
    L.append((ellipse(0.80, 0.235, 0.032, 0.022, rot=-0.6), hexcol("BFE6FF", 0.9), (0.75, 0.19, 0.85, 0.28), 0.008))
    L.append((ellipse(0.835, 0.225, 0.030, 0.020, rot=0.6), hexcol("BFE6FF", 0.9), (0.79, 0.18, 0.88, 0.27), 0.008))
    L.append((circle(0.815, 0.265, 0.024), INK, (0.78, 0.23, 0.85, 0.30), 0.0))
    for cx, y0, y1 in ((0.135, 0.30, 0.47), (0.195, 0.22, 0.40), (0.255, 0.27, 0.39)):
        L.append((wavy(cx, y0, y1, 0.018, 20.0, 0.013), hexcol("6FA34E"), (cx - 0.05, y0 - 0.03, cx + 0.05, y1 + 0.03), 0.0))
    return L


def render(size):
    px = 1.0 / size
    buf = [[0.0, 0.0, 0.0, 0.0] for _ in range(size * size)]
    for sdf, col, (x0, y0, x1, y1), outline in layers():
        pad = outline + 2 * px
        i0, i1 = max(0, int((x0 - pad) * size)), min(size, int((x1 + pad) * size) + 1)
        j0, j1 = max(0, int((y0 - pad) * size)), min(size, int((y1 + pad) * size) + 1)
        for j in range(j0, j1):
            y = (j + 0.5) * px
            row = j * size
            for i in range(i0, i1):
                x = (i + 0.5) * px
                d = sdf(x, y)
                if d - outline > px:
                    continue
                cov_fill = min(1.0, max(0.0, 0.5 - d / px))
                c = col(x, y) if callable(col) else col
                if outline > 0:
                    cov_out = min(1.0, max(0.0, 0.5 - (d - outline) / px))
                    blend(buf[row + i], INK[0], INK[1], INK[2], cov_out * c[3])
                if cov_fill > 0:
                    blend(buf[row + i], c[0], c[1], c[2], cov_fill * c[3])
    return buf


def blend(p, r, g, b, a):
    if a <= 0:
        return
    out_a = a + p[3] * (1 - a)
    if out_a <= 0:
        return
    for k, v in enumerate((r, g, b)):
        p[k] = (v * a + p[k] * p[3] * (1 - a)) / out_a
    p[3] = out_a


def write_png(path, size, buf):
    raw = bytearray()
    for j in range(size):
        raw.append(0)
        for i in range(size):
            p = buf[j * size + i]
            raw += bytes(int(round(max(0.0, min(1.0, v)) * 255)) for v in p)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT
    size = int(sys.argv[2]) if len(sys.argv) > 2 else 1024
    write_png(out, size, render(size))
    print(f"wrote {os.path.abspath(out)} ({size}x{size})")
