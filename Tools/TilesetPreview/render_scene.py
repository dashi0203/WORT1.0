"""
不开 Unity，按 Wort 的游戏相机（正交俯视 + 纵向错切：屏幕 v = z + y）软件渲染一个 Tileset 示例场景，交付前自查用。
读场景、Prefab、网格（.asset 文本）、材质、贴图（Git LFS 会自动取出），光照方向取场景里的平行光。
认识 Wort/TileSurface 的规则：法线贴图只占 _DetailNormal 的光照、_Projection = 1 世界 xz 投影、_SeamOn 唇边接缝融合。
只是近似（没有阴影、没有 URP 的完整光照），最终以 Unity 为准。

用法：python3 Tools/TilesetPreview/render_scene.py 提交|WT 场景路径 输出.png [shearx=0] [每米像素=48] [裁剪 x0,z0,x1,z1]
  或：PLACEMENTS=tiles.json python3 ... WT Tileset目录 输出.png ...（按 TerrainPreview --tiles 输出的瓦片清单拼，验证选件）
  提交 = git 提交号（读该提交里的文件）；WT = 读工作区（含未提交的文件）。
  shearx ≠ 0 时横向也错切，能看见东西两侧（只是斜着看，不是游戏视角）。
  环境变量 LIGHT="x,y,z" 覆盖光照方向（指向光源），用来检查换方向后会不会两套光打架。
例：python3 Tools/TilesetPreview/render_scene.py WT Assets/Wort/Art/Tilesets/Limestone/Limestone_Showcase.unity out.png 0 100
需要：python3、numpy、Pillow、git-lfs。
"""
import sys, re, subprocess, struct, math, io, os
import numpy as np
from PIL import Image

PLACEMENTS = os.environ.get('PLACEMENTS')  # 瓦片清单 JSON（TerrainPreview --tiles 输出）；这时 SCENE_PATH 写 Tileset 目录
commit, scene_path, out = sys.argv[1], sys.argv[2], sys.argv[3]
shearx = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0
ppm = float(sys.argv[5]) if len(sys.argv) > 5 else 48.0
crop = [float(v) for v in sys.argv[6].split(',')] if len(sys.argv) > 6 else None
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))

import os
LIGHT = os.environ.get('LIGHT')  # 覆盖光照方向（指向光源）"x,y,z"
def show(path, binary=False):
    if commit == 'WT':
        data = open(os.path.join(REPO, path), 'rb').read()
    else:
        data = subprocess.run(['git', 'show', f'{commit}:{path}'], cwd=REPO, capture_output=True).stdout
    return data if binary else data.decode('utf-8', 'replace')

# guid -> 路径
files = (subprocess.run(['git', 'ls-files', '--cached', '--others', '--exclude-standard', 'Assets/Wort'], cwd=REPO, capture_output=True, text=True).stdout.split() + [f for f in ['Assets/Wort/Rendering/Shaders/TileSurface.shader.meta'] if os.path.exists(os.path.join(REPO, f))]) if commit == 'WT' else subprocess.run(['git', 'ls-tree', '-r', '--name-only', commit, 'Assets/Wort'], cwd=REPO, capture_output=True, text=True).stdout.split()
guid2path = {}
for f in files:
    if f.endswith('.meta'):
        m = re.search(r'guid: ([0-9a-f]{32})', show(f))
        if m: guid2path[m.group(1)] = f[:-5]

def docs(text):
    out = {}
    for part in re.split(r'^--- ', text, flags=re.M)[1:]:
        head, _, body = part.partition('\n')
        m = re.match(r'!u!(\d+) &(-?\d+)( stripped)?', head)
        if m: out[m.group(2)] = (int(m.group(1)), body, bool(m.group(3)))
    return out

def vec(body, key):
    m = re.search(key + r': \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)(?:, w: ([-\d.e]+))?\}', body)
    if not m: return None
    return [float(v) for v in m.groups() if v is not None]

def qrot(q, v):
    x, y, z, w = q
    # v' = q v q*
    ux, uy, uz = x, y, z
    tx = 2 * (uy * v[2] - uz * v[1]); ty = 2 * (uz * v[0] - ux * v[2]); tz = 2 * (ux * v[1] - uy * v[0])
    return [v[0] + w * tx + (uy * tz - uz * ty), v[1] + w * ty + (uz * tx - ux * tz), v[2] + w * tz + (ux * ty - uy * tx)]

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz]

scene = docs(show(scene_path)) if not PLACEMENTS else {}
# 普通 Transform
tr = {}
for fid, (cls, body, stripped) in scene.items():
    if cls == 4 and not stripped:
        father = re.search(r'm_Father: \{fileID: (-?\d+)\}', body).group(1)
        tr[fid] = (vec(body, 'm_LocalPosition'), vec(body, 'm_LocalRotation'), vec(body, 'm_LocalScale'), father)
def world(fid):
    if fid == '0' or fid not in tr: return [0, 0, 0], [0, 0, 0, 1]
    p, q, s, f = tr[fid]
    pp, pq = world(f)
    return [a + b for a, b in zip(pp, qrot(pq, p))], qmul(pq, q)

# 光源方向（Directional Light 的前方）
light = [0.35, -0.85, 0.45]
for fid, (cls, body, stripped) in scene.items():
    if cls == 108:
        go = re.search(r'm_GameObject: \{fileID: (-?\d+)\}', body).group(1)
        for t, (c2, b2, s2) in scene.items():
            if c2 == 4 and not s2 and re.search(r'm_GameObject: \{fileID: ' + go + r'\}', b2):
                _, q = world(t); light = qrot(q, [0, 0, 1])
L = np.array([-light[0], -light[1], -light[2]]) if not LIGHT else np.array([float(v) for v in LIGHT.split(',')]); L /= np.linalg.norm(L)
print('光照方向（指向光源）', L.round(3))

prefab_cache, mesh_cache, mat_cache, tex_cache = {}, {}, {}, {}
def tex(guid):
    if guid not in tex_cache:
        raw = show(guid2path[guid], True)
        data = subprocess.run(['git', 'lfs', 'smudge'], input=raw, cwd=REPO, capture_output=True).stdout if raw.startswith(b'version https://git-lfs') else raw
        tex_cache[guid] = np.asarray(Image.open(io.BytesIO(data)).convert('RGB'), dtype=np.float32) / 255.0
    return tex_cache[guid]
def material(guid):
    if guid not in mat_cache:
        t = show(guid2path[guid])
        def texguid(name):
            m = re.search(r'- ' + name + r':\n\s+m_Texture: \{fileID: \d+(?:, guid: ([0-9a-f]{32}))?', t)
            return m.group(1) if m and m.group(1) else None
        def tiling(name):
            m = re.search(r'- ' + name + r':\n\s+m_Texture: [^\n]+\n\s+m_Scale: \{x: ([-\d.e]+), y: ([-\d.e]+)\}\n\s+m_Offset: \{x: ([-\d.e]+), y: ([-\d.e]+)\}', t)
            return [float(v) for v in m.groups()] if m else [1, 1, 0, 0]
        col = re.search(r'- _BaseColor: \{r: ([-\d.e]+), g: ([-\d.e]+), b: ([-\d.e]+)', t)
        bs = re.search(r'- _BumpScale: ([-\d.e]+)', t)
        def fl(name, d):
            m = re.search(r'- ' + name + r': ([-\d.e]+)', t)
            return float(m.group(1)) if m else d
        tile = 'TileSurface' in guid2path.get(re.search(r'm_Shader: \{fileID: \d+, guid: ([0-9a-f]{32})', t).group(1), '')
        mat_cache[guid] = dict(base=texguid('_BaseMap'), bump=texguid('_BumpMap'), st=tiling('_BaseMap'),
                               tile=tile, detail=fl('_DetailNormal', 0.35), proj=fl('_Projection', 0), wtile=fl('_WorldTile', 1),
                               seam=fl('_SeamOn', 0), seamcliff=texguid('_SeamCliff'), seamlip=texguid('_SeamLip'), lipy=fl('_LipY', 0.88), seamw=fl('_SeamWidth', 0.09), seamnoise=fl('_SeamNoise', 2.2), lipshadow=fl('_LipShadow', 0.22), shadowdepth=fl('_ShadowDepth', 0.2),
                               color=[float(v) for v in col.groups()] if col else [1, 1, 1], bumpscale=float(bs.group(1)) if bs else 1.0,
                               name=guid2path[guid].split('/')[-1])
    return mat_cache[guid]
def mesh(guid):
    if guid in mesh_cache: return mesh_cache[guid]
    t = show(guid2path[guid])
    vc = int(re.search(r'm_VertexCount: (\d+)', t).group(1))
    chans = re.findall(r'- stream: (\d+)\n\s+offset: (\d+)\n\s+format: (\d+)\n\s+dimension: (\d+)', t)
    data = bytes.fromhex(re.search(r'_typelessdata: ([0-9a-f]*)', t).group(1))
    stride = sum(int(c[3]) * 4 for c in chans if int(c[3]) > 0 and int(c[0]) == 0)
    def ch(k):
        s, o, f, d = [int(v) for v in chans[k]]
        if d == 0: return None
        a = np.zeros((vc, d), np.float32)
        for i in range(vc): a[i] = struct.unpack_from('<' + 'f' * d, data, i * stride + o)
        return a
    pos, nor, tan, uv = ch(0), ch(1), ch(2), ch(4)
    idx = bytes.fromhex(re.search(r'm_IndexBuffer: ([0-9a-f]*)', t).group(1))
    fmt = int(re.search(r'm_IndexFormat: (\d+)', t).group(1))
    ind = np.frombuffer(idx, np.uint16 if fmt == 0 else np.uint32).astype(np.int64)
    subs = [(int(a) // (2 if fmt == 0 else 4), int(b)) for a, b in re.findall(r'firstByte: (\d+)\n\s+indexCount: (\d+)', t)]
    mesh_cache[guid] = (pos, nor, tan, uv, ind, subs)
    return mesh_cache[guid]
def prefab(guid):
    if guid not in prefab_cache:
        t = show(guid2path[guid])
        mg = re.search(r'm_Mesh: \{fileID: \d+, guid: ([0-9a-f]{32})', t).group(1)
        mats = re.findall(r'- \{fileID: 2100000, guid: ([0-9a-f]{32}), type: 2\}', t)
        prefab_cache[guid] = (mg, mats)
    return prefab_cache[guid]

# 实例
inst = []
for fid, (cls, body, stripped) in scene.items():
    if cls != 1001: continue
    parent = re.search(r'm_TransformParent: \{fileID: (-?\d+)\}', body).group(1)
    src = re.search(r'm_SourcePrefab: \{fileID: \d+, guid: ([0-9a-f]{32})', body).group(1)
    mods = dict(re.findall(r'propertyPath: (m_Local\w+\.\w)\n\s+value: ([-\d.e]+)', body))
    p = [float(mods.get('m_LocalPosition.' + a, 0)) for a in 'xyz']
    q = [float(mods.get('m_LocalRotation.' + a, 0 if a != 'w' else 1)) for a in 'xyzw']
    pp, pq = world(parent)
    inst.append((src, [a + b for a, b in zip(pp, qrot(pq, p))], qmul(pq, q)))
if PLACEMENTS:
    import json
    path2guid = {v: k for k, v in guid2path.items()}
    for p in json.load(open(PLACEMENTS)):
        sub = 'Top' if p['name'].startswith('Top_') else 'Cliff'
        g = path2guid.get(f"{scene_path}/Prefabs/{sub}/{p['name']}.prefab")
        if g is None: print('缺瓦片', p['name']); continue
        a = math.radians(90 * p['rot']) / 2
        inst.append((g, [p['x'], p['y'], p['z']], [0, math.sin(a), 0, math.cos(a)]))
print('实例', len(inst))

tris = []  # (3x3 世界坐标, 3x2 uv, 3x3 法线, 3x4 切线, 材质)
for src, p, q in inst:
    mg, mats = prefab(src)
    pos, nor, tan, uv, ind, subs = mesh(mg)
    R = np.array([qrot(q, e) for e in ([1, 0, 0], [0, 1, 0], [0, 0, 1])]).T
    wp = pos @ R.T + np.array(p); wn = nor @ R.T
    wt = tan[:, :3] @ R.T if tan is not None else None
    for si, (first, cnt) in enumerate(subs):
        if cnt == 0 or si >= len(mats): continue
        m = material(mats[si])
        for k in range(first, first + cnt, 3):
            a, b, c = ind[k], ind[k + 1], ind[k + 2]
            tris.append((wp[[a, b, c]], uv[[a, b, c]] if uv is not None else np.zeros((3, 2)), wn[[a, b, c]],
                         (wt[[a, b, c]], tan[[a, b, c], 3]) if wt is not None else None, m))
print('三角形', len(tris))
allp = np.concatenate([t[0] for t in tris])
x0, z0, x1, z1 = (allp[:, 0].min() - 1, allp[:, 2].min() - 1, allp[:, 0].max() + 1, allp[:, 2].max() + 1) if crop is None else crop
ymax = allp[:, 1].max(); print('范围 x', allp[:,0].min().round(2), allp[:,0].max().round(2), 'y', allp[:,1].min().round(2), ymax.round(2), 'z', allp[:,2].min().round(2), allp[:,2].max().round(2))
def proj(P):  # 屏幕 (u, v)：u = x − y·shearx，v = z + y
    return P[:, 0] - P[:, 1] * shearx, P[:, 2] + P[:, 1]
us, vs = proj(allp)
u0 = min(us.min(), x0) if crop is None else x0 - ymax * max(shearx, 0)
u1 = max(us.max(), x1) if crop is None else x1 - ymax * min(shearx, 0)
v0 = vs.min() if crop is None else z0
v1 = vs.max() if crop is None else z1 + ymax
W, H = int((u1 - u0) * ppm), int((v1 - v0) * ppm)
img = np.zeros((H, W, 3), np.float32); img[:] = [0.10, 0.14, 0.19]
depth = np.full((H, W), -1e9, np.float32)
for P, UV, N, T, m in tris:
    su, sv = proj(P)
    px = (su - u0) * ppm; py = H - (sv - v0) * ppm
    xa, xb = int(max(0, math.floor(px.min()))), int(min(W - 1, math.ceil(px.max())))
    ya, yb = int(max(0, math.floor(py.min()))), int(min(H - 1, math.ceil(py.max())))
    if xa > xb or ya > yb: continue
    gx, gy = np.meshgrid(np.arange(xa, xb + 1) + 0.5, np.arange(ya, yb + 1) + 0.5)
    d = (px[1] - px[0]) * (py[2] - py[0]) - (py[1] - py[0]) * (px[2] - px[0])
    if abs(d) < 1e-9: continue
    w1 = ((gx - px[0]) * (py[2] - py[0]) - (gy - py[0]) * (px[2] - px[0])) / d
    w2 = ((px[1] - px[0]) * (gy - py[0]) - (py[1] - py[0]) * (gx - px[0])) / d
    w0 = 1 - w1 - w2
    ins = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
    # 背面剔除：几何法线朝后（z+ 朝北看不见、朝下）不画
    fn = np.cross(P[1] - P[0], P[2] - P[0]); fn /= (np.linalg.norm(fn) + 1e-12)
    if np.dot(fn, N.mean(0)) < 0: fn = -fn
    view = np.array([shearx, 1.0, -1.0])  # 朝镜头的方向（大致）
    if np.dot(fn, view) <= 0: continue
    dep = w0 * (P[0, 1] - P[0, 2] * 1e-3) + w1 * (P[1, 1] - P[1, 2] * 1e-3) + w2 * (P[2, 1] - P[2, 2] * 1e-3)
    sub = depth[ya:yb + 1, xa:xb + 1]
    ok = ins & (dep > sub)
    if not ok.any(): continue
    sub[ok] = dep[ok]
    ww = [w[ok] for w in (w0, w1, w2)]
    uvp = sum(ww[i][:, None] * UV[i] for i in range(3))
    uvraw = uvp.copy()
    wpos = sum(ww[i][:, None] * P[i] for i in range(3))
    if m.get('tile') and m['proj'] > 0.5: uvp = wpos[:, [0, 2]] / m['wtile']
    st = m['st']; uvp = uvp * np.array(st[:2]) + np.array(st[2:])
    nrm = sum(ww[i][:, None] * N[i] for i in range(3)); nrm /= np.linalg.norm(nrm, axis=1, keepdims=True) + 1e-9
    col = np.ones((len(uvp), 3)) * np.array(m['color'])
    if m['base']:
        t = tex(m['base']); th, tw = t.shape[:2]
        ix = (np.floor(uvp[:, 0] * tw).astype(int)) % tw; iy = (th - 1 - np.floor(uvp[:, 1] * th).astype(int)) % th
        col = t[iy, ix] * np.array(m['color'])
    lipshade = np.ones(len(uvp))
    if m.get('tile') and m.get('seam', 0) > 0.5:
        def samp(g, uu, vv):
            t2 = tex(g); th, tw = t2.shape[:2]
            return t2[(th - 1 - np.floor(vv * th).astype(int)) % th, (np.floor(uu * tw).astype(int)) % tw]
        d = uvraw[:, 1] - m['lipy']
        cc = samp(m['seamcliff'], uvraw[:, 0], uvraw[:, 1]); lc = samp(m['seamlip'], uvraw[:, 0], m['lipy'] + np.abs(d) + 0.005)
        hc = cc @ np.array([0.299, 0.587, 0.114])
        P = np.clip(0.5 + d / m['seamw'] + (hc - 0.3) * m['seamnoise'], 0, 1); P = P * P * (3 - 2 * P)
        zone = (np.abs(d) < m['seamw'] * 2.5) | ((d < 0) & (-d < m['shadowdepth']))
        col = np.where(zone[:, None], cc * (1 - P[:, None]) + lc * P[:, None], col)
        sm = np.clip(np.maximum(-d, 0) / m['shadowdepth'], 0, 1); sm = sm * sm * (3 - 2 * sm)
        lipshade = np.where(zone, 1 - m['lipshadow'] * (1 - P) * (1 - sm), 1)
    ngeo = nrm.copy()
    if m['bump'] and T is not None:
        t = tex(m['bump']); th, tw = t.shape[:2]
        ix = (np.floor(uvp[:, 0] * tw).astype(int)) % tw; iy = (th - 1 - np.floor(uvp[:, 1] * th).astype(int)) % th
        tn = t[iy, ix] * 2 - 1; tn[:, :2] *= m['bumpscale']
        tg = sum(ww[i][:, None] * T[0][i] for i in range(3)) if not (m.get('tile') and m['proj'] > 0.5) else np.tile([1.0, 0, 0], (len(nrm), 1)); tg -= nrm * (tg * nrm).sum(1, keepdims=True); tg /= np.linalg.norm(tg, axis=1, keepdims=True) + 1e-9
        bt = np.cross(nrm, tg) * T[1][0]
        nrm = tg * tn[:, :1] + bt * tn[:, 1:2] + nrm * tn[:, 2:3]; nrm /= np.linalg.norm(nrm, axis=1, keepdims=True) + 1e-9
    lam = np.clip(nrm @ L, 0, 1)
    if m.get('tile'): lam = np.clip(ngeo @ L, 0, 1) * (1 - m['detail']) + lam * m['detail']; nrm = ngeo
    sky = 0.5 + 0.5 * nrm[:, 1]
    k = (0.32 + 0.25 * sky + 0.75 * lam) * lipshade
    sub_img = img[ya:yb + 1, xa:xb + 1]
    sub_img[ok] = np.clip(col * k[:, None], 0, 1)
Image.fromarray((img * 255).astype(np.uint8)).save(out)
print('输出', out, W, H)
