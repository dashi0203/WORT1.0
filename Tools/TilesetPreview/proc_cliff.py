"""
程序化崖壁预览（原型，不开 Unity）：体素高度场 → 生成网格 → 游戏相机（正交俯视 + 纵向错切，屏幕 v = z + y）光栅化。
网格就是以后 Unity 里 CliffMesher 要生成的东西（这里用 Python 先定形）。

只生成镜头看得见的面（顶面 + 朝南竖面），看得见的体素露边一律往外鼓（负责人 2026-11-13，超出碰撞格）：
- 侧面鼓起：只鼓“顶层”和“前排”体素的东 / 西侧面（顶层还有北边轮廓），被挡住的不生成；顶面 / 正面平面本身不鼓。
- 贴图沿轮廓连续：正面 u = x，侧面 u 从正面转角接着走。
- 正面：平面，UV 按世界坐标（u = x，v = y，1m 一张）连续；东西两头整个高度往外鼓。
- 北 / 东 / 西的竖面看不见，不生成（Unity 里另做只投影的面）。
着色（对应 Wort/TileSurface）：崖壁颜色 + 无缝法线（权重 1），顶面泥土按世界 xz，暗部提亮 0.08，崖脚 / 崖顶按高度差融合泥土，阴影按体素近似。

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
# (Biome, 崖壁颜色, 崖壁法线, 顶岩颜色, 顶岩法线)；顶岩为 None = 直接用泥土（沼泽、森林，负责人 2026-11-13）
SETS = [('OliveStrata', 'IMG_9471_BaseColor.png', 'IMG_9471_Normal_Periodic512.png', None, None),
        ('RustCrag', 'Redo/Forest_Cliff_BaseColor.png', 'Redo/Forest_Cliff_Normal_Periodic512.png', None, None),
        ('Limestone', 'IMG_9485_BaseColor.png', 'IMG_9485_Normal_Periodic512.png', 'Redo/Human_TopRock_BaseColor.png', 'Redo/Human_TopRock_Normal_Periodic512.png')]
TR_PERIOD = 2.7    # 顶岩平铺周期（m），不取整数，和崖壁 1m 错开
NCLIP = 0.12       # 顶面北边透明裁剪最大深度（m）
WALL_GAIN, WALL_SAT = 1.12, 1.12   # 崖壁提亮 / 加饱和（Unity 材质参数，预览模拟）
EARTH_P = 2.0      # 泥土平铺周期（m）
TOPB = 0.35        # 崖壁顶部融合顶岩的高度（m）
INSET0, INSET1 = 0.15, 0.35   # 泥土离露边内收（m）
SUB = 4                    # 每格顶面细分
FOOT, TOP = 0.35, 0.22
LIFT = 0.08
L = np.array([0.329, 0.819, -0.470]); L /= np.linalg.norm(L)

H = np.zeros((18, 13), int)
H[1:5, 6:10] = 4
H[7:15, 5:11] = 2
H[10:15, 8:11] = 3
H[7:10, 2:5] = 1
H[12:17, 1:3] = 1
NX, NZ = H.shape
YMAX = H.max()

def hc(ix, iz):
    return H[ix, iz] if 0 <= ix < NX and 0 <= iz < NZ else 0

def vnoise(t, seed, per=0.45):
    t = np.asarray(t, float) / per + seed * 17.3
    i = np.floor(t); f = t - i; f = f * f * (3 - 2 * f)
    r = lambda k: (np.sin(k * 12.9898 + seed * 78.233) * 43758.5453) % 1.0
    return r(i) * (1 - f) + r(i + 1) * f


# ---------- 网格 ----------
# 鼓起规则（负责人 2026-11-13）：只有“看得见的体素层”的侧面往外鼓——
#   顶层（这一列最上面那块体素）和前排（南面露出的体素）的东 / 西侧面；鼓起在顶沿处收为 0，往下才鼓出。
#   顶面、正面这两张平面本身不变形；顶面露边用着色器假倒角。被挡住的体素侧面不生成（Unity 里另做只投影的平面）。
# 贴图沿轮廓连续：正面 u = x；东侧 u = 东边 x + (z − 正面 z)，西侧 u = 西边 x − (z − 正面 z)，转角处接上；v = y。
BUL0, BUL1 = 0.06, 0.30     # 侧面鼓出量（m）
BEV0, BEV1 = 0.08, 0.24     # 顶面假倒角宽度（m，着色器）

def vnoise2(a, b, seed, per=0.4):
    a = np.asarray(a, float) / per + seed * 7.1; b = np.asarray(b, float) / per - seed * 3.7
    i, j = np.floor(a), np.floor(b); fa, fb = a - i, b - j
    fa = fa * fa * (3 - 2 * fa); fb = fb * fb * (3 - 2 * fb)
    r = lambda p, q: (np.sin(p * 12.9898 + q * 78.233 + seed * 4.1) * 43758.5453) % 1.0
    return (r(i, j) * (1 - fa) + r(i + 1, j) * fa) * (1 - fb) + (r(i, j + 1) * (1 - fa) + r(i + 1, j + 1) * fa) * fb

def bulge(side, y, z, h):  # 东(1) / 西(-1) 侧面在 (y, z) 处往外鼓多少；世界坐标噪声，相邻面自动连续；顶沿 h 处收为 0（顶面不变形）
    t = np.clip((h - y) / 0.35, 0, 1); t = t * (2 - t)          # 顶上线性起步（约 45° 朝上），往下变缓
    return (BUL0 + (BUL1 - BUL0) * vnoise2(y, z, 3 if side > 0 else 4)) * t

def bend(v, out, ang):
    """把“顶面坐标系”里的法线 v 绕边缘轴（up × out）转 ang 弧度：0 = 顶面，45° = 折线，90° = 竖面。"""
    up = np.array([0, 1.0, 0])
    k = np.cross(np.broadcast_to(up, out.shape), out); k /= np.linalg.norm(k, axis=1, keepdims=True) + 1e-12
    c, s_ = np.cos(ang)[:, None], np.sin(ang)[:, None]
    r = v * c + np.cross(k, v) * s_ + k * (k * v).sum(1, keepdims=True) * (1 - c)
    return r / (np.linalg.norm(r, axis=1, keepdims=True) + 1e-12)

def smooth_r(along, seed):   # 平滑倒角宽度（m）：18–32cm，沿边缓慢变化；顶面与墙面用同一个，折线两侧对上
    return 0.18 + 0.14 * vnoise(along, 100 + seed, 0.7)

tris = []   # (P 3×3, kind, extra, U 3)  kind: 1 顶面 2 正面 4 侧面；extra = (崖脚高, 崖顶高, 法线…)
def quad(a, b, c, d, kind, extra=(0, 0), U=None):
    Ua = U if U is not None else (0, 0, 0, 0)
    tris.append((np.array([a, b, c], float), kind, extra, np.array([Ua[0], Ua[1], Ua[2]], float)))
    tris.append((np.array([a, c, d], float), kind, extra, np.array([Ua[0], Ua[2], Ua[3]], float)))

def front_z(ix, iz):       # 这一列往南数，正面在哪个 z（用来让侧面 u 从正面转角接上）
    h = hc(ix, iz); k = iz
    while k > 0 and hc(ix, k - 1) >= h: k -= 1
    return float(k)

for ix in range(NX):
    for iz in range(NZ):
        h = H[ix, iz]
        if h == 0: continue
        # 顶面：平的 1m 方块（边缘的倒角是着色器里的假倒角，不动网格）
        quad([ix, h, iz], [ix + 1, h, iz], [ix + 1, h, iz + 1], [ix, h, iz + 1], 1)
        hs = hc(ix, iz - 1)
        front = hs < h
        # 正面：平面 z = iz，从 hs 到 h；东西两头若露边，端点跟着侧面鼓出量走
        if front:
            ys = np.linspace(hs, h, int((h - hs) * SUB) + 1)
            def xe(side, y):
                edge = ix + (1.0 if side > 0 else 0.0)
                return edge + side * bulge(side, y, iz, h) if hc(ix + side, iz) < y + 1e-6 and hc(ix + side, iz) < h else edge
            for k in range(len(ys) - 1):
                y0, y1 = ys[k], ys[k + 1]
                a_, b_, c_, d_ = [xe(-1, y0), y0, iz], [xe(1, y0), y0, iz], [xe(1, y1), y1, iz], [xe(-1, y1), y1, iz]
                quad(a_, b_, c_, d_, 2, (hs, h), (a_[0], b_[0], c_[0], d_[0]))
        # 东 / 西侧面：前排整个高度、顶层最上 1m；只在旁边这一格更低的高度上
        fz = front_z(ix, iz)
        for side in (1, -1):
            hb = hc(ix + side, iz)
            if hb >= h: continue
            lo = hb if front else max(hb, h - 1)
            ys = np.linspace(lo, h, int(round((h - lo) * SUB)) + 1)
            zs = [iz + j / SUB for j in range(SUB + 1)]
            edge = ix + (1.0 if side > 0 else 0.0)
            G = [[[edge + side * bulge(side, y_, z_, h), y_, z_] for z_ in zs] for y_ in ys]
            for r in range(len(ys) - 1):
                for c in range(SUB):
                    a_, b_, c2, d_ = G[r][c], G[r][c + 1], G[r + 1][c + 1], G[r + 1][c]
                    n = np.cross(np.subtract(b_, a_), np.subtract(d_, a_)); n = n / (np.linalg.norm(n) + 1e-12)
                    if n[0] * side < 0: n = -n
                    U = [edge + side * (q[2] - fz) for q in (a_, b_, c2, d_)]
                    quad(a_, b_, c2, d_, 4, (hb, h, *n), U)
print('三角形', len(tris))

# ---------- 光栅化 + 着色 ----------
def load(p): return np.asarray(Image.open(p).convert('RGB'), np.float32) / 255
def samp(t, u, v):
    th, tw = t.shape[:2]
    return t[(th - 1 - np.floor(v * th).astype(int)) % th, np.floor(u * tw).astype(int) % tw]

def shadow(P):
    sh = np.ones(len(P)); p0 = P + np.array([0, 0.02, 0])
    for s in range(1, 140):
        q = p0 + L * (s * 0.04)
        ix = np.floor(q[:, 0]).astype(int); iz = np.floor(q[:, 2]).astype(int)
        ok = (ix >= 0) & (ix < NX) & (iz >= 0) & (iz < NZ)
        hh = np.where(ok, H[np.clip(ix, 0, NX - 1), np.clip(iz, 0, NZ - 1)], 0)
        sh[q[:, 1] < hh - 1e-4] = 0
    return sh

def render(biome, cpath, npath, trpath, trnpath):
    cliff = load(X / cpath); nmap = load(X / npath) * 2 - 1
    earth = load(GROUND / f'{biome}.png')
    en_ = GROUND / f'{biome}_Normal_Periodic768.png'   # 泥土法线：同一流程，不做向下延伸、幅度 0.12（隐约可见）
    enm = load(en_) * 2 - 1 if en_.exists() else None
    toprock = load(X / trpath) if trpath else None
    trn = load(X / trnpath) * 2 - 1 if trnpath else None
    u0, v0 = -1.0, -1.0
    W = int((NX + 2) * PPM); Hh = int((NZ + YMAX + 2) * PPM)
    gx, gy = np.meshgrid(np.arange(W) + 0.5, np.arange(Hh) + 0.5)
    wx = gx / PPM + u0; wz = (Hh - gy) / PPM + v0
    depth = (0 - wz).astype(np.float64)          # 深度 = y − z（越大越靠近镜头）；地面 y = 0
    kind = np.zeros((Hh, W), np.int8); hx = wx.copy(); hy = np.zeros_like(wx); hz = wz.copy()
    eb = np.zeros_like(wx); et = np.zeros_like(wx); en = np.zeros((Hh, W, 3))
    hu = wx.copy()
    for P, k, extra, U in tris:
        su = P[:, 0]; sv = P[:, 2] + P[:, 1]
        px = (su - u0) * PPM; py = Hh - (sv - v0) * PPM
        xa, xb = int(max(0, math.floor(px.min()))), int(min(W - 1, math.ceil(px.max())))
        ya, yb = int(max(0, math.floor(py.min()))), int(min(Hh - 1, math.ceil(py.max())))
        if xa > xb or ya > yb: continue
        d = (px[1] - px[0]) * (py[2] - py[0]) - (py[1] - py[0]) * (px[2] - px[0])
        if abs(d) < 1e-12: continue
        X_, Y_ = np.meshgrid(np.arange(xa, xb + 1) + 0.5, np.arange(ya, yb + 1) + 0.5)
        w1 = ((X_ - px[0]) * (py[2] - py[0]) - (Y_ - py[0]) * (px[2] - px[0])) / d
        w2 = ((px[1] - px[0]) * (Y_ - py[0]) - (py[1] - py[0]) * (X_ - px[0])) / d
        w0 = 1 - w1 - w2
        ins = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        wp = [w0, w1, w2]
        Px = sum(wp[i] * P[i, 0] for i in range(3)); Py = sum(wp[i] * P[i, 1] for i in range(3)); Pz = sum(wp[i] * P[i, 2] for i in range(3))
        dep = Py - Pz + (0.002 if k == 3 else 0)
        sl = (slice(ya, yb + 1), slice(xa, xb + 1))
        if k == 1:
            # 顶面北边是真正的轮廓线（后面是更低的地面）：着色器透明裁剪，按噪声把边咬掉 0–NCLIP，轮廓不再笔直
            cix, ciz, ch_ = int(round(P[:, 0].min())), int(round(P[:, 2].min())), P[0, 1]
            if hc(cix, ciz + 1) < ch_:
                cw = NCLIP * (0.55 * vnoise(Px, 31, 0.35) + 0.45 * vnoise(Px, 32, 0.09))
                cw = cw + 0.10 * np.clip((vnoise(Px, 33, 0.8) - 0.7) / 0.3, 0, 1)
                ins = ins & ((ciz + 1 - Pz) > cw)
        ok = ins & (dep > depth[sl])
        if not ok.any(): continue
        depth[sl][ok] = dep[ok]; kind[sl][ok] = k
        hx[sl][ok] = Px[ok]; hy[sl][ok] = Py[ok]; hz[sl][ok] = Pz[ok]; eb[sl][ok] = extra[0]; et[sl][ok] = extra[1]
        if len(extra) > 2: en[sl][ok] = extra[2:]
        hu[sl][ok] = sum(wp[i] * U[i] for i in range(3))[ok]
    x, y, z = hx.ravel(), hy.ravel(), hz.ravel(); kd = kind.ravel(); yb_ = eb.ravel(); yt_ = et.ravel()
    n = len(x)
    uu = hu.ravel()
    tc = samp(cliff, uu, y); te = samp(earth, x / EARTH_P, z / EARTH_P); nt = samp(nmap, uu, y)
    if enm is not None:
        ne = samp(enm, x / EARTH_P, z / EARTH_P); Ne = np.c_[ne[:, 0], ne[:, 2], ne[:, 1]]; Ne /= np.linalg.norm(Ne, axis=1, keepdims=True)
    else:
        Ne = np.tile([0.0, 1.0, 0.0], (n, 1))
    N = Ne.copy(); alb = te.copy()
    Nw = np.c_[nt[:, 0], nt[:, 1], -nt[:, 2]]; Nw /= np.linalg.norm(Nw, axis=1, keepdims=True)
    lc = tc @ [0.299, 0.587, 0.114]; le = te @ [0.299, 0.587, 0.114]
    qf = np.clip((FOOT - (y - yb_)) / FOOT + (le - lc) * 1.2, 0, 1); qf = qf * qf * (3 - 2 * qf)
    qt = np.clip((TOP - (yt_ - y)) / TOP + (le - lc) * 1.2 - 0.25, 0, 1); qt = qt * qt * (3 - 2 * qt)
    q = qf     # 崖顶不再融泥土：顶沿由顶面倒角过渡，否则石头倒角和石头崖面之间夹一条泥土色（封边）
    # 顶面假倒角（着色器）：露边内侧一条“露出来的石头边”。
    #   宽度 = 两层噪声（长起伏 + 碎边）+ 偶尔的深缺口；边界再按崖壁贴图明暗推进 / 退缩（跟着石块走）；
    #   凸角用到两条边的距离合成圆角；倒角里法线往外倾，颜色从泥土渐变到崖壁石头。
    m = kd == 1
    idx = np.where(m)[0]
    cx = np.floor(x[idx] - 1e-6).astype(int); cz = np.floor(z[idx] - 1e-6).astype(int)
    fx = x[idx] - cx; fz = z[idx] - cz
    hh = np.vectorize(hc); h0 = hh(cx, cz)
    k_ = len(idx)
    edges = []
    for dx_, dz_, dist, along, seed in ((0, -1, fz, x[idx], 1), (0, 1, 1 - fz, x[idx], 2), (1, 0, 1 - fx, z[idx], 3), (-1, 0, fx, z[idx], 4)):
        ex = hh(cx + dx_, cz + dz_) < h0
        w = BEV0 + (BEV1 - BEV0) * (0.65 * vnoise(along, seed + 20, 0.55) + 0.35 * vnoise(along, seed + 40, 0.13))
        notch = np.clip((vnoise(along, seed + 60, 0.8) - 0.72) / 0.28, 0, 1)        # 偶尔的深缺口
        edges.append((ex, dist / (w + 0.30 * notch), dx_, dz_, along, dist))
    D = np.full(k_, 9.0); alongs = np.zeros(k_); dmn = np.full(k_, 9.0); seedn = np.ones(k_)
    dm = np.full(k_, 9.0)
    for ei, (ex, dn, dx_, dz_, along, dist) in enumerate(edges):
        closer = ex & (dist < dmn); dmn = np.where(closer, dist, dmn); seedn = np.where(closer, ei + 1, seedn)
        dm = np.where(ex, np.minimum(dm, dist), dm)
        dn = np.where(ex, dn, 9.0); better = dn < D
        D = np.where(better, dn, D); alongs = np.where(better, along, alongs)
    acc = np.zeros(k_); dirx = np.zeros(k_); dirz = np.zeros(k_)
    for ex, dn, dx_, dz_, along, dist in edges:
        q_ = np.where(ex, np.clip(1 - dn, 0, 1), 0)
        acc += q_ * q_; dirx += dx_ * q_; dirz += dz_ * q_
    D = np.minimum(D, 1 - np.sqrt(np.clip(acc, 0, 1)))                                # 凸角圆角
    # 顶面分两层：顶岩铺满（世界 xz，周期 TR_PERIOD），泥土离露边内收 INSET0–INSET1（不规则，按两边明暗差推进 / 退缩），
    # 泥土边在顶岩上投一点暗边；倒角只作用在最外圈。沼泽 / 森林的顶岩就是泥土本身。
    xi, zi = x[idx], z[idx]
    if toprock is not None:
        rk = samp(toprock, xi / TR_PERIOD, zi / TR_PERIOD)
        rn2 = samp(trn, xi / TR_PERIOD, zi / TR_PERIOD)
        Nr = np.c_[rn2[:, 0], rn2[:, 2], rn2[:, 1]]; Nr /= np.linalg.norm(Nr, axis=1, keepdims=True)   # 切线 +x、副切线 +z
    else:
        rk = te[idx]; Nr = Ne[idx]
    lr = rk @ [0.299, 0.587, 0.114]; lE = te[idx] @ [0.299, 0.587, 0.114]
    wi = INSET0 + (INSET1 - INSET0) * (0.6 * vnoise(alongs, 81, 0.5) + 0.4 * vnoise(alongs, 82, 0.12))
    wi = wi + 0.15 * np.clip((vnoise(alongs, 83, 0.9) - 0.75) / 0.25, 0, 1)
    se = dm - wi + (lE - lr) * 0.12 + (vnoise2(xi, zi, 85, 0.06) - 0.5) * 0.06
    qe = np.clip((se + 0.015) / 0.03, 0, 1); qe = qe * qe * (3 - 2 * qe)                 # 1 = 泥土
    ao = 1 - 0.35 * np.clip(1 + se / 0.05, 0, 1) * (1 - qe) * (toprock is not None)      # 泥土边在顶岩上的暗边
    D2 = D - (lr - 0.35) * 1.4 + (vnoise2(xi, zi, 9, 0.07) - 0.5) * 0.6                   # 倒角：跟着顶岩明暗 + 碎边
    t = np.clip(1 - D2, 0, 1); t = t * t * (3 - 2 * t)
    dl = np.sqrt(dirx ** 2 + dirz ** 2) + 1e-9
    base = Nr * (1 - qe[:, None]) + Ne[idx] * qe[:, None]
    # 平滑倒角（负责人 2026-11-13）：离露边 r 内法线连续往外倒，到边上正好 45°；r 与墙面同一函数，折线两侧角度一致、亮度平滑过渡
    rr = smooth_r(alongs, seedn)
    sm = np.clip(1 - dm / rr, 0, 1)
    th = np.radians(45) * sm
    outd = np.c_[dirx / dl, np.zeros(k_), dirz / dl]
    nb = np.where((sm > 0)[:, None], bend(base, np.where((sm > 0)[:, None], outd, [0, 0, -1.0]), th), base)   # 细节跟着一起弯过去，不淡出
    nb /= np.linalg.norm(nb, axis=1, keepdims=True)
    N[idx] = nb
    alb[idx] = rk * (1 - qe[:, None]) * ao[:, None] + te[idx] * qe[:, None]
    # 正面顶部也做假倒角：同一条宽度噪声（与顶面南边一致），法线往上倾，与顶面倒角在折线处都约 45°，折线就看不出来
    wS = BEV0 + (BEV1 - BEV0) * (0.65 * vnoise(x, 21, 0.55) + 0.35 * vnoise(x, 41, 0.13))
    wS = (wS + 0.30 * np.clip((vnoise(x, 61, 0.8) - 0.72) / 0.28, 0, 1)) * 0.6
    tw = np.clip(1 - (yt_ - y) / wS - (lc - 0.35) * 0.8, 0, 1); tw = tw * tw * (3 - 2 * tw)
    Nf = Nw + tw[:, None] * np.array([0, 1.0, 0]); Nf /= np.linalg.norm(Nf, axis=1, keepdims=True)
    # 崖壁顶部融合顶岩（沼泽 / 森林即泥土）：顶面倒角是顶岩颜色，墙顶也渐变成顶岩，石头接石头，不夹泥土
    tsrc, tnsrc, tper = (toprock, trn, TR_PERIOD) if toprock is not None else (earth, enm, EARTH_P)
    # 展开取样：把墙面当作顶面越过折线的延续（正面：往南；侧面：往东 / 西），石块能从顶面绕到墙上，折线处贴图连续
    sgn = np.sign(en.reshape(-1, 3)[:, 0])
    edge = np.where(sgn > 0, np.floor(x), np.ceil(x))
    su = np.where(kd == 4, edge + sgn * (yt_ - y), x)
    sv = np.where(kd == 4, z, z - (yt_ - y))
    trw = samp(tsrc, su / tper, sv / tper)
    # 墙顶融合区也换成顶岩（沼泽 / 森林即泥土）的法线：贴在竖面上，切线 +x、副切线 +y
    if tnsrc is not None:
        tnw = samp(tnsrc, su / tper, sv / tper); Ntw = np.c_[tnw[:, 0], tnw[:, 1], -tnw[:, 2]]
    else:
        Ntw = np.tile([0.0, 0.0, -1.0], (n, 1))
    lt = trw @ [0.299, 0.587, 0.114]
    qtop = np.clip((TOPB - (yt_ - y)) / TOPB + (lt - lc) * 1.2 - 0.15 + (vnoise2(uu, y, 91, 0.08) - 0.5) * 0.5, 0, 1); qtop = qtop * qtop * (3 - 2 * qtop)
    # 先混合崖壁 / 顶岩法线，最后再加“往上倾”的假倒角（否则倒角被顶岩法线盖掉，折线处亮度一下跳变）
    Nf2 = Nw * (1 - qtop[:, None]) + Ntw * qtop[:, None]; Nf2 /= np.linalg.norm(Nf2, axis=1, keepdims=True)
    rw = smooth_r(x, np.ones(n)); sw = np.clip(1 - (yt_ - y) / rw, 0, 1); thw = np.radians(45) * sw
    vtop = np.c_[Nf2[:, 0], -Nf2[:, 2], Nf2[:, 1]]                                   # 墙面坐标 → 展开到顶面坐标
    Nf2 = bend(vtop, np.tile([0, 0, -1.0], (n, 1)), np.radians(90) - thw)
    m = kd == 2; N[m] = Nf2[m]; alb[m] = ((tc * (1 - q[:, None]) + te * q[:, None]) * (1 - qtop[:, None]) + trw * qtop[:, None])[m]
    # 侧面：崖壁贴图按 (z, y)，几何法线
    m = kd == 4
    ts = tc; ls = lc
    qs = np.clip((FOOT - (y - yb_)) / FOOT + (le - ls) * 1.2, 0, 1)
    Ng = en.reshape(-1, 3)
    # 侧面也叠法线贴图（切线沿轮廓方向，简化：在几何法线上加法线贴图的 xy 扰动）
    Ns = Ng + np.c_[np.zeros(n), nt[:, 1], np.zeros(n)] * 0.6 + np.c_[np.zeros(n), np.zeros(n), -nt[:, 0]] * 0.6 * np.sign(Ng[:, 0:1]).ravel()[:, None]
    Ns /= np.linalg.norm(Ns, axis=1, keepdims=True) + 1e-9
    Ntside = Ng + np.c_[np.zeros(n), Ntw[:, 1], np.zeros(n)] * 0.8; Ntside /= np.linalg.norm(Ntside, axis=1, keepdims=True)
    Ns2 = Ns * (1 - qtop[:, None]) + Ntside * qtop[:, None]; Ns2 /= np.linalg.norm(Ns2, axis=1, keepdims=True)
    # 侧面顶部同样平滑翻上去：外侧法线 → 往上 45°
    rs = smooth_r(z, np.where(sgn > 0, 3, 4)); ss = np.clip(1 - (yt_ - y) / rs, 0, 1); ths = np.radians(45) * ss
    outs = np.c_[sgn, np.zeros(n), np.zeros(n)]
    outs_ = np.where((np.abs(sgn) > 0)[:, None], outs, [1.0, 0, 0])
    vts = np.c_[tnw[:, 0], tnw[:, 2], tnw[:, 1]] if tnsrc is not None else np.tile([0, 1.0, 0], (n, 1))   # 顶岩细节（顶面坐标）
    bent = bend(vts, outs_, np.radians(90) - ths)
    Ns2 = Ns2 * (1 - ss[:, None]) + bent * ss[:, None]; Ns2 /= np.linalg.norm(Ns2, axis=1, keepdims=True)
    N[m] = Ns2[m]; alb[m] = ((ts * (1 - qs[:, None]) + te * qs[:, None]) * (1 - qtop[:, None]) + trw * qtop[:, None])[m]
    # 崖壁 / 侧面：预览里模拟 Unity 材质上的提亮与加饱和（不改贴图）
    mw = (kd == 2) | (kd == 4)
    lw = alb[mw] @ [0.2126, 0.7152, 0.0722]
    alb[mw] = np.clip((lw[:, None] + (alb[mw] - lw[:, None]) * WALL_SAT) * WALL_GAIN, 0, 1)
    alb = LIFT + alb * (1 - LIFT)
    sh = shadow(np.c_[x, y, z])
    lam = np.clip(N @ L, 0, 1) * sh
    k = 0.30 + 0.22 * (0.5 + 0.5 * N[:, 1]) + 0.78 * lam
    col = np.clip(alb * k[:, None], 0, 1)
    return (col.reshape(Hh, W, 3) * 255).astype(np.uint8)

imgs = [render(*a) for a in SETS]
gap = np.zeros((imgs[0].shape[0], 8, 3), np.uint8)
Image.fromarray(np.concatenate(sum([[im, gap] for im in imgs], [])[:-1], 1)).save(OUT)
print('输出', OUT)
