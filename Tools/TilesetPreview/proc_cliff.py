"""
程序化崖壁预览（原型，不开 Unity）：体素高度场 → 游戏相机（正交俯视 + 纵向错切，屏幕 v = z + y）逐像素求交渲染。

几何（只画镜头看得见的：顶面 + 朝南竖面）：
- 朝南竖面 = 平面，UV 按世界坐标（u = x，v = y，1m 一张），相邻格、上下层连续。
- 顶沿倒角：露边的顶面边缘按噪声切一条 45° 倒角（宽 CH0–CH1，只往里收）。南边倒角迎光成亮线；
  北边倒角和视线平行，看到的是顶面轮廓往里收；东西边倒角斜着露出一点。
- 两头外凸：朝南竖面在一段崖的东西两头往外延伸，宽度随高度变化（顶上 0，往下最多 BULGE），只在人脚下方。
着色（对应 Unity 的 Wort/TileSurface）：
- 崖壁颜色 + 无缝法线（权重 1）；顶面泥土贴图按世界 xz；暗部提亮 0.08（sRGB）。
- 崖脚往上 FOOT 内按高度差融合泥土；崖顶往下 TOP 内同样融合（交界跟着石块明暗走，不是直线）。
- 阴影：沿光线方向在体素里步进（近似 Unity 的阴影，看不见的北 / 东 / 西面在 Unity 里只投影不渲染）。

用法：python3 proc_cliff.py <泥土贴图目录(含 OliveStrata.png 等)> <输出.png> [每米像素=48]
"""
import sys, math
import numpy as np
from pathlib import Path
from PIL import Image

REPO = Path(__file__).resolve().parents[2]
X = REPO / 'TilesetExperiment'
GROUND = Path(sys.argv[1]); OUT = sys.argv[2]
PPM = int(sys.argv[3]) if len(sys.argv) > 3 else 48
SETS = [('OliveStrata', 'IMG_9471'), ('RustCrag', 'IMG_9473'), ('Limestone', 'IMG_9485')]
CH0, CH1 = 0.04, 0.14      # 倒角宽度范围（m）
BULGE = 0.35               # 两头最大外凸（m）
FOOT, TOP = 0.35, 0.22     # 崖脚 / 崖顶融合高度（m）
LIFT = 0.08
L = np.array([0.329, 0.819, -0.470]); L /= np.linalg.norm(L)   # 指向光源（TerrainLab 的平行光）

# 测试地：x 向东，z 向北；数值 = 层数
H = np.zeros((18, 13), int)
H[1:5, 6:10] = 4            # 4×4×4 方块
H[7:15, 5:11] = 2           # 台地
H[10:15, 8:11] = 3          # 台地上再高一层（凹角）
H[7:10, 2:5] = 1            # 一层高的小台
H[12:17, 1:3] = 1           # 一层高的长条
NX, NZ = H.shape
YMAX = H.max()

def h_at(ix, iz):
    ok = (ix >= 0) & (ix < NX) & (iz >= 0) & (iz < NZ)
    return np.where(ok, H[np.clip(ix, 0, NX - 1), np.clip(iz, 0, NZ - 1)], 0)

def vnoise(t, seed):  # 一维值噪声，周期约 0.35m，0..1
    t = t / 0.35 + seed * 17.3
    i = np.floor(t); f = t - i; f = f * f * (3 - 2 * f)
    def r(k): return (np.sin(k * 12.9898 + seed * 78.233) * 43758.5453) % 1.0
    return r(i) * (1 - f) + r(i + 1) * f

def ch(t, seed): return CH0 + (CH1 - CH0) * vnoise(t, seed)

def load(p, linear=False):
    a = np.asarray(Image.open(p).convert('RGB'), np.float32) / 255
    return a

def samp(t, u, v):
    th, tw = t.shape[:2]
    return t[(th - 1 - np.floor(v * th).astype(int)) % th, np.floor(u * tw).astype(int) % tw]

def render(biome, stem):
    cliff = load(X / f'{stem}_BaseColor.png'); nmap = load(X / f'{stem}_Normal_Periodic512.png') * 2 - 1
    earth = load(GROUND / f'{biome}.png')
    W = int((NX + 2) * PPM); Hh = int((NZ + YMAX + 2) * PPM)
    px, py = np.meshgrid(np.arange(W) + 0.5, np.arange(Hh) + 0.5)
    x = (px / PPM - 1).ravel(); v = ((Hh - py) / PPM - 1).ravel()
    n = len(x)
    kind = np.zeros(n, int)          # 0 背景 1 顶面 2 南面 3 南倒角 4 东西倒角
    hit = np.zeros((n, 3)); yb = np.zeros(n)   # yb = 南面所在崖的崖脚高度
    htop = np.zeros(n)
    ix = np.floor(x).astype(int)
    z = v - (YMAX + 0.01); iz = np.floor(z).astype(int); first = np.ones(n, bool)
    act = np.ones(n, bool)
    for _ in range(NZ + YMAX + 4):
        a = np.where(act)[0]
        if len(a) == 0: break
        xa, va, iza, ixa = x[a], v[a], iz[a], ix[a]
        h = h_at(ixa, iza)
        ze = np.where(first[a], z[a], iza); ye = va - ze
        hs = h_at(ixa, iza - 1)
        # 1) 从南边进入、低于本格顶 → 南面（倒角区例外）
        cS = ch(xa, 1)
        enter = (~first[a]) & (ye <= h) & (ye >= 0) & (h > hs)
        southface = enter & (ye <= h - cS)
        southch = enter & (ye > h - cS)
        # 两头外凸：本格比 ye 低，但东 / 西邻格在这个高度有朝南的面
        bul = np.zeros(len(a), bool); bulH = np.zeros(len(a))
        for dx, edge in ((1, ixa + 1.0), (-1, ixa + 0.0)):
            hn = h_at(ixa + dx, iza); hns = h_at(ixa + dx, iza - 1)
            d = np.abs(xa - edge)
            depth = np.clip((hn - ye) / 0.6, 0, 1)
            width = BULGE * depth * (0.45 + 0.55 * vnoise(ye * 1.7 + edge * 3.1, 5))
            c = (~first[a]) & (ye < hn) & (ye > hns) & (ye >= h) & (d < width) & (ye >= 0)
            bul |= c; bulH = np.where(c, np.maximum(bulH, hn), bulH)
        sf = southface | bul
        idx = a[sf]; kind[idx] = 2; hit[idx] = np.c_[xa[sf], ye[sf], iza[sf]]
        yb[idx] = np.where(bul[sf] & ~southface[sf], h[sf], hs[sf])
        htop[idx] = np.where(bul[sf] & ~southface[sf], bulH[sf], h[sf])
        # 南倒角：平面 y = h − c + (z − iz)
        zc = (va + iza - h + cS) / 2
        idx = a[southch]; kind[idx] = 3; hit[idx] = np.c_[xa[southch], (va - zc)[southch], zc[southch]]; htop[idx] = h[southch]
        done = sf | southch
        # 2) 本格内落到顶面
        zt = va - h
        intop = (~done) & (zt >= ze - 1e-9) & (zt < iza + 1)
        # 顶面上的倒角（露边才有）
        cN = ch(xa, 2); cE = ch(zt, 3); cW = ch(zt, 4)
        hn_ = h_at(ixa, iza + 1); he_ = h_at(ixa + 1, iza); hw_ = h_at(ixa - 1, iza)
        fx = xa - ixa; fz = zt - iza
        inS = (hs < h) & (fz < cS); inN = (hn_ < h) & (fz > 1 - cN)
        inE = (he_ < h) & (fx > 1 - cE); inW = (hw_ < h) & (fx < cW)
        top_ok = intop & ~inS & ~inN & ~inE & ~inW
        idx = a[top_ok]; kind[idx] = 1; hit[idx] = np.c_[xa[top_ok], h[top_ok], zt[top_ok]]; htop[idx] = h[top_ok]
        # 南倒角（从顶面那边落进去）
        s2 = intop & inS
        idx = a[s2]; kind[idx] = 3; hit[idx] = np.c_[xa[s2], (va - zc)[s2], zc[s2]]; htop[idx] = h[s2]
        # 东 / 西倒角：y = h − (fx − (1 − cE))，视线 x 不变，直接求 y
        for m_, yy in ((intop & inE & ~inS, h - (fx - (1 - cE))), (intop & inW & ~inS & ~inE, h - (cW - fx))):
            zz = np.minimum(va - yy, iza + 1 - 1e-4)
            m2 = m_
            idx = a[m2]; kind[idx] = 4; hit[idx] = np.c_[xa[m2], yy[m2], zz[m2]]; htop[idx] = h[m2]
        # 北倒角与视线平行：顶面往里收，继续往北
        hitnow = kind[a] > 0
        act[a[hitnow]] = False
        nxt = a[~hitnow]
        iz[nxt] += 1; first[nxt] = False
        out = nxt[(v[nxt] - iz[nxt]) < -0.5]
        act[out] = False
    # 着色
    col = np.zeros((n, 3)) + [0.10, 0.14, 0.19]
    P = hit
    tex_c = samp(cliff, P[:, 0], P[:, 1]); tex_e = samp(earth, P[:, 0], P[:, 2])
    nt = samp(nmap, P[:, 0], P[:, 1])
    N = np.zeros((n, 3)); alb = np.zeros((n, 3))
    m = kind == 1; N[m] = [0, 1, 0]; alb[m] = tex_e[m]
    m = kind == 2
    Nw = np.c_[nt[:, 0], nt[:, 1], -nt[:, 2]]; Nw /= np.linalg.norm(Nw, axis=1, keepdims=True)
    N[m] = Nw[m]
    lc = tex_c @ [0.299, 0.587, 0.114]; le = tex_e @ [0.299, 0.587, 0.114]
    hf = P[:, 1] - yb; ht = htop - P[:, 1]
    qf = np.clip((FOOT - hf) / FOOT + (le - lc) * 1.2, 0, 1); qf = qf * qf * (3 - 2 * qf)
    qt = np.clip((TOP - ht) / TOP + (le - lc) * 1.2 - 0.25, 0, 1); qt = qt * qt * (3 - 2 * qt)
    q = np.maximum(qf, qt)
    alb[m] = (tex_c * (1 - q[:, None]) + tex_e * q[:, None])[m]
    m = kind == 3; N[m] = [0, 0.7071, -0.7071]; alb[m] = (tex_e * 0.6 + tex_c * 0.4)[m] * 1.08
    m = kind == 4; sgn = np.sign(P[:, 0] - np.floor(P[:, 0]) - 0.5)
    N[m] = np.c_[sgn * 0.7071, np.full(n, 0.7071), np.zeros(n)][m]; alb[m] = (tex_e * 0.6 + tex_c * 0.4)[m]
    # 暗部提亮（sRGB）
    alb = LIFT + alb * (1 - LIFT)
    # 阴影：沿光线步进，进入体素即在阴影里
    sh = np.ones(n)
    g = np.where(kind > 0)[0]
    p0 = P[g] + N[g] * 0.02
    for s in range(1, 160):
        q_ = p0 + L * (s * 0.04)
        inside = q_[:, 1] < h_at(np.floor(q_[:, 0]).astype(int), np.floor(q_[:, 2]).astype(int)) - 1e-4
        sh[g[inside]] = 0
    lam = np.clip(N @ L, 0, 1) * sh
    sky = 0.5 + 0.5 * N[:, 1]
    k = 0.30 + 0.22 * sky + 0.78 * lam
    hitm = kind > 0
    col[hitm] = np.clip(alb[hitm] * k[hitm, None], 0, 1)
    return (col.reshape(Hh, W, 3) * 255).astype(np.uint8)

imgs = [render(b, s) for b, s in SETS]
gap = np.zeros((imgs[0].shape[0], 8, 3), np.uint8)
Image.fromarray(np.concatenate(sum([[im, gap] for im in imgs], [])[:-1], 1)).save(OUT)
print('输出', OUT)
