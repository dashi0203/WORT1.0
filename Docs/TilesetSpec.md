# 模块化地形瓦片（Tileset）交付规格

给制作 Tileset 的人（Codex / 美术）看：做一套新的 Biome 瓦片（沼泽、森林、山地……）时照这里做，交付前用云端渲染脚本自查。
第一套是 `Assets/Wort/Art/Tilesets/Limestone`（人类区，砂土 + 石灰岩），以它为样板；它的 `README.md` 写了拼装规则，本文只写所有 Tileset 共同的要求。
状态：【暂定】负责人 2026-11-12 起改用 3D 模块化瓦片，细节在原型中调整。

## 1. 尺寸与拼装（和体素格对齐）

- 1 格 = 1m，1 层 = 1m（与体素一致）。碰撞仍由体素层负责，瓦片只管外观。
- 顶面瓦片 `Top_E{E}_C{C}`：1m × 1m，原点在西南角、放在该格顶面高度，y = 0 是顶面。
  E = 低于本格的四邻（东 1、北 2、西 4、南 8），C = 四邻不低但斜邻低于本格时补的凹角（东北 1、西北 2、西南 4、东南 8），十六进制。
  共 47 种（E = 0 时 C 0–F；露边时只留合法组合），一套都要有。没有凹角时可以叫 `Top_E8`（等同 `Top_E8_C0`），两种都认。
- 崖壁瓦片：宽 1m、高 1m，原点左下角，沿本地 +X 延伸，正面朝本地 −Z。南面直接放，东西北面绕 Y 轴转 90° 的整数倍。
  最上一层用 `Crown`（带唇边），以下各层用 `Repeat`。`Front`（正面，镜头看得见）/ `Side`（侧面，可以起伏多一点），各 A / B 两个变体。
- 不超出碰撞格：顶面不超出 1m 格；墙面往外鼓 ≤ 0.45m（参见《地形外观与崖面美术规范》第 1 节），越少越好。
- 镜头固定（正交俯视 + 错切），只看得见顶面和朝南的面：细节花在南面和顶沿上，北面可以很简。

## 2. 网格

- 每块墙件约 32 个三角形就够（正面细节靠贴图）。顶面露边那圈要细分（样板约 800–960 个顶点），以后要按世界坐标噪声做轮廓挤出。
- **墙件 UV 纵向 = 件内高度（0 底 … 1 顶），横向 = 沿墙 0 … 1。** Crown 的唇边在 v = 0.88 以上（唇边下沿 0.88）。着色器靠这两条做唇边接缝融合。
- 相邻件接缝处的顶点位置、法线要一致（A / B 混用不裂）。
- 墙件底边（本地 y = 0）中间的顶点不要放在两端以外的特殊位置：贴地的那一块在合并网格时会被往正面推 0.5–4cm 做噪声（`TerrainLab.FootNoise`），两端不动。
- 交付前对网格跑一次平滑法线（PSX 式，让顶面 → 唇边 → 崖壁光照连续过渡）：
  `python3 Tools/TilesetFix/smooth_normals.py Assets/Wort/Art/Tilesets/<名字>/Meshes`（可重复运行）。
  它假设：Crown 唇边顶点行在 y = 1 / 0.88 / 0.62；顶面露边在格边上。网格不是这样时先说一声。

## 3. 贴图

| 贴图 | 用在 | 要求 |
| --- | --- | --- |
| 崖壁 BaseColor + Normal | 墙件崖壁 | 可平铺；明暗可以画进颜色里（光从上方偏前，与游戏太阳一致）；法线只做细节（裂缝、石块边） |
| 唇边 BaseColor + Normal | Crown 唇边 | 只用到 v = 0.88–1.0 这一段；接缝融合会镜像取唇边下沿往上一段，**下沿往上约 0.1 的范围要像唇边本身**（Limestone 的唇边贴图下半部是竖条纹，只能靠镜像避开） |
| 顶沿 BaseColor + Normal | 顶面露边那圈 | 沿边方向可平铺 |
| 顶面 BaseColor（可加 Normal） | 顶面泥土 / 砂土 | **四边无缝平铺**，按世界 xz 投影；A 通道 = 高度（路面交界用，没有就当 0.5） |

- **贴图边缘不能混入别的材质**（Limestone 的砂土左边缘混进了一条石灰岩，平铺后每 1m 一条虚线；修补脚本 `Tools/TilesetFix/fix_leak.py`）。
- 颜色贴图里的明暗是美术的一部分（2D 做法）：实时光只给整块面的明暗，法线贴图只占三成光照。不要把强烈的方向光画进顶面贴图。
- 尺寸：游戏相机 1080p 下约 57 像素 / m，贴图 256–512 像素 / m 足够（Limestone 是 512 / m，偏多）。PNG，走 Git LFS（`.gitattributes` 已设）。

## 4. 材质（一律用 `Wort/TileSurface`，属性名同 URP Lit）

| 材质 | 设置 |
| --- | --- |
| 崖壁（Repeat 墙件） | `_BaseMap` / `_BumpMap` / `_BumpScale`；`_DetailNormal` 0.35 |
| 崖顶崖壁（Crown 墙件的崖壁部分，单独一个材质） | 同上，另加 `_SeamOn` = 1、`_SeamCliff` = 崖壁颜色贴图、`_SeamLip` = 唇边颜色贴图、`_LipY` 0.88、`_SeamWidth` 0.07、`_SeamNoise` 3、`_LipShadow` 0.12、`_ShadowDepth` 0.14 |
| 唇边 | 唇边贴图；接缝参数同上（`_SeamOn` = 1，两张贴图一样） |
| 顶沿 | 顶沿贴图；`_Projection` = 0（网格 UV，跟着瓦片转） |
| 顶面泥土 | `_Projection` = 1（世界 xz）、`_WorldTile` = 一张贴图覆盖几米、`_RoadOn` = 1（路面盖在上面） |

Limestone 的材质（`CharcoalCliff`、`CharcoalCliffCrown`、`PaleLimestoneLip`、`LimestoneRimTop`、`OchreSand`）可以直接复制改贴图。
OliveStrata / RustCrag 的崖顶用一整张画好唇边的 Crown 贴图（单一子网格），这种做法不需要接缝融合，Crown 材质照普通崖壁设置即可；A / B 可以各有材质。
同一套网格可以给不同 Biome 共用，只换材质（例如混交林 / 针叶林）。

## 5. 目录与示例场景

```
Assets/Wort/Art/Tilesets/<名字>/
  Meshes/      Top_E*_C*.asset、{Front,Side}_{Crown,Repeat}_{A,B}.asset
  Materials/   见第 4 节
  Textures/    PNG（LFS）
  Prefabs/     Top/、Cliff/
  <名字>_Showcase.unity   单层十字形 + 1–3 层台地（含外角、凹角、窄臂）
  README.md
```

## 6. 接入

每套建一个 `TilesetAsset`（`<名字>_Tileset.asset`，列出全部 Prefab）；`TerrainLab` 的 `Tilesets` 数组里的下标就是套件编号。
空白测试地（生成器 `TestGround`）里用体素笔刷搭地形、刷套件，见 `Docs/DevLog.md`。

## 7. 交付前自查

1. 云端 / 本地都能跑的渲染脚本（游戏相机，不开 Unity）。也可以按选件结果拼：`dotnet run --project Tools/TerrainPreview -c Release -- --tiles --out out`，
   再 `PLACEMENTS=out/tiles.json python3 Tools/TilesetPreview/render_scene.py WT Assets/Wort/Art/Tilesets/<名字> out.png 0 60`：
   `python3 Tools/TilesetPreview/render_scene.py WT Assets/Wort/Art/Tilesets/<名字>/<名字>_Showcase.unity out.png 0 100`
   看：顶面有没有格间接缝、唇边和崖壁的交界、重复感。换光照方向再看一次：`LIGHT="-0.8,0.45,0.2" python3 ...`。
2. Unity 里打开示例场景：Console 无报错，截图给负责人。
3. 推送到负责人指定的分支，在提交说明里写清楚改了哪些文件。
