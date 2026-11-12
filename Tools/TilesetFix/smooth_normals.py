"""
平滑法线（负责人 2026-11-12：像老 PSX 游戏那样，让顶面 → 唇边 → 崖壁的光照连续过渡，不要求几何上完全一致）。
直接改 Unity 网格资源（.asset 文本里的 _typelessdata，float32，每顶点 位置 / 法线 / 切线 / UV）：
- 崖顶墙件（*_Crown_*，本地 +y 向上、正面朝 −z）：唇边最上一行（y = 1）法线 = 朝上与朝前各一半；y = 0.88 行稍朝上；y = 0.62 行带一点；
  只改 y / z 分量（x 保留墙面起伏），切线（沿 x）仍与法线正交。
- 顶面瓦片（Top_E*_C*，y = 0，原点西南角）：离露边（E 位：东 1、北 2、西 4、南 8）8cm 以内的顶点，法线往外倾，越靠边越倾（最多朝上 0.6、朝外 0.8）；
  角上两边都露时朝两个方向的合；切线做一次正交化。
用法：python3 smooth_normals.py 网格目录   （可重复运行：按位置重新设定，不叠加）
"""
import os, re, struct, sys
import numpy as np

def load(path):
    t = open(path).read()
    m = re.search(r'_typelessdata: ([0-9a-f]*)', t)
    ch = re.findall(r'- stream: (\d+)\n\s+offset: (\d+)\n\s+format: (\d+)\n\s+dimension: (\d+)', t)
    vc = int(re.search(r'm_VertexCount: (\d+)', t).group(1))
    stride = sum(int(c[3]) * 4 for c in ch if int(c[3]) > 0 and c[0] == '0')
    assert all(c[2] == '0' for c in ch if int(c[3]) > 0), '只支持 float32 通道'
    data = bytearray(bytes.fromhex(m.group(1)))
    return t, m, data, vc, stride, int(ch[1][1]), int(ch[2][1])

def save(path, t, m, data):
    open(path, 'w').write(t[:m.start(1)] + data.hex() + t[m.end(1):])

def norm(v):
    v = np.asarray(v, float); return v / np.linalg.norm(v)

def crown(path):
    t, m, data, vc, stride, on, ot = load(path)
    rows = {1.0: (0.7, 0.71), 0.88: (0.3, 0.95), 0.62: (0.08, 1.0)}  # y → (朝上, 朝前) 权重
    n_set = 0
    for i in range(vc):
        x, y, z = struct.unpack_from('<3f', data, i * stride)
        nx, ny, nz = struct.unpack_from('<3f', data, i * stride + on)
        for ry, (up, fw) in rows.items():
            if abs(y - ry) < 1e-3:
                n = norm([nx * 0.5, up, -fw])
                struct.pack_into('<3f', data, i * stride + on, *n); n_set += 1
    save(path, t, m, data)
    return n_set

EDGE = 0.08

def top(path, emask):
    t, m, data, vc, stride, on, ot = load(path)
    out = []
    n_set = 0
    for i in range(vc):
        x, y, z = struct.unpack_from('<3f', data, i * stride)
        # 顶沿是不规则的石块轮廓（顶点离格边 0–4cm）：离露边越近越往外倾，EDGE 米以外不变
        def wgt(d): return max(0.0, 1 - d / EDGE)
        o = np.zeros(3)
        if emask & 1: o += np.array([1, 0, 0]) * wgt(1 - x)
        if emask & 2: o += np.array([0, 0, 1]) * wgt(1 - z)
        if emask & 4: o += np.array([-1, 0, 0]) * wgt(x)
        if emask & 8: o += np.array([0, 0, -1]) * wgt(z)
        k = min(1.0, float(np.linalg.norm(o)))
        n = norm([0, 1, 0]) if k == 0 else norm(np.array([0, 1 - 0.4 * k, 0]) + 0.8 * k * norm(o))
        tg = np.array(struct.unpack_from('<3f', data, i * stride + ot))
        tg = norm(tg - n * tg.dot(n))
        struct.pack_into('<3f', data, i * stride + on, *n)
        struct.pack_into('<3f', data, i * stride + ot, *tg)
        if o.any(): n_set += 1
    save(path, t, m, data)
    return n_set

d = sys.argv[1]
for f in sorted(os.listdir(d)):
    if not f.endswith('.asset'): continue
    p = os.path.join(d, f)
    if '_Crown_' in f: print(f, '平滑', crown(p), '个顶点')
    mm = re.match(r'Top_E([0-9A-F])(?:_C[0-9A-F])?\.asset', f)
    if mm and mm.group(1) != '0': print(f, '露边顶点', top(p, int(mm.group(1), 16)))
