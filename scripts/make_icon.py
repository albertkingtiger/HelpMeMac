"""Draw the Help Me! icon: the PNG the app loads, and the .icns the bundle uses.

The icon is a vector in Assets/helpme.svg. This renders the same shapes rather
than tracing the SVG, so there is no dependency on a rasteriser: macOS has sips
and iconutil, but the package has to be buildable from the machine the code is
written on too.
"""
import argparse
import math
import pathlib
import struct
import zlib

BACKGROUND = (0x0B, 0x12, 0x20)
BUBBLE = (0x1E, 0x9C, 0xBE)
SPARKLE = (0xA7, 0xF3, 0xD0)

# The master is rendered once at this many samples across the 256-unit artboard
# and every size is averaged down from it; rendering each size separately would
# cost minutes in pure Python for no visible gain.
MASTER = 2048


def rounded(x, y, x0, y0, x1, y1, radius):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + radius), x1 - radius)
    cy = min(max(y, y0 + radius), y1 - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius


def triangle(x, y, p0, p1, p2):
    def side(a, b):
        return (b[0] - a[0]) * (y - a[1]) - (b[1] - a[1]) * (x - a[0])
    d0, d1, d2 = side(p0, p1), side(p1, p2), side(p2, p0)
    return not ((d0 < 0 or d1 < 0 or d2 < 0) and (d0 > 0 or d1 > 0 or d2 > 0))


def sparkle(x, y):
    # A four-pointed star: the square root makes the sides concave, which is
    # what gives it the drawn-in waist rather than a plain diamond.
    dx, dy = abs(x - 128) / 31.0, abs(y - 98) / 31.0
    if dx > 1 or dy > 1:
        return False
    return math.sqrt(dx) + math.sqrt(dy) <= 1.0


def render_master():
    """RGBA rows at MASTER x MASTER, one byte per channel."""
    rows = []
    scale = 256.0 / MASTER
    for py in range(MASTER):
        y = (py + 0.5) * scale
        row = bytearray()
        for px in range(MASTER):
            x = (px + 0.5) * scale
            if not rounded(x, y, 12, 12, 244, 244, 42):
                row += b"\0\0\0\0"
                continue
            colour = BACKGROUND
            if rounded(x, y, 55, 44, 201, 165, 22) or triangle(x, y, (90, 165), (90, 193), (124, 165)):
                colour = BUBBLE
            if sparkle(x, y):
                colour = SPARKLE
            row += bytes((colour[0], colour[1], colour[2], 255))
        rows.append(bytes(row))
    return rows


def downsample(master, size):
    """Area-average the master down to size x size, as RGBA rows."""
    block = MASTER // size
    out = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            r = g = b = a = 0
            for sy in range(block):
                line = master[y * block + sy]
                base = (x * block) * 4
                for sx in range(block):
                    offset = base + sx * 4
                    alpha = line[offset + 3]
                    if alpha:
                        r += line[offset]
                        g += line[offset + 1]
                        b += line[offset + 2]
                        a += alpha
            samples = block * block
            covered = a // 255
            if covered == 0:
                row += b"\0\0\0\0"
            else:
                row += bytes((r // covered, g // covered, b // covered, a // samples))
        out.append(bytes(row))
    return out


def png(rows, size):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)
    raw = b"".join(b"\0" + row for row in rows)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


# Every entry in a modern .icns may be a PNG, which is what makes writing one
# without iconutil reasonable. The types are the sizes Finder and the Dock ask
# for, including the @2x variants.
ICNS_TYPES = [
    (b"icp4", 16), (b"icp5", 32), (b"icp6", 64),
    (b"ic07", 128), (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024),
    (b"ic11", 32), (b"ic12", 64), (b"ic13", 256), (b"ic14", 512),
]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--png", help="write the application PNG here")
    parser.add_argument("--png-size", type=int, default=256)
    parser.add_argument("--icns", help="write the bundle icon here")
    arguments = parser.parse_args()
    if not arguments.png and not arguments.icns:
        parser.error("Nothing to write: pass --png, --icns, or both.")

    master = render_master()
    cache = {}

    def sized(size):
        if size not in cache:
            cache[size] = png(downsample(master, size), size)
        return cache[size]

    if arguments.png:
        path = pathlib.Path(arguments.png)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(sized(arguments.png_size))
        print(f"Wrote {path} ({arguments.png_size}x{arguments.png_size})")

    if arguments.icns:
        entries = b""
        for tag, size in ICNS_TYPES:
            data = sized(size)
            entries += tag + struct.pack(">I", len(data) + 8) + data
        path = pathlib.Path(arguments.icns)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"icns" + struct.pack(">I", len(entries) + 8) + entries)
        print(f"Wrote {path} ({len(ICNS_TYPES)} representations)")


if __name__ == "__main__":
    main()
