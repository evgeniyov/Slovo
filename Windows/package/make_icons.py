#!/usr/bin/env python3
"""Draws the Windows icons (app and tray) the way Scripts/make_icon.swift draws the macOS one.

Standard library only; run it after changing the design:
    python3 Windows/package/make_icons.py
"""
import math
import struct
import zlib
from pathlib import Path

ASSETS = Path(__file__).resolve().parent.parent / "src" / "Slovo.App" / "Assets"


def hex_rgb(value):
    return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)


def rounded_rect_sd(px, py, cx, cy, hw, hh, r):
    """Signed distance from a point to a rounded rectangle (negative inside)."""
    qx = abs(px - cx) - (hw - r)
    qy = abs(py - cy) - (hh - r)
    outside = math.hypot(max(qx, 0), max(qy, 0))
    inside = min(max(qx, qy), 0)
    return outside + inside - r


def blend(dst, src, alpha):
    return tuple(d + (s - d) * alpha for d, s in zip(dst, src))


def segment_distance(px, py, ax, ay, bx, by):
    """Distance from a point to the segment from (ax, ay) to (bx, by)."""
    vx, vy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * vx + (py - ay) * vy) / (vx * vx + vy * vy)))
    return math.hypot(px - (ax + vx * t), py - (ay + vy * t))


def mark_coverage(mark, sx, sy, dx, dy, r):
    """How much of a pixel the glyph inside the dot covers: a slash (microphone off) or "!" (microphone lost)."""
    if mark == "slash":
        # The macOS mic.slash direction: top left to bottom right.
        half_width = max(r * 0.13, 0.55)
        return min(1, max(0, 0.5 - (segment_distance(sx, sy, dx - r * 0.55, dy - r * 0.55, dx + r * 0.55, dy + r * 0.55) - half_width)))
    if mark == "exclamation":
        half_width = max(r * 0.14, 0.55)
        bar = min(1, max(0, 0.5 - rounded_rect_sd(sx, sy, dx, dy - r * 0.2, half_width, r * 0.38, half_width)))
        point = min(1, max(0, 0.5 - (math.hypot(sx - dx, sy - (dy + r * 0.52)) - half_width * 1.1)))
        return max(bar, point)
    return 0


def draw(size, tile_colors, bar_color, accent_bar_color, dot=None):
    """
    RGBA rows: a rounded tile with a vertical gradient, seven bars, an optional dot.
    The dot is (color, radius) or (color, radius, mark, mark color) with a glyph inside.
    """
    inset = size * (0.098 if size >= 48 else 0.04)
    tile_half = (size - inset * 2) / 2
    radius = tile_half * 2 * 0.225
    center = size / 2
    heights = [0.20, 0.36, 0.54, 0.70, 0.54, 0.36, 0.20] if size >= 32 else [0.30, 0.55, 0.78, 0.55, 0.30]
    count = len(heights)
    bar_width = tile_half * 2 * (0.074 if count == 7 else 0.12)
    gap = tile_half * 2 * (0.046 if count == 7 else 0.07)
    total = count * bar_width + (count - 1) * gap
    bars = []
    x = center - total / 2
    for index, fraction in enumerate(heights):
        height = tile_half * 2 * fraction
        bars.append((x + bar_width / 2, center, bar_width / 2, height / 2, index == count // 2))
        x += bar_width + gap

    rows = []
    top, bottom = hex_rgb(tile_colors[0]), hex_rgb(tile_colors[1])
    for py in range(size):
        row = bytearray()
        for px in range(size):
            sx, sy = px + 0.5, py + 0.5
            color = (0.0, 0.0, 0.0)
            alpha = 0.0
            tile_cov = min(1, max(0, 0.5 - rounded_rect_sd(sx, sy, center, center, tile_half, tile_half, radius)))
            if tile_cov > 0:
                t = (sx + (size - sy)) / (2 * size)
                color = blend(top, bottom, t)
                alpha = tile_cov
            for bx, by, bhw, bhh, is_accent in bars:
                cov = min(1, max(0, 0.5 - rounded_rect_sd(sx, sy, bx, by, bhw, bhh, bhw)))
                if cov > 0:
                    color = blend(color, hex_rgb(accent_bar_color if is_accent else bar_color), cov)
            if dot is not None:
                dot_color, dot_radius = dot[0], dot[1]
                dx, dy = size * 0.78, size * 0.22
                cov = min(1, max(0, 0.5 - (math.hypot(sx - dx, sy - dy) - size * dot_radius)))
                if cov > 0:
                    ring = min(1, max(0, 0.5 - (math.hypot(sx - dx, sy - dy) - size * (dot_radius + 0.06))))
                    color = blend(color, (255, 255, 255), ring)
                    alpha = max(alpha, ring)
                    color = blend(color, hex_rgb(dot_color), cov)
                    if len(dot) > 2:
                        mark, mark_color = dot[2], dot[3]
                        glyph = mark_coverage(mark, sx, sy, dx, dy, size * dot_radius) * cov
                        if glyph > 0:
                            color = blend(color, hex_rgb(mark_color), glyph)
            row += bytes([round(color[0]), round(color[1]), round(color[2]), round(alpha * 255)])
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + row for row in rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def ico(path, images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path.write_bytes(header + entries + b"".join(data for _, data in images))


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    warm = (0x2A231F, 0x4A3A30)
    variants = {
        "slovo.ico": ([16, 20, 24, 32, 48, 64, 256], warm, 0xF2EDE4, 0xF4A259, None),
        "tray-idle.ico": ([16, 20, 24, 32], warm, 0xF2EDE4, 0xF4A259, None),
        "tray-recording.ico": ([16, 20, 24, 32], (0x7C5CFA, 0x6A48F0), 0xFFFFFF, 0xFFFFFF, None),
        "tray-meeting.ico": ([16, 20, 24, 32], warm, 0xF2EDE4, 0xF4A259, (0xE5484D, 0.16)),
        # A meeting recording with the microphone switched off (macOS mic.slash.circle) or
        # gone (exclamationmark.circle): the dot changes colour and carries a glyph.
        "tray-meeting-microphone-off.ico": ([16, 20, 24, 32], warm, 0xF2EDE4, 0xF4A259, (0x8A8A8E, 0.2, "slash", 0xFFFFFF)),
        "tray-meeting-microphone-lost.ico": ([16, 20, 24, 32], warm, 0xF2EDE4, 0xF4A259, (0xF5A524, 0.2, "exclamation", 0x2A231F)),
    }
    for name, (sizes, tile, bar, accent, dot) in variants.items():
        images = [(size, png(size, draw(size, tile, bar, accent, dot))) for size in sizes]
        ico(ASSETS / name, images)
        print("wrote", ASSETS / name)
    (ASSETS / "slovo-256.png").write_bytes(png(256, draw(256, warm, 0xF2EDE4, 0xF4A259)))


if __name__ == "__main__":
    main()
