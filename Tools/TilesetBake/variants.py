"""崖壁变体处理（负责人 2026-11-13）：TilesetExperiment/Variants_GPT/<套>/<尺寸>_v<n>.png → processed/<尺寸>_v<n>_BaseColor.png、_Normal.png。

每张：取中间挖空区域 → 颜色均值 / 方差对齐基础块 → 外圈 BAND px 渐变成基础块（和任何邻块无缝）→
按 67% 流程算高度（周期）→ 周期域贪心插点三角化求面法线 → 法线外圈同样渐变成基础块法线。
最后输出一张 8×3 格的墙（基础块 + 变体混铺）和只有基础块的墙并排对比：processed/preview.png。

用法：python variants.py [套名…]   （默认三套；需要 numpy、pillow、scipy）
"""
import sys
from pathlib import Path
import numpy as np
from PIL import Image
from scipy.spatial import Delaunay

REPO = Path(__file__).resolve().parents[2]
X = REPO / 'TilesetExperiment'
C = 512
BAND = 56
SETS = {'Swamp_OliveStrata': ('IMG_9471_BaseColor.png', 'IMG_9471_Normal_Periodic512.png'),
        'Forest_RustCrag': ('Redo/Forest_Cliff_BaseColor.png', 'Redo/Forest_Cliff_Normal_Periodic512.png'),
        'Human_Limestone': ('IMG_9485_BaseColor.png', 'IMG_9485_Normal_Periodic512.png')}
SIZES = {'2x1': (2, 1), '1x2': (1, 2), '2x2': (2, 2)}
W9 = [.051, .0918, .12245, .1531, .1633, .1531, .12245, .0918, .051]
q8 = lambda a: np.rint(np.clip(a, 0, 1) * 255) / 255

def height67(rgb01):
    h = q8(rgb01 @ np.array([.2126, .7152, .0722]))
    h = q8((1.26 / .74) * (h - .5) + .5)
    for ax in (0, 1):
        h = q8(sum(w * np.roll(h, int(np.floor(k * (-32) / 5 + .5)), axis=ax) for k, w in zip(range(-4, 5), W9)))
    e = np.maximum.reduce([np.roll(h, d, axis=0) for d in range(65)])
    return h + .67 * (e - h)

def facet_normals(z, nverts):
    """周期域（宽 SX、高 SY 像素）上贪心插点三角化，返回每像素面法线（切线空间，+X 右、+Y 上）。每轮插入一批误差最大的点。"""
    SY, SX = z.shape
    yy, xx = np.mgrid[0:SY, 0:SX]
    pix = np.c_[xx.ravel() + .5, yy.ravel() + .5]
    zf = z.ravel()
    gx_, gy_ = 4 * SX // C, 4 * SY // C
    pts = [((i + .5) * SX / gx_, (j + .5) * SY / gy_) for j in range(gy_) for i in range(gx_)]
    off = np.array([(dx, dy) for dy in (-SY, 0, SY) for dx in (-SX, 0, SX)], float)
    used = set()
    while True:
        P = np.array(pts)
        rep = (P[None] + off[:, None]).reshape(-1, 2)
        zi = np.tile(z[(P[:, 1] - .5).astype(int), (P[:, 0] - .5).astype(int)], 9)
        tri = Delaunay(rep)
        s = tri.find_simplex(pix)
        T = tri.transform[s]
        b = np.einsum('nij,nj->ni', T[:, :2], pix - T[:, 2])
        interp = (np.c_[b, 1 - b.sum(1)] * zi[tri.simplices[s]]).sum(1)
        if len(pts) >= nverts:
            break
        err = np.abs(interp - zf)
        # 每个三角形里误差最大的像素，再取最大的一批
        order = np.lexsort((-err, s)); first = np.r_[True, s[order][1:] != s[order][:-1]]
        cand = order[first]; cand = cand[np.argsort(-err[cand])]
        k = max(8, min(len(pts) // 8, nverts - len(pts)))
        added = 0
        for c in cand:
            if c in used: continue
            used.add(int(c)); pts.append(tuple(pix[c])); added += 1
            if added >= k: break
        if added == 0: break
    V = rep[tri.simplices]; Z = zi[tri.simplices]
    A = np.stack([V[:, 1] - V[:, 0], V[:, 2] - V[:, 0]], 1)
    dz = np.stack([Z[:, 1] - Z[:, 0], Z[:, 2] - Z[:, 0]], 1)
    grad = np.linalg.solve(A, dz[..., None])[..., 0]
    gx = grad[s, 0].reshape(SY, SX); gy = grad[s, 1].reshape(SY, SX)
    n = np.stack([-gx, gy, np.ones_like(gx)], -1)
    return n / np.linalg.norm(n, axis=2, keepdims=True)

def band_weight(SY, SX):
    yy, xx = np.mgrid[0:SY, 0:SX]
    d = np.minimum(np.minimum(xx, SX - 1 - xx), np.minimum(yy, SY - 1 - yy)).astype(float)
    a = np.clip(d / BAND, 0, 1)
    return (a * a * (3 - 2 * a))[..., None]

def load(p): return np.asarray(Image.open(p).convert('RGB'), float) / 255

def process(name):
    cpath, npath = SETS[name]
    base = load(X / cpath); bn = load(X / npath) * 2 - 1
    d = X / 'Variants_GPT' / name; out = d / 'processed'; out.mkdir(exist_ok=True)
    made = {}
    for f in sorted(d.glob('*_v*.png')):
        size, ver = f.stem.split('_')
        w, h = SIZES[size]
        img = Image.open(f).convert('RGB')
        if img.size != ((w + 1) * C, (h + 1) * C):
            img = img.resize(((w + 1) * C, (h + 1) * C), Image.LANCZOS)
        a = np.asarray(img, float) / 255
        hole = a[C // 2:C // 2 + h * C, C // 2:C // 2 + w * C]
        bt = np.tile(base, (h, w, 1)); bnt = np.tile(bn, (h, w, 1))
        hole = (hole - hole.mean((0, 1))) / (hole.std((0, 1)) + 1e-6) * bt.std((0, 1)) + bt.mean((0, 1))   # 色调对齐
        wgt = band_weight(h * C, w * C)
        col = np.clip(bt * (1 - wgt) + hole * wgt, 0, 1)
        z = .5 * (height67(col) - .5) * C
        nn = facet_normals(z, 256 * w * h)
        nn = bnt * (1 - wgt) + nn * wgt; nn /= np.linalg.norm(nn, axis=2, keepdims=True)
        Image.fromarray(np.uint8(np.rint(col * 255))).save(out / f'{size}_{ver}_BaseColor.png')
        Image.fromarray(np.uint8(np.clip(np.rint((nn * .5 + .5) * 255), 0, 255))).save(out / f'{size}_{ver}_Normal.png')
        made[f'{size}_{ver}'] = (col, nn)
        print(name, size, ver, 'ok')
    # 预览：8×3 格的墙，基础块 + 变体混铺；左边只有基础块
    def wall(use):
        Wc, Hc = 8, 3
        cc = np.tile(base, (Hc, Wc, 1)); nc = np.tile(bn, (Hc, Wc, 1))
        if use:
            plan = [('2x2_v1', 1, 0), ('2x1_v1', 4, 0), ('1x2_v1', 6, 1), ('2x1_v2', 3, 2), ('1x2_v2', 0, 1)]
            for key, cx, cy in plan:
                if key not in made: continue
                c_, n_ = made[key]; hh, ww = c_.shape[:2]
                if cy * C + hh > Hc * C or cx * C + ww > Wc * C: continue
                cc[cy * C:cy * C + hh, cx * C:cx * C + ww] = c_; nc[cy * C:cy * C + hh, cx * C:cx * C + ww] = n_
        L = np.array([-.7, .5, 1]); L /= np.linalg.norm(L)
        lit = cc * (np.clip((nc * L).sum(2), 0, 1)[..., None] * 1.2 + .15)
        return np.clip(lit, 0, 1)
    pv = np.concatenate([wall(False), np.zeros((3 * C, 24, 3)), wall(True)], 1)
    Image.fromarray(np.uint8(pv * 255)).resize((pv.shape[1] // 3, pv.shape[0] // 3), Image.LANCZOS).save(out / 'preview.png')

if __name__ == '__main__':
    for n in (sys.argv[1:] or list(SETS)):
        process(n)
