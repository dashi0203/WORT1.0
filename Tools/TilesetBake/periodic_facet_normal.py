"""崖壁法线（无缝版）：周期域上的贪心插点三角化 + 面法线直接光栅化。

替代 Blender Collapse 减面 + 烘焙那一步，其余与 TilesetExperiment/WORKFLOW.md 相同：
原色图 → 高度（Contrast 0.26、Blur/Sharp -32）→ 向下 64px 最大包络 67% → 置换幅度 0.5。
三角化在环面（左右、上下周期）上做，所以贴图天然无缝，边界上没有固定折线。
每次插入误差最大的像素（Garland–Heckbert 贪心插点），环面上 三角面 = 2 × 顶点，
默认 256 个顶点 = 512 个三角面，与 Blender 那档面数一致。
正交俯视下面法线 = 烘焙结果，不需要 Blender。

用法：python periodic_facet_normal.py <含 IMG_*_BaseColor.png 的目录> <输出目录> [顶点数=256]
依赖：numpy、pillow、scipy
"""
import sys
from pathlib import Path
import numpy as np
from PIL import Image
from scipy.spatial import Delaunay

S = 512
W9 = [.051, .0918, .12245, .1531, .1633, .1531, .12245, .0918, .051]
q = lambda a: np.rint(np.clip(a, 0, 1) * 255) / 255

def height67(path):
    a = np.array(Image.open(path).convert('RGB')) / 255.0
    h = q(a @ np.array([.2126, .7152, .0722]))
    h = q((1.26 / .74) * (h - .5) + .5)
    for ax in (0, 1):
        h = q(sum(w * np.roll(h, int(np.floor(k * (-32) / 5 + .5)), axis=ax) for k, w in zip(range(-4, 5), W9)))
    e = np.maximum.reduce([np.roll(h, d, axis=0) for d in range(65)])
    return h + .67 * (e - h)

def triangulate(z, nverts):
    """z: 像素单位高度 (S×S)。返回 (每像素所在三角形的平面梯度 gx, gy)。"""
    yy, xx = np.mgrid[0:S, 0:S]
    pix = np.c_[xx.ravel() + .5, yy.ravel() + .5]
    zf = z.ravel()
    g = 4  # 初始 4×4 周期格点
    pts = [((i + .5) * S / g, (j + .5) * S / g) for j in range(g) for i in range(g)]
    off = np.array([(dx, dy) for dy in (-S, 0, S) for dx in (-S, 0, S)], float)
    def build(pts):
        P = np.array(pts)
        rep = (P[None] + off[:, None]).reshape(-1, 2)
        zi = np.tile(z[(P[:, 1] - .5).astype(int), (P[:, 0] - .5).astype(int)], 9)
        tri = Delaunay(rep)
        s = tri.find_simplex(pix)
        T = tri.transform[s]
        b = np.einsum('nij,nj->ni', T[:, :2], pix - T[:, 2])
        bc = np.c_[b, 1 - b.sum(1)]
        zv = zi[tri.simplices[s]]
        return (bc * zv).sum(1), tri, s, rep, zi
    while True:
        interp, tri, s, rep, zi = build(pts)
        if len(pts) >= nverts:
            break
        err = np.abs(interp - zf)
        k = int(np.argmax(err))
        pts.append(tuple(pix[k]))
    # 每个三角形的平面梯度
    V = rep[tri.simplices]; Z = zi[tri.simplices]
    A = np.stack([V[:, 1] - V[:, 0], V[:, 2] - V[:, 0]], 1)        # (t,2,2)
    dz = np.stack([Z[:, 1] - Z[:, 0], Z[:, 2] - Z[:, 0]], 1)        # (t,2)
    grad = np.linalg.solve(A, dz[..., None])[..., 0]                                  # (t,2): dz/dx, dz/dy(图像向下)
    gx = grad[s, 0].reshape(S, S); gy = grad[s, 1].reshape(S, S)
    return gx, gy, len(pts)

def main():
    src, out = Path(sys.argv[1]), Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
    nverts = int(sys.argv[3]) if len(sys.argv) > 3 else 256
    for f in sorted(src.glob('IMG_*_BaseColor.png')):
        stem = f.name.replace('_BaseColor.png', '')
        z = .5 * (height67(f) - .5) * S          # 像素单位
        gx, gy, nv = triangulate(z, nverts)
        n = np.stack([-gx, gy, np.ones_like(gx)], -1)               # 切线空间 +X 右、+Y 上
        n /= np.linalg.norm(n, axis=2, keepdims=True)
        img = np.uint8(np.clip(np.rint((n * .5 + .5) * 255), 0, 255))
        Image.fromarray(img).save(out / f'{stem}_Normal_Periodic{2*nv}.png')
        print(stem, 'vertices', nv, 'triangles', 2 * nv)

if __name__ == '__main__':
    main()
