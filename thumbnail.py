import sys, struct, zlib
import numpy as np

"""Превью облака точек из LAS в PNG (изометрическая проекция).

Без внешних зависимостей: только numpy + zlib (встроенный).
Цвет берётся из RGB точек, если он есть в формате; иначе — по высоте.

Использование:  python thumbnail.py <вход.las> <выход.png> [ширина] [высота]
"""

W_DEFAULT = 640
H_DEFAULT = 400
MAX_POINTS = 400000

# смещение RGB в записи точки по формату LAS
RGB_OFFSET = {2: 20, 3: 28, 5: 28, 7: 30, 8: 30, 10: 30}


def write_png(path, w, h, rgb):
    raw = bytearray()
    stride = w * 3
    for y in range(h):
        raw.append(0)
        raw += rgb[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))

    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)))
        f.write(chunk(b'IDAT', zlib.compress(bytes(raw), 6)))
        f.write(chunk(b'IEND', b''))


def read_las(path):
    with open(path, 'rb') as f:
        head = f.read(400)
    if head[0:4] != b'LASF':
        raise SystemExit('не LAS-файл')
    ver_minor = head[25]
    hdr_size = struct.unpack_from('<H', head, 94)[0]
    offset = struct.unpack_from('<I', head, 96)[0]
    fmt = head[104] & 0x3F
    reclen = struct.unpack_from('<H', head, 105)[0]
    n = struct.unpack_from('<I', head, 107)[0]
    if n == 0 and ver_minor >= 4 and len(head) >= 255:
        n = struct.unpack_from('<Q', head, 247)[0]
    if n == 0:
        raise SystemExit('в файле 0 точек')
    scale = struct.unpack_from('<3d', head, 131)
    offs = struct.unpack_from('<3d', head, 155)
    return offset, fmt, reclen, n, scale, offs


def palette(t):
    """t: 0..1 -> RGB по градиенту синий-циан-зелёный-жёлтый-красный."""
    stops = [(0.0, (40, 70, 170)), (0.25, (30, 170, 200)), (0.5, (60, 180, 90)),
             (0.75, (230, 200, 70)), (1.0, (225, 70, 60))]
    t = min(max(t, 0.0), 1.0)
    for i in range(len(stops) - 1):
        a, ca = stops[i]
        b, cb = stops[i + 1]
        if a <= t <= b:
            k = (t - a) / (b - a) if b > a else 0.0
            return tuple(int(ca[j] + (cb[j] - ca[j]) * k) for j in range(3))
    return stops[-1][1]


def render(src, dst, w=W_DEFAULT, h=H_DEFAULT):
    offset, fmt, reclen, n, scale, offs = read_las(src)
    stride = max(1, n // MAX_POINTS)

    raw = np.memmap(src, dtype='u1', mode='r', offset=offset, shape=(n, reclen))
    part = raw[::stride]
    xi = part[:, 0:4].copy().view('<i4').ravel().astype(np.float64)
    yi = part[:, 4:8].copy().view('<i4').ravel().astype(np.float64)
    zi = part[:, 8:12].copy().view('<i4').ravel().astype(np.float64)

    x = xi * scale[0] + offs[0]
    y = yi * scale[1] + offs[1]
    z = zi * scale[2] + offs[2]

    colors = None
    ro = RGB_OFFSET.get(fmt)
    if ro is not None and ro + 6 <= reclen:
        try:
            rgb = part[:, ro:ro + 6].copy().view('<u2').reshape(-1, 3).astype(np.float64)
            mx = float(rgb.max()) if rgb.size else 0.0
            if mx > 255.0:
                rgb = rgb / 257.0
            colors = np.clip(rgb, 0, 255).astype(np.uint8)
        except Exception:
            colors = None

    cx = (x.min() + x.max()) / 2.0
    cy = (y.min() + y.max()) / 2.0
    cz = (z.min() + z.max()) / 2.0
    x = x - cx
    y = y - cy
    z = z - cz

    az = np.radians(35.0)
    el = np.radians(28.0)
    sx = x * np.cos(az) + y * np.sin(az)
    sy = -x * np.sin(az) * np.sin(el) + y * np.cos(az) * np.sin(el) - z * np.cos(el)
    depth = -x * np.sin(az) * np.cos(el) + y * np.cos(az) * np.cos(el) + z * np.sin(el)

    span_x = max(float(sx.max() - sx.min()), 1e-9)
    span_y = max(float(sy.max() - sy.min()), 1e-9)
    pad = 0.04
    k = min((w * (1 - 2 * pad)) / span_x, (h * (1 - 2 * pad)) / span_y)
    px = ((sx - (sx.min() + sx.max()) / 2.0) * k + w / 2.0)
    py = (h / 2.0 - (sy - (sy.min() + sy.max()) / 2.0) * k)
    px = np.clip(px.astype(np.int32), 0, w - 1)
    py = np.clip(py.astype(np.int32), 0, h - 1)

    if colors is None:
        zt = (z - z.min()) / max(float(z.max() - z.min()), 1e-9)
        lut = np.array([palette(float(t)) for t in np.linspace(0, 1, 256)], dtype=np.uint8)
        colors = lut[np.clip((zt * 255).astype(np.int32), 0, 255)]

    order = np.argsort(depth)
    px = px[order]
    py = py[order]
    colors = colors[order]

    img = np.zeros((h, w, 3), dtype=np.uint8)
    img[:, :, :] = (14, 20, 28)
    # точки рисуем блоками 2x2, чтобы не было дырок
    for dx in (0, 1):
        for dy in (0, 1):
            xx = np.clip(px + dx, 0, w - 1)
            yy = np.clip(py + dy, 0, h - 1)
            img[yy, xx] = colors

    write_png(dst, w, h, img.tobytes())
    return len(px)


def main():
    if len(sys.argv) < 3:
        raise SystemExit('использование: thumbnail.py <вход.las> <выход.png> [ширина] [высота]')
    src, dst = sys.argv[1], sys.argv[2]
    w = int(sys.argv[3]) if len(sys.argv) > 3 else W_DEFAULT
    h = int(sys.argv[4]) if len(sys.argv) > 4 else H_DEFAULT
    used = render(src, dst, w, h)
    print('превью записано: %s (%d точек)' % (dst, used))


if __name__ == '__main__':
    main()
