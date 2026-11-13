#!/usr/bin/env python3
"""Generates src/AutoZamena.App/Assets/app.ico (multi-resolution 32-bit RGBA Windows icon)."""

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
    """Renders the AutoZamena emerald/cyan squircle + crisp 'A' + subtle spark badge in [-1, 1] coords."""
    aa = 2.0 / size
    out = bytearray(size * size * 4)

    for y in range(size):
        ny = ((y + 0.5) / size) * 2.0 - 1.0
        for x in range(size):
            nx = ((x + 0.5) / size) * 2.0 - 1.0

            # 1. Outer rounded squircle
            d_box = sd_rounded_box(nx, ny, 0.88, 0.88, 0.40)
            alpha_box = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, d_box)

            if alpha_box <= 0.001:
                continue

            # Diagonal rich emerald -> deep teal gradient
            grad_t = clamp01((nx * 0.55 + ny * 0.85 + 1.0) * 0.5)
            r = 18.0 * (1.0 - grad_t) + 9.0 * grad_t
            g = 184.0 * (1.0 - grad_t) + 118.0 * grad_t
            b = 118.0 * (1.0 - grad_t) + 109.0 * grad_t

            # Subtle top inner highlight rim
            d_inner = abs(d_box + 0.035) - 0.015
            rim = (1.0 - smoothstep(0.0, aa * 1.4, d_inner)) * clamp01((-ny + 0.3) * 0.7) * 0.28
            r += (255.0 - r) * rim
            g += (255.0 - g) * rim
            b += (255.0 - b) * rim

            # 2. Bold geometric letter 'А' (centered slightly left-up to balance corner pill)
            stroke_w = 0.112 if size >= 32 else 0.128
            ax_top, ay_top = -0.02, -0.46
            ax_l, ay_l = -0.39, 0.43
            ax_r, ay_r = 0.35, 0.43
            bar_y = 0.13

            d_leg_l = sd_segment(nx, ny, ax_top, ay_top, ax_l, ay_l) - stroke_w
            d_leg_r = sd_segment(nx, ny, ax_top, ay_top, ax_r, ay_r) - stroke_w
            d_cross = sd_segment(nx, ny, -0.24, bar_y, 0.20, bar_y) - (stroke_w * 0.84)
            d_letter = min(d_leg_l, d_leg_r, d_cross)

            # Soft drop shadow under 'A'
            d_leg_ls = sd_segment(nx, ny - 0.045, ax_top, ay_top, ax_l, ay_l) - stroke_w
            d_leg_rs = sd_segment(nx, ny - 0.045, ax_top, ay_top, ax_r, ay_r) - stroke_w
            d_cross_s = sd_segment(nx, ny - 0.045, -0.24, bar_y, 0.20, bar_y) - (stroke_w * 0.84)
            shadow_a = (1.0 - smoothstep(-aa, aa * 2.4, min(d_leg_ls, d_leg_rs, d_cross_s))) * 0.24
            r *= (1.0 - shadow_a)
            g *= (1.0 - shadow_a)
            b *= (1.0 - shadow_a)

            letter_a = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, d_letter)
            r = r * (1.0 - letter_a) + 255.0 * letter_a
            g = g * (1.0 - letter_a) + 255.0 * letter_a
            b = b * (1.0 - letter_a) + 255.0 * letter_a

            # 3. Crisp electric-sky accent dot / lightning spark in top-right (RU <-> EN auto-switch cue)
            dx_sp, dy_sp = nx - 0.45, ny - (-0.45)
            d_spark = math.hypot(dx_sp, dy_sp) - (0.145 if size >= 32 else 0.165)
            spark_a = 1.0 - smoothstep(-aa * 0.85, aa * 0.85, d_spark)
            if spark_a > 0.0:
                r = r * (1.0 - spark_a) + 125.0 * spark_a
                g = g * (1.0 - spark_a) + 249.0 * spark_a
                b = b * (1.0 - spark_a) + 255.0 * spark_a

            idx = (y * size + x) * 4
            out[idx] = int(round(clamp01(r / 255.0) * 255.0))
            out[idx + 1] = int(round(clamp01(g / 255.0) * 255.0))
            out[idx + 2] = int(round(clamp01(b / 255.0) * 255.0))
            out[idx + 3] = int(round(alpha_box * 255.0))

    return bytes(out)


def encode_png_rgba(width: int, height: int, rgba: bytes) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        crc = zlib.crc32(tag + data) & 0xFFFFFFFF
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", crc)

    raw_rows = bytearray()
    stride = width * 4
    for y in range(height):
        raw_rows.append(0)  # Filter type 0 (None)
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
    preview_tmp = "/tmp/autozamena_icon_preview.png"
    build_ico(ico_out, preview_tmp)
    print(f"Generated {ico_out} ({os.path.getsize(ico_out)} bytes)")
