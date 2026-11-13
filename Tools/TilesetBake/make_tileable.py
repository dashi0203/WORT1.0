"""把一张原色图做成四方连续：
1) 去掉低频明暗渐变（逐行 / 逐列平均亮度用大半径平滑后拉平），解决“越往下越深、一层一层”的问题；
2) 周期 + 平滑分解（Moisan 2011）去掉对边不连续，只改低频、几乎不动细节。
用法：python make_tileable.py 输入.png 输出.png [平滑半径px=96] [拉平强度0..1=0.85]
"""
import sys, numpy as np
from PIL import Image

def blur1d(a, r, axis):
    k = np.exp(-0.5 * (np.arange(-3 * r, 3 * r + 1) / r) ** 2); k /= k.sum()
    n = a.shape[axis]; pad = 3 * r
    ap = np.concatenate([np.take(a, range(n - pad, n), axis), a, np.take(a, range(pad), axis)], axis)   # 周期
    return np.apply_along_axis(lambda v: np.convolve(v, k, 'valid'), axis, ap)

def periodic_part(u):
    M, N = u.shape; v = np.zeros_like(u)
    v[0] += u[-1] - u[0]; v[-1] += u[0] - u[-1]; v[:, 0] += u[:, -1] - u[:, 0]; v[:, -1] += u[:, 0] - u[:, -1]
    q = np.arange(M)[:, None]; r = np.arange(N)[None, :]
    d = 2 * np.cos(2 * np.pi * q / M) + 2 * np.cos(2 * np.pi * r / N) - 4; d[0, 0] = 1
    s = np.fft.fft2(v) / d; s[0, 0] = 0
    return u - np.real(np.fft.ifft2(s))

src, dst = sys.argv[1], sys.argv[2]
R = int(sys.argv[3]) if len(sys.argv) > 3 else 96
K = float(sys.argv[4]) if len(sys.argv) > 4 else 0.85
a = np.asarray(Image.open(src).convert('RGB'), float) / 255
lum = a @ [0.2126, 0.7152, 0.0722]; m = lum.mean()
for axis in ((0, 1) if K > 0 else ()):   # 0: 逐行（纵向渐变）  1: 逐列（横向渐变）；K = 0 只做四方连续
    prof = lum.mean(axis=1 - axis)
    prof = blur1d(prof, R, 0)
    gain = (m / np.maximum(prof, 1e-3)) ** K
    a = a * (gain[:, None, None] if axis == 0 else gain[None, :, None])
    lum = a @ [0.2126, 0.7152, 0.0722]
a = np.stack([periodic_part(a[..., c]) for c in range(3)], -1)
Image.fromarray(np.uint8(np.clip(np.rint(a * 255), 0, 255))).save(dst)
e = lambda b: (np.abs(b[:, 0] - b[:, -1]).mean() * 255, np.abs(b[0] - b[-1]).mean() * 255)
print('对边差 L/R %.1f T/B %.1f' % e(a))
