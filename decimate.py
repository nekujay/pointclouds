import sys, struct
import numpy as np

"""Воксельное прореживание LAS без изменения атрибутов точек.

Работает с любым форматом записи LAS (0,1,2,3,5,...): записи копируются
как есть, читаются только X/Y/Z для сетки. Цвет, интенсивность,
классификация сохраняются.

Использование:  python decimate.py <вход.las> <выход.las> [шаг_в_метрах]
"""


def main():
    src, dst = sys.argv[1], sys.argv[2]
    vox = float(sys.argv[3]) if len(sys.argv) > 3 else 0.02

    with open(src, 'rb') as f:
        h = f.read(400)
    if h[0:4] != b'LASF':
        raise SystemExit('не LAS-файл')

    ver_major, ver_minor = h[24], h[25]
    hdr_size = struct.unpack_from('<H', h, 94)[0]
    offset = struct.unpack_from('<I', h, 96)[0]
    fmt = h[104] & 0x3F
    reclen = struct.unpack_from('<H', h, 105)[0]
    n = struct.unpack_from('<I', h, 107)[0]
    if n == 0 and ver_minor >= 4 and len(h) >= 255:
        n = struct.unpack_from('<Q', h, 247)[0]
    if n == 0:
        raise SystemExit('в файле 0 точек')
    if reclen < 12:
        raise SystemExit('неподдерживаемый размер записи: %d' % reclen)

    scale = struct.unpack_from('<3d', h, 131)
    offs = struct.unpack_from('<3d', h, 155)

    print('точек в файле: %d, формат %d (%d байт)' % (n, fmt, reclen))

    raw = np.memmap(src, dtype='u1', mode='r', offset=offset, shape=(n, reclen))
    xi = raw[:, 0:4].copy().view('<i4').ravel().astype(np.int64)
    yi = raw[:, 4:8].copy().view('<i4').ravel().astype(np.int64)
    zi = raw[:, 8:12].copy().view('<i4').ravel().astype(np.int64)

    step = []
    for axis_scale in scale:
        v = max(1, int(round(vox / axis_scale)))
        step.append(v)

    kx = (xi - xi.min()) // step[0]
    ky = (yi - yi.min()) // step[1]
    kz = (zi - zi.min()) // step[2]
    keys = (kx << 42) | (ky << 21) | kz

    print('сетка %.0f мм, ищу повторы...' % (vox * 1000))
    _, first = np.unique(keys, return_index=True)
    first.sort()
    print('осталось точек: %d (%.1f%%)' % (first.size, 100.0 * first.size / n))

    sel = raw[first]
    xs = xi[first] * scale[0] + offs[0]
    ys = yi[first] * scale[1] + offs[1]
    zs = zi[first] * scale[2] + offs[2]

    nh = bytearray(h[:hdr_size])
    struct.pack_into('<I', nh, 107, first.size)
    struct.pack_into('<I', nh, 111, first.size)
    if ver_minor >= 4 and len(nh) >= 255:
        struct.pack_into('<Q', nh, 247, first.size)
    struct.pack_into('<d', nh, 179, float(xs.max()))
    struct.pack_into('<d', nh, 187, float(xs.min()))
    struct.pack_into('<d', nh, 195, float(ys.max()))
    struct.pack_into('<d', nh, 203, float(ys.min()))
    struct.pack_into('<d', nh, 211, float(zs.max()))
    struct.pack_into('<d', nh, 219, float(zs.min()))

    with open(dst, 'wb') as f:
        f.write(bytes(nh))
        if offset > len(nh):
            f.write(b'\x00' * (offset - len(nh)))
        f.write(sel.tobytes())

    print('записано: %s' % dst)


if __name__ == '__main__':
    main()
