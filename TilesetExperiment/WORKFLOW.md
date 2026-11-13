# Wort 崖壁贴图：置换、定向延伸、减面与法线烘焙流程

## 当前状态与适用范围

本文件记录三张原始崖壁颜色贴图的实验流程，供后续复现和接入 Unity 时参考。当前选用的组合是：

**原色图 → 网站参数生成基础高度 → 向下 64px 最大高度包络、混合 67% → 0.5 幅度置换 → Collapse 减面到约 512 三角面 → Flat Shading → Cycles 烘焙切线法线。**

这是已生成、已用 Blender 对照渲染的实验结果，**尚未在 Unity 实机验收，不是生产资源验收结论**。

**仓库版本注意：本目录现有三张 `*_Normal_Baked.png` 是较早的基础置换、平滑着色版本，没有本文件后续的 67% 向下延伸和约 512 面 Flat 减面步骤。本次只维护流程文档，不替换这六张图片，不要把现有 PNG 当作本文最新流程产物。** 这些旧版图片由提交 `464b6fda7d739738890c5376a38ac6f6f4d3f255` 加入。

| 岩壁 | 原始图片 | 本目录原色文件 |
|---|---|---|
| 层状岩壁 | IMG_9471.png | IMG_9471_BaseColor.png |
| 灰褐岩壁 | IMG_9485.png | IMG_9485_BaseColor.png |
| 红褐岩壁 | IMG_9473.png | IMG_9473_BaseColor.png |

三张输入均为 512×512。颜色图保持原样，不采用 AI 重新绘制版本，也不叠加额外细节法线。

## 1. 基础高度：复现 NormalMap-Online 的置换参数

参考工具：[NormalMap-Online](https://cpetry.github.io/NormalMap-Online/)。用户提供的置换设置：

- Contrast：**0.26**。
- Blur/Sharp：**-32**。
- Displacement Invert：关闭。
- 网站 3D Preview 的 Displacement 强度：**1**。

本实验通过读取网站源码编写了离线实现，并非网页实际导出。以下规范以**实际生成本次资源的 Python/Blender 算法**为准，不承诺与任一浏览器的网页导出逐字节相同。网站 GPU 的采样、mediump 精度、Canvas 色彩处理和舍入可能不同；未保存对应网页版本的 Git commit，无法声称锁定网站版本。

### 实际计算

1. 将输入 RGB 码值除以 255，按网站公式转灰度：
   `H = 0.2126 R + 0.7152 G + 0.0722 B`。
2. 使用网站 GPU 对比度公式：
   `factor = (1 + 0.26) / (1 - 0.26)`；
   `H = clamp(factor × (H - 0.5) + 0.5, 0, 1)`。
3. 按网站两次一维、每次 9 个采样的滤波复现 Blur/Sharp -32，垂直后水平，周期采样。权重为：
   `[0.051, 0.0918, 0.12245, 0.1531, 0.1633, 0.1531, 0.12245, 0.0918, 0.051]`。
   对应采样间隔为 `-32 / 5` 像素；离线实现采用最近邻像素偏移及周期 `roll`。
4. 保留基础灰度、对比度及两个滤波阶段的 8-bit 量化。

**-32 不是通用高斯滤波的 sigma=32，不能直接用另一款工具的“模糊半径32”替代。** 这些灰度表示从颜色推测的高度，不是测得的真实深度；没有执行颜色去光照。

## 2. 向下定向延伸：64px、67%、无遮罩、无衰减

以步骤 1 的高度图 `H` 为输入。图像坐标 `y` 向下增加，取每个像素正上方 0～64px 内的最大高度，包括它自己：

```text
E(x, y) = max(H(x, y - d)), d = 0, 1, ..., 64
H_out = H + 0.67 × (E - H)
```

实际 NumPy 等价核心：

```python
envelope = np.maximum.reduce([
    np.roll(h, d, axis=0) for d in range(65)
])
h_out = h + 0.67 * (envelope - h)
```

- **方向是上方高值向下传播**，不是左右膨胀，也不是上下双向膨胀。
- 64px 是 512px 图高的 12.5%；这组数值针对当前 512 图。
- 混合比例为 0.67，不是把整个高度直接乘 0.67。
- **没有石缝遮罩，没有距离衰减，没有额外整体模糊。** 不应混入早期另一套带遮罩/衰减算法。
- `np.roll` 使用周期边界：顶部采样越界时从底部接续。它避免边界空洞，但不代表原始岩块纹理已经无缝。
- 用浮点 `H_out` 继续置换；输出供观看的 8-bit 高度 PNG 不应再读回成为新的、重复量化的输入。

这一步使高处向下形成更持续的体积，不会识别“哪一块是真正的石头”。无遮罩时可能把上方岩块延伸进下方岩块，100% 混合还容易产生平台、拖长柱体或新的下沿。67% 是本轮选用的实验值，不是所有岩壁通用的物理规则。

## 3. 实际置换网格

- 基础平面：1×1 单位。
- 网格：**128×128 个四边形**，129×129=16,641 顶点，三角化后32,768面。
- 高度采样：像素中心双线性采样，周期寻址；UV 的竖向与图像行坐标相反。
- 位移：`z = 0.5 × (H_out - 0.5)`。
- **0.5 是单位面宽下，全黑到全白的位移跨度**，不是每张实际图片都一定有0.5的最大最小高度差。

0.5的幅度参考了所读取网站源码的 Cube 边长10和`displacementScale = scale * 5`。但本实验明确使用正方向`+0.5`、固定midlevel `0.5`；网站另有界面符号转换及由边角灰度计算的displacement bias，不能把本实验说成逐项复刻网站Cube的实际位置与方向。固定midlevel只移动网格整体位置，不改变局部坡度。

## 4. Collapse 减面与 Flat Shading

在完成置换后执行 Blender Decimate：

- 模式：**Collapse**。
- Ratio：**0.015625，即1/64**。
- `use_collapse_triangulate = True`。
- 实际应用修改器，不能只改显示设置。
- 对剩余所有面设置 `use_smooth = False`，即 **Flat Shading**。

本轮实际输出：

| 岩壁 | 顶点 | 三角面 | 平滑面数 |
|---|---:|---:|---:|
| 灰褐 IMG_9485 | 288 | **511** | 0 |
| 红褐 IMG_9473 | 282 | **511** | 0 |
| 层状 IMG_9471 | 286 | **512** | 0 |

“512档”是目标比例，实际面数以修改器结果为准，不应把511写成512。减面改变了几何近似，Flat 改变了面法线，两者一起产生硬朗的大面受光；不能把全部变化归因于单独关闭平滑。

这一步不是石块语义分割，Collapse 不保证按原图每块石头的大面组织三角形。可能出现斜切三角面、坡度失真以及局部轮廓偏移。

## 5. Cycles 真正烘焙法线

使用上一步的实际减面网格作为源模型，而不是直接对高度图做梯度转换：

- Blender Cycles，CPU；实验脚本样本数8。
- Selected to Active：开启；源模型和低模均选中，低模为 Active。
- 烘焙目标：1×1平面，烘焙时位于 `z=-0.5`。
- Cage Extrusion：1；Max Ray Distance：2；Margin：0。
- Bake Type：Normal；Normal Space：Tangent。
- 输出：**512×512 PNG，Non-Color，+X/+Y/+Z 切线空间法线**。
- 不追加网站细节法线，不追加25%叠加，不把AO或颜色明暗烘入法线。

文中“高模”指用于向平面传递表面方向的源几何；最终该源几何只有约512面，不应误称仍有数十万面。

## 6. 颜色、打光与真实几何核对

颜色图使用三张原始 BaseColor，不重绘、不去光照。验证需区分：

1. **平面 + 原色图 + 烘焙Normal**：检验最后的法线贴图受光，平面本身不发生置换。
2. **真正置换、减面后的白模**：直接显示同一个源网格，不使用法线贴图；倾斜25°，用于检查真实形体，而不是把颜色图的明暗当成体积。

本轮对照条件：

- 左上光源来向 `(-0.7, +0.5, +1)`；右上为 `(+0.7, +0.5, +1)`，按方向归一化。
- 太阳强度2，角度0.1弧度。**环境光核查更正：**脚本只写了`world.color=(0.12,0.12,0.12)`，但实际`World.use_nodes=True`，该字段不控制本次节点环境光。实际Background节点为线性RGBA `(0.05087608844,0.05087608844,0.05087608844,1)`，Strength=1；不能把0.12记作真实环境光强度。
- 原色、相机、曝光、材质参数与法线强度在双向光之间保持相同。
- 粗糙度0.75；Cycles渲染48 samples，固定seed，关闭去噪；Standard视图变换。
- 三套白模同尺度、统一 `(0.6, 0.6, 0.6)` 材质，左上光、倾斜25°。

最终三套对照按“原始颜色图 / 平面法线左上光 / 平面法线右上光 / 真正高模白模”排列。不能用白模的轮廓或投影证明平面法线也拥有相同能力。

## 7. 已试过但不属于当前组合的路线

- **直接采用网站Normal**：网站Normal参数与置换参数是独立的。当前使用置换源几何烘焙，而非直接使用网站那张Normal。
- **额外25%细节法线叠加**：做过对照，后续决定不采用；最终保留纯烘焙。
- **Blur -8 / -16 / -24**：做过同条件测试；较小模糊会强化亮部边缘，也会强化纹理杂波和暗沟。当前基础高度仍用-32。
- **位移幅度0.25 / 0.125**：做过强度对照，当前仍用0.5。
- **早期16/32/48px、70%、距离衰减与暗缝遮罩的延伸**：与最终64px无遮罩、无衰减、67%最大包络是不同算法，不能拼接参数。
- **64px包络0% / 33% / 100%混合**：作为对照；当前选67%。
- **平滑原网格与较轻减面档**：做过约8191、2047面的Flat对照；当前采用最强减面档，实际511/511/512面。
- **AI灰度候选直接置换**：候选可能仍含光照而非真实高度，出现错位、暗边和细纹；不是这三张原图当前流程的输入。
- **重新程序化建石块并烘焙自洽颜色/法线**：属于另一个独立小样，不是本流程，不能用它替代这三张原图的实验结果。

## 8. 局限与 Unity 接入检查

- 原色图本身包含绘制明暗；从颜色推高度会把部分阴影误当成凹陷，原色与实时受光叠加仍可能偏黑。
- 基础8-bit高度、陡坡、网格采样及减面可能带来台阶、细纹、三角面和锯齿。Flat受光是实验选择，不代表错误几何已自动修复。
- 定向包络可能跨石缝连接相邻岩块；周期寻址不保证视觉无缝。须在多层堆叠、左右拼接、拐角、不同距离和Mipmap下复查。
- 法线只能改变面内受光，不新增真实几何轮廓、遮挡、碰撞或自投影。若需要这些能力，仍需独立处理运行时网格。
- Unity 将输出PNG设为 **Texture Type: Normal map**，不要再次从灰度生成。先用强度1作基准，检查切线、UV、绿通道方向和压缩设置。
- 此前检查的 `Wort/TileSurface` 将法线细节受光权重设为0.35，会弱化结果；接入时可与1做对照，但本次文档上传没有修改Shader或材质。
- 当前自定义Shader没有单独AO输入，不应把网站AO导出误写成已经接入。
- **尚未 Unity 实机验收。** 最终验收应在实际错切相机、真实太阳光、地图拼接和目标运行分辨率下完成。

## 9. Git 范围

`TilesetExperiment/.gitattributes` 仅允许本实验目录PNG按普通Git二进制保存，仓库其他位置继续原有Git LFS策略。

本次只维护 `TilesetExperiment/WORKFLOW.md`，不改六张旧版图片，不改其他文件夹，不更新生产资源。后续若要上传67%延伸+Flat减面的新版PNG，应另行明确版本和替换范围，避免新旧实验同名混淆。

## 10. 精确数值规范：不要自行解释“渐变偏移”

### 10.1 输入与颜色空间

- 输入是上表三张PNG解码后的 **RGB uint8码值**，512×512。Pillow `convert('RGB')`不在本脚本中做ICC变换，不执行sRGB→linear；没有gamma校正、颜色去光照、自动拉伸直方图或逐图归一化。
- `np.array(image)/255.0`及后续计算使用float64。灰度权重作用于上述码值，不是线性光强。
- 与此不同，**渲染**时BaseColor由Blender按`sRGB`读取，Normal为`Non-Color`；这是数据用途不同，不可把渲染色彩转换提前加入高度生成。
- Alpha在高度计算中丢弃；本组三图无透明切孔需求。

### 10.2 每一步量化和九个偏移

定义`Q(x)=np.rint(np.clip(x,0,1)*255)/255`。`np.rint`是最近整数、恰好半整数取偶数（ties-to-even），不是一律向上，也不是截断。

严格顺序：`灰度→Q→对比度→Q→纵向9tap→Q→横向9tap→Q`。每次滤波先完整加权求和，再量化一次，不是每个tap量化。

`k=-4,-3,-2,-1,0,1,2,3,4`，位移是`int(floor(k*(-32)/5+0.5))`：

```text
k:       -4   -3   -2  -1  0   1    2    3    4
roll:    26   19   13   6  0  -6  -13  -19  -26
weight: .051 .0918 .12245 .1531 .1633 .1531 .12245 .0918 .051
```

- 图像行`y`向下，列`x`向右。纵向用`axis=0`，横向用`axis=1`。
- `roll(A,s)[y,x]=A[(y-s) mod 512,x]`（纵向）；正偏移表示内容向下移动。
- 两遍均周期取样，不用Clamp、镜像或补零。对称权重意味着该模糊核正负整体翻转等效，但必须保留实际偏移与量化顺序。
- `-32/5=-6.4`是采样步长，不是32像素Gaussian sigma；tap最大偏移26px。正数Blur/Sharp对应网站另一条锐化分支，本规范不实现它。

### 10.3 67%不是距离衰减，也不是重新缩放高度

准确公式（索引为`[y,x]`）：

```text
E[y,x] = max(H[(y-d) mod 512,x] for d in 0..64)  # 65个位置，包括自己
H67[y,x] = H[y,x] + 0.67 * (E[y,x] - H[y,x])
         = 0.33 * H[y,x] + 0.67 * E[y,x]
```

这里只有混合权重0.67，**没有随d变化的渐变权重**。它是单向最大值包络（定向膨胀）再与原图混合。不能替换为整体向下平移、平均模糊、每移一步乘0.67、只采样上方64px单一点，或迭代64次扩散。

H67不再8-bit量化。保留float64 `.npy`直接供网格采样。供观看的高度PNG用了`uint8(clip(H67,0,1)*255)`，这是非负值**截断**，与上面的Q不同。若改用PNG再读取，会引入新误差；故PNG只用于检视。

### 10.4 网格坐标、UV和高度采样

本实验不用Blender Displace修改器，直接写顶点坐标；数值等价于strength=0.5、midlevel=0.5：

```text
i,j = 0..128；u=i/128，v=j/128
X=u-0.5，Y=v-0.5，Z=0.5*(bilinear(H67,u,v)-0.5)
纹理连续坐标：x=u*512-0.5，y=(1-v)*512-0.5
```

双线性四邻均以`%512`周期取样。`i`在内层、`j`在外层；顶点编号`j*129+i`。四边形绕序是`(j*129+i, j*129+i+1, (j+1)*129+i+1, (j+1)*129+i)`，法线初始朝+Z。对象位置/旋转为0、缩放为1；高模没有UV，高模烘焙只依赖表面几何和射线。

目标平面由`primitive_plane_add(size=1,location=(0,0,-0.5))`创建。其四顶点为`(-.5,-.5,0),(.5,-.5,0),(-.5,.5,0),(.5,.5,0)`，唯一面顶点序`[0,1,3,2]`，对应loop UV为`[(0,0),(1,0),(1,1),(0,1)]`。它覆盖整张0..1 UV，几何法线+Z、切线+X、另一切线+Y；不是相机投影烘焙。

### 10.5 Blender版本、烘焙隐含项与存档状态

- 实测Blender **4.3.2**（当前安装的`build_hash`报告Unknown，无法锁定到源码提交）；Python侧NumPy **2.3.5**、Pillow **13.0.0**。换Blender版本可能影响Collapse排序、面数和像素输出。
- `decimate_type='COLLAPSE'`、ratio=0.015625、triangulate开启；不设顶点组、不设对称约束、不做额外Weld/Remesh/Dissolve。源网格减面前和应用修改器后都关闭所有面的smooth；不添加Weighted Normal或其他法线修改器。
- `bake.use_cage=False`：没有自建Cage对象；`cage_extrusion=1`仍按设置用于自动射线起点。`max_ray_distance=2`、`margin=0`、`use_clear=True`、`target='IMAGE_TEXTURES'`，通道`POS_X/POS_Y/POS_Z`。Margin类型默认ADJACENT_FACES，但margin=0没有扩边。
- 目标Image使用`alpha=False,float_buffer=False`，RGB 8-bit PNG（实际depth=24），Non-Color；不再反转R/G/Height。截图中网站Normal选项不参与这张烘焙Normal。
- 相机和灯光不参与NORMAL bake；只有源/目标几何、UV、法线空间和射线设置有关。
- `.blend`保存前脚本把低模移回`z=0`，方便预览；**要在该文件中重烘焙，先把低模恢复到z=-0.5**。源网格不移动。勿用两个模型都在z=0的存档状态直接重烘焙。

## 11. 可复制执行的本地复现脚本

只需保存下列两个代码块为脚本，不需要改仓库其他文件。把本目录三张`IMG_*_BaseColor.png`作为输入；输出到独立目录，不覆盖仓库旧版Normal。

```sh
python prepare_exact.py /path/to/TilesetExperiment /path/to/output
blender --factory-startup -b -t 4 --python bake_exact.py -- /path/to/output
```

`prepare_exact.py`：

```python
from pathlib import Path
import sys, hashlib, json
import numpy as np
from PIL import Image
src = Path(sys.argv[1])  # directory containing IMG_*_BaseColor.png
out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
w = [.051,.0918,.12245,.1531,.1633,.1531,.12245,.0918,.051]
def q(a):
    return np.rint(np.clip(a, 0, 1) * 255) / 255
for stem in ['IMG_9485', 'IMG_9473', 'IMG_9471']:
    p = out / stem; p.mkdir(exist_ok=True)
    image = Image.open(src / (stem + '_BaseColor.png')).convert('RGB')
    a = np.array(image) / 255.0
    assert a.shape == (512,512,3)
    h = q(a @ np.array([.2126,.7152,.0722]))
    h = q((1.26/.74) * (h-.5) + .5)
    for axis in [0,1]:
        h = q(sum(weight * np.roll(h, int(np.floor(k*(-32)/5+.5)), axis=axis)
                  for k,weight in zip(range(-4,5),w)))
    e = np.maximum.reduce([np.roll(h,d,axis=0) for d in range(65)])
    final = h + .67 * (e-h)
    np.save(p/'base_height.npy',h)
    np.save(p/'height.npy',final)  # Blender reads this float64, NOT the PNG
    Image.fromarray(np.uint8(np.clip(final,0,1)*255)).save(p/'Height_preview.png')
    image.save(p/'BaseColor.png')
    print(stem, hashlib.sha256(final.astype('<f8').tobytes()).hexdigest())
```

`bake_exact.py`：

```python
import bpy, numpy as np, pathlib, sys, json
root = pathlib.Path(sys.argv[sys.argv.index('--')+1])
for stem in ['IMG_9485','IMG_9473','IMG_9471']:
    p=root/stem; h=np.load(p/'height.npy'); N=128
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    def sample(u,v):
        x=u*512-.5; y=(1-v)*512-.5
        ix=int(np.floor(x)); iy=int(np.floor(y)); fx=x-ix; fy=y-iy
        return (h[iy%512,ix%512]*(1-fx)+h[iy%512,(ix+1)%512]*fx)*(1-fy)+(h[(iy+1)%512,ix%512]*(1-fx)+h[(iy+1)%512,(ix+1)%512]*fx)*fy
    verts=[(i/N-.5,j/N-.5,.5*(sample(i/N,j/N)-.5)) for j in range(N+1) for i in range(N+1)]
    faces=[(j*(N+1)+i,j*(N+1)+i+1,(j+1)*(N+1)+i+1,(j+1)*(N+1)+i) for j in range(N) for i in range(N)]
    m=bpy.data.meshes.new('DisplacedHighMesh'); m.from_pydata(verts,[],faces); m.update()
    hi=bpy.data.objects.new('DisplacedHigh_128x128',m); bpy.context.collection.objects.link(hi)
    for f in m.polygons: f.use_smooth=False
    bpy.context.view_layer.objects.active=hi; hi.select_set(True)
    mod=hi.modifiers.new('ActualCollapseDecimation','DECIMATE')
    mod.decimate_type='COLLAPSE'; mod.ratio=.015625; mod.use_collapse_triangulate=True
    bpy.ops.object.modifier_apply(modifier=mod.name)
    for f in hi.data.polygons: f.use_smooth=False
    hi.data.calc_loop_triangles()
    bpy.ops.mesh.primitive_plane_add(size=1, location=(0,0,-.5))
    lo=bpy.context.object; lo.name='LowPlane_BakeTarget'
    mat=bpy.data.materials.new('BakeTarget'); mat.use_nodes=True; lo.data.materials.append(mat)
    im=bpy.data.images.new(stem+'_Normal',width=512,height=512,alpha=False,float_buffer=False)
    im.colorspace_settings.name='Non-Color'
    node=mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image=im; mat.node_tree.nodes.active=node
    sc=bpy.context.scene; sc.render.engine='CYCLES'; sc.cycles.device='CPU'; sc.cycles.samples=8
    b=sc.render.bake; b.use_selected_to_active=True; b.use_cage=False
    b.cage_extrusion=1; b.max_ray_distance=2; b.margin=0
    b.normal_space='TANGENT'; b.normal_r='POS_X'; b.normal_g='POS_Y'; b.normal_b='POS_Z'
    b.use_clear=True; b.target='IMAGE_TEXTURES'
    bpy.ops.object.select_all(action='DESELECT'); hi.select_set(True); lo.select_set(True)
    bpy.context.view_layer.objects.active=lo; bpy.ops.object.bake(type='NORMAL')
    im.filepath_raw=str(p/'Normal.png'); im.file_format='PNG'; im.save()
    (p/'mesh_stats.json').write_text(json.dumps({'vertices':len(hi.data.vertices),'triangles':len(hi.data.loop_triangles),'smooth_faces':sum(f.use_smooth for f in hi.data.polygons)}))
    lo.location.z=0  # saved preview target only; restore -.5 before rebaking
    bpy.ops.wm.save_as_mainfile(filepath=str(p/'Bake.blend'))
```


## 12. 输入、浮点高度与当前新版Normal的校验

本次用上面两个代码块重新生成三套，在本机同一Blender 4.3.2中验证：**67%浮点高度逐字节一致，三张新版Normal的解码像素及PNG文件字节均与原实验一致，最大像素差0；面数与flat状态一致。** 这验证的是本地算法复现，不是网页输出一致性，也不是跨Blender版本保证。

### 输入PNG SHA-256

- `IMG_9471_BaseColor.png`：`37f6eed71287048746e76f9df9329a7ab41a37712166177b4cf9b207dc0a6632`
- `IMG_9485_BaseColor.png`：`2baeb6e5373b02ba0329b66ddc5d97315456e64cdc07ac81dacd4fe2a743b0fd`
- `IMG_9473_BaseColor.png`：`c785b5634c79e09ad0d0c7cf4795618fac6a0685293aee7e567f4926ddc51b70`

### 高度范围与精度

下表为完整512²像素数组的范围，不是128²网格顶点采样后的极值。Z范围由像素极值代入`0.5*(H67-0.5)`计算；真实网格极值可因采样与减面稍有变化。

| 输入 | 基础H min/max | H67 min/max | 像素对应Z min/max |
|---|---|---|---|
| IMG_9485 | 0.007843137255 / 0.549019607843 | 0.017019607843 / 0.549019607843 | -0.241490196078 / 0.024509803922 |
| IMG_9473 | 0.000000000000 / 0.301960784314 | 0.010470588235 / 0.301960784314 | -0.244764705882 / -0.099019607843 |
| IMG_9471 | 0.000000000000 / 0.431372549020 | 0.015686274510 / 0.431372549020 | -0.242156862745 / -0.034313725490 |

### 数组与新版Normal SHA-256

- 高度hash对`H67.astype('<f8').tobytes()`计算：小端float64、C顺序、不含`.npy`头。
- Normal像素hash对Pillow解码`convert('RGB')`的uint8连续RGB字节计算；文件hash则针对完整PNG。
- 下列是**67% + 约512面Flat新版**，不是仓库现有旧版`IMG_*_Normal_Baked.png`。不要拿新hash去断言旧版文件损坏。

**IMG_9485**

- H67：`db71b7e5620c1a074cb8015317b1a32440907e9a6866fc71a60793172a3e3f49`
- Normal PNG：`295fd25c14b7c620e361231fbb79b248e5b473a52d968b35b252667ccd9629f4`
- Normal RGB像素：`efb03034bc4a197a4dbb4e0d6897c94d1b2fa50d51893cd4ef6c0ebd45225774`
- 实际网格：288顶点 / 511三角面 / 0平滑面。

**IMG_9473**

- H67：`c3a464a42731dd516e3905d2ee19cddda0b6df94d137265111f18332e7d282a9`
- Normal PNG：`0dc7e5163ec1281293d4abb2e136b0f025d88ca60141901c0fa383d46d1f961d`
- Normal RGB像素：`34d00fc9dadc1c8e6062c557421f353924a5c771881b1b791fd60d57ffb041b9`
- 实际网格：282顶点 / 511三角面 / 0平滑面。

**IMG_9471**

- H67：`f1a558c03c153f6f39bcc3ac9d5b08356a37e464495abb0f2a5fdf896a8b744c`
- Normal PNG：`594ff6d62ff6b10e652979517e06d7923b99653ea1ea7e4c4fb1290f3908d691`
- Normal RGB像素：`14ceac523f026135f480f14120d6510deeecc9bdb3986f58d5dd67c08244d3d1`
- 实际网格：286顶点 / 512三角面 / 0平滑面。


## 13. 可复现的打光/白模设置

法线烘焙无需相机；以下仅用于重现最后三套对照的渲染。下列渲染脚本也已实际运行，左上光、右上光和真实高模三张输出与原始实验渲染逐像素相同（最大通道差0）。原始渲染使用Blender默认Principled BSDF：Metallic=0、IOR=1.5、Specular IOR Level=0.5、Alpha=1，Roughness显式设0.75。Normal Map节点Tangent、Strength=1。Image Texture默认Linear插值、Repeat扩展；BaseColor=sRGB，Normal=Non-Color。

- 三套平面中心Y分别1.2、0、-1.2，均1×1单位。
- 相机在(0,0,5)，旋转(0,0,0)，Orthographic Scale=3.6；输出600×1800，100%。相机沿-Z看XY平面。
- Cycles CPU，48 samples，seed=0，denoising=False；Standard view、look=None、Exposure=0、Gamma=1、显示设备sRGB。不声称这与Unity色调映射一致。
- Sun来向用`(-Vector(source)).to_track_quat('-Z','Y')`，源方向分别(-.7,.5,1)与(.7,.5,1)。只改方向，Energy=2、angle=.1弧度。
- 白模从实际`Bake.blend`加载`DisplacedHigh*`对象，不加载低模；绕自身X轴+25°，材质BaseColor=(.6,.6,.6,1)，无图像Normal。原高度的Z中点不重新居中，左上光，其他设置相同。
- 最后排版从三个600×1800渲染中按`(0, row*600, 600, (row+1)*600)`截取三行；原图缩为500×500放进600×600底板中央。排版不参与贴图计算。

保存为`render_exact.py`，运行：

```sh
blender --factory-startup -b -t 4 --python render_exact.py -- /path/to/output
```

```python
import bpy,pathlib,math,sys
from mathutils import Vector
root=pathlib.Path(sys.argv[sys.argv.index('--')+1])
sets=[('Gray',root/'IMG_9485'),('Red',root/'IMG_9473'),('Layered',root/'IMG_9471')]

def scene(geometry=False):
 bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
 for k,(name,p) in enumerate(sets):
  y=(1-k)*1.2
  if geometry:
   with bpy.data.libraries.load(str(p/'Bake.blend'),link=False) as (src,dst):dst.objects=[n for n in src.objects if n.startswith('DisplacedHigh')]
   ob=dst.objects[0];bpy.context.collection.objects.link(ob);ob.location=(0,y,0);ob.rotation_euler.x=math.radians(25);ob.data.materials.clear()
  else:
   bpy.ops.mesh.primitive_plane_add(size=1,location=(0,y,0));ob=bpy.context.object
  mat=bpy.data.materials.new(name+str(geometry));mat.use_nodes=True;n=mat.node_tree.nodes;l=mat.node_tree.links;bs=n.get('Principled BSDF');bs.inputs['Roughness'].default_value=.75
  if geometry:bs.inputs['Base Color'].default_value=(.6,.6,.6,1)
  else:
   tx=n.new('ShaderNodeTexImage');tx.image=bpy.data.images.load(str(p/'BaseColor.png'),check_existing=True);l.new(tx.outputs['Color'],bs.inputs['Base Color'])
   tx=n.new('ShaderNodeTexImage');tx.image=bpy.data.images.load(str(p/'Normal.png'),check_existing=True);tx.image.colorspace_settings.name='Non-Color';nm=n.new('ShaderNodeNormalMap');l.new(tx.outputs['Color'],nm.inputs['Color']);l.new(nm.outputs['Normal'],bs.inputs['Normal'])
  ob.data.materials.append(mat)
 bpy.ops.object.light_add(type='SUN');light=bpy.context.object;light.data.energy=2;light.data.angle=.1
 sc=bpy.context.scene;sc.world.color=(.12,.12,.12)
 sc.world.use_nodes=True
 bg=sc.world.node_tree.nodes.get('Background')
 bg.inputs['Color'].default_value=(.05087608844041824,)*3+(1,)
 bg.inputs['Strength'].default_value=1
 bpy.ops.object.camera_add(location=(0,0,5));cam=bpy.context.object;cam.data.type='ORTHO';cam.data.ortho_scale=3.6;sc.camera=cam
 sc.render.engine='CYCLES';sc.cycles.device='CPU';sc.cycles.samples=48;sc.cycles.use_denoising=False;sc.cycles.seed=0;sc.render.resolution_x=600;sc.render.resolution_y=1800;sc.render.resolution_percentage=100
 sc.view_settings.view_transform='Standard';sc.render.image_settings.file_format='PNG'
 for direction,source in ([('HighGeometry',(-.7,.5,1))] if geometry else [('UpperLeft',(-.7,.5,1)),('UpperRight',(.7,.5,1))]):
  light.rotation_euler=(-Vector(source)).to_track_quat('-Z','Y').to_euler();sc.render.filepath=str(root/f'Three_{direction}_raw.png');bpy.ops.render.render(write_still=True)
scene(False);scene(True)
```

## 14. 仍不能保证的部分

1. 没有锁定NormalMap-Online仓库commit，也没有用浏览器导出同输入作逐像素验证；上述代码是**本实验的确定性规范**，不是对所有网站版本/GPU的保证。
2. 当前Blender安装报告build hash Unknown；不同Blender编译版本、CPU/GPU或依赖版本可能改变Collapse拓扑与烘焙/渲染细节。先用高度hash和面数定位差异，再比法线像素hash；PNG元数据变化不等于像素变化。
3. 原始图案与AI生成候选无关。此流程不理解真实岩石深度，周期取样也不保证图案无缝；原贴图绘制阴影、跨缝延伸、三角面受光等限制仍存在。
4. 所有脚本只复现独立输出；本次文档更新没有上传新版法线，没有修改仓库其他文件，也没有改变Unity Shader、材质或导入设置。
