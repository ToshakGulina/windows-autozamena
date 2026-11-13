#!/usr/bin/env python3
"""Generates src/AutoZamena.App/Assets/app.ico (multi-size 32-bit RGBA Windows icon)."""

import math
import os
import struct
import zlib


def clamp01(x: float) -> float:
    return 0.0 if x < 0.0 else (1.0 if x > 1.0 else x)


def smoothstep(edge0: float, edge1: float, x: float) -> float:
    t = clamp01((x - edge0) / (edge1 - edge0))
    return t * t * (3.0 - 2.0 * t)


def sd_rounded_box(px: float, py: float, bx: float, by: float, r: float) -> float:
    qx = abs(px) - bx + r
    qy = abs(py) - by + r
    out_d = math.hypot(max(qx, 0.0), max(qy, 0.0))
    in_d = min(max(qx, qy), 0.0)
    return out_d + in_d - r


def sd_segment(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    pax, pay = px - ax, py - ay
    bax, bay = bx - ax, by - ay
    denom = bax * bax + bay * bay
    h = 0.0 if denom == 0.0 else clamp01((pax * bax + pay * bay) / denom)
    return math.hypot(pax - bax * h, pay - bay * h)


def render_icon_rgba(size: int) -> bytes:
    """Renders a matte graphite keycap with a clean white 'A' and subtle blue underline."""
    aa = 2.0 / size
    out = bytearray(size * size * 4)

    for y in range(size):
        ny = ((y + 0.5) / size) * 2.0 - 1.0
        for x in range(size):
            nx = ((x + 0.5) / size) * 2.0 - 1.0

            # Outer keycap base skirt
            d_outer = sd_rounded_box(nx, ny, 0.88, 0.88, 0.24)
            alpha = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, d_outer)
            if alpha <= 0.001:
                continue

            # Matte dark base (#1C2026)
            r, g, b = 28.0, 32.0, 38.0

            # Inner raised keycap dish (#2B313B -> #252A33)
            d_cap = sd_rounded_box(nx, ny + 0.04, 0.76, 0.73, 0.18)
            cap_a = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, d_cap)
            if cap_a > 0.0:
                shade = clamp01((ny + 0.8) * 0.6)
                cr = 46.0 * (1.0 - shade) + 37.0 * shade
                cg = 52.0 * (1.0 - shade) + 42.0 * shade
                cb = 62.0 * (1.0 - shade) + 51.0 * shade
                r = r * (1.0 - cap_a) + cr * cap_a
                g = g * (1.0 - cap_a) + cg * cap_a
                b = b * (1.0 - cap_a) + cb * cap_a

            # Subtle 1px top keycap edge highlight
            d_edge = abs(d_cap + 0.015) - 0.012
            edge_a = (1.0 - smoothstep(0.0, aa * 1.2, d_edge)) * clamp01(-ny) * 0.18
            r += (255.0 - r) * edge_a
            g += (255.0 - g) * edge_a
            b += (255.0 - b) * edge_a

            # Clean centered 'A' glyph
            stroke_w = 0.092 if size >= 32 else 0.112
            top_x, top_y = 0.0, -0.45
            left_x, left_y = -0.31, 0.27
            right_x, right_y = 0.31, 0.27
            bar_y = 0.04

            d_l = sd_segment(nx, ny, top_x, top_y, left_x, left_y) - stroke_w
            d_r = sd_segment(nx, ny, top_x, top_y, right_x, right_y) - stroke_w
            d_b = sd_segment(nx, ny, -0.19, bar_y, 0.19, bar_y) - (stroke_w * 0.84)
            glyph_a = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, min(d_l, d_r, d_b))

            r = r * (1.0 - glyph_a) + 244.0 * glyph_a
            g = g * (1.0 - glyph_a) + 246.0 * glyph_a
            b = b * (1.0 - glyph_a) + 249.0 * glyph_a

            # Thin understated indicator bar beneath glyph (#4B9EFF)
            bar_h = 0.046 if size >= 32 else 0.062
            d_uline = sd_rounded_box(nx, ny - 0.49, 0.28, bar_h, bar_h)
            uline_a = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, d_uline)
            if uline_a > 0.0:
                r = r * (1.0 - uline_a) + 75.0 * uline_a
                g = g * (1.0 - uline_a) + 158.0 * uline_a
                b = b * (1.0 - uline_a) + 255.0 * uline_a

            idx = (y * size + x) * 4
            out[idx] = int(round(clamp01(r / 255.0) * 255.0))
            out[idx + 1] = int(round(clamp01(g / 255.0) * 255.0))
            out[idx + 2] = int(round(clamp01(b / 255.0) * 255.0))
            out[idx + 3] = int(round(alpha * 255.0))

    return bytes(out)


def encode_png_rgba(width: int, height: int, rgba: bytes) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        crc = zlib.crc32(tag + data) & 0xFFFFFFFF
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", crc)

    raw_rows = bytearray()
    stride = width * 4
    for y in range(height):
        raw_rows.append(0)
        raw_rows.extend(rgba[y * stride : (y + 1) * stride])

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    idat = zlib.compress(bytes(raw_rows), level=9)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b"")


def build_ico(ico_path: str, preview_png_path: str | None = None) -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    png_blobs: list[tuple[int, bytes]] = []
    for sz in sizes:
        rgba = render_icon_rgba(sz)
        png = encode_png_rgba(sz, sz, rgba)
        png_blobs.append((sz, png))
        if sz == 256 and preview_png_path:
            os.makedirs(os.path.dirname(preview_png_path), exist_ok=True)
            with open(preview_png_path, "wb") as pf:
                pf.write(png)

    os.makedirs(os.path.dirname(ico_path), exist_ok=True)
    header = struct.pack("<HHH", 0, 1, len(png_blobs))
    entries = bytearray()
    offset = 6 + 16 * len(png_blobs)
    payloads = bytearray()

    for sz, blob in png_blobs:
        w_byte = 0 if sz >= 256 else sz
        h_byte = 0 if sz >= 256 else sz
        entries.extend(struct.pack("<BBBBHHII", w_byte, h_byte, 0, 0, 1, 32, len(blob), offset))
        payloads.extend(blob)
        offset += len(blob)

    with open(ico_path, "wb") as f:
        f.write(header)
        f.write(entries)
        f.write(payloads)


if __name__ == "__main__":
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ico_out = os.path.join(repo_root, "src", "AutoZamena.App", "Assets", "app.ico")
    build_ico(ico_out, "/tmp/autozamena_icon_preview.png")
    print(f"Generated {ico_out} ({os.path.getsize(ico_out)} bytes)")
