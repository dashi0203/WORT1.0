# Wort 开发记录

最后更新：2026-11-10 · 分支 `claude/intelligent-noether-7f0v7r` · 最新提交见 `git log`

GDD：https://claude.ai/artifact/S6CtD759fRjLrJdhjF5inT（版本 1.0.01）

## GDD 同步记录

`Docs/GDD.md` 是 claude.ai 上 GDD 的只读快照。每次 GDD 改动后重新导出，并在下表最上面加一行。

| 日期 | 源文档 rev | 内容 |
| --- | --- | --- |
| 2026-11-10 | 56 | 并入世界生成讨论（`Docs/WorldGen.md` 第 12 节）：第 4 节高度表（设计目标 32m、硬上限 56m、地下最多 8m、柱子 −8 到 +56）与“0 层为海平面”；第 5 节坠落伤害；第 6 节半手工流程第 1–2 步、通道类型表悬崖一行；6.3 节 Biome 区域图来源、阶段 A 第 1/5/8 步、地形元素表（新增矮台地，主力台地 +3–4、高台每级 +3–4、孤峰 6–10、大悬崖带 7 层以上）、宏观高度来源；新增 6.4 节“世界布局”（布局顺序、原则、区域类型表）；第 14 节新增 2 条待讨论。核对：【已定】85 / 【暂定】34（另有 1 处“【暂定，…】”）/ 【待讨论】11，表格 20，公式 4。 |
| 2026-11-10 | 34 | 长线副本改为“大地图上只放入口预制件、没有地形”：第 4 节世界结构表“高度上限”、地图方位、特殊关卡段；第 12 节改名“地牢与长线副本”，雪山段改写为长线副本（雪山、巨木沼泽、深渊），新增 2 条【待讨论】；第 14 节待讨论清单对应一项。核对：【已定】78 / 【暂定】33 / 【待讨论】11，表格 19，公式 4。 |
| 2026-11-10 | 28 | 首次导出。已包含 6.3 节“微起伏默认是台阶，少量斜坡、整体比例要小【已定】”的修改。核对：【已定】78 / 【暂定】33 / 【待讨论】10，表格 19，公式 4。 |

## 当前状态

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 第 1 步 | 盘点 topdowntemp，定目录结构 | 完成 |
| 第 2 步 | 项目骨架、.gitignore / .gitattributes（LFS）、`Docs/LocalSetup.md` | 完成 |
| 第 3 步 | GDD 16 节 8×8 chunk 地形实验：纯 C# 生成核心 + 云端预览 + Unity 接入 | 完成，本地测试通过 |
| 第 4 步 | 世界生成规格 `Docs/WorldGen.md`（layout 优先、拓扑由 layout 决定） | 完成，已并入 GDD rev 56 |
| 第 5 步 | layout 管线技术验证：`LayoutGenerator` + 检查 + 8×8 测试 layout + 云端预览 | 云端通过，等负责人看图；Unity 侧只加了测试，TerrainLab 仍用旧生成器 |

本地验证结果（2026-11-09）：Unity 里 checksum 与云端预览一致，EditMode 测试通过，“基本顺利且符合预期”。

## 目录

```
Assets/Wort/
├─ Art/Characters/Chibi/      人物（来自 topdowntemp，GUID 未变）
├─ Input/                     InputSystem_Actions
├─ Rendering/                 URP 资产；Shaders/VerticalTiltShift（Renderer Feature 默认关闭）
├─ Scenes/                    TerrainLab.unity（菜单生成，暂未入仓）
├─ Scripts/Generation/        Wort.Generation：纯 C# 地形生成
├─ Scripts/Runtime/           Wort.Runtime：Camera / Presentation / Player / Rendering / Terrain
├─ Scripts/Editor/            Wort.Editor：Wort/Create Terrain Lab Scene 菜单、TerrainLab Inspector
└─ Tests/EditMode/            确定性测试
Tools/TerrainPreview/         云端预览（.NET 8，直接编译 Generation 源码）
```

## 云端预览用法

```bash
apt-get update && apt-get install -y dotnet-sdk-8.0
cd Tools/TerrainPreview
dotnet build -c Release
dotnet bin/Release/net8.0/TerrainPreview.dll --seed 20261109 --out out
```

输出（`out/` 已被 .gitignore 忽略）：
- `biome.png`、`height.png`：俯视。高度图里黑线 = 高差 ≥ 2 的崖；橙 = 坡道；浅色 = 斜坡；
  品红斜纹 = 悬崖背面遮挡带；青色 = 冲沟；灰白 = 孤峰 / 碎石；红点 = 河流落差点；红白圆 = 出生点；网格 = 64m chunk。
- `shear_overview.png`：整区错切视角，2 px/m。
- `shear_mountain_zoom.png`：山地局部，5 px/m。
- `shear_1..4_*.png`：1080p 相机视野（orthoSize 9.5，shear 1），红框 = 1×1.5 格人物参照。
- 控制台输出各项统计和 `checksum`。

预览专用的视觉辅助（Unity 里没有）：深度突变处描暗边、顶面按高度略提亮。

**layout 模式**（Docs/WorldGen.md 管线）：

```bash
dotnet bin/Release/net8.0/TerrainPreview.dll --layout ../../Assets/Wort/Data/Layout/Test8x8.json --out out_layout
```

- 同目录的 `SiteTemplates.json` 自动读入。检查全部通过时退出码 0，有错误时 2。
- `layout.png`：区域类型（绿 = 开放区，土黄 = 台地，蓝 = 盆地，紫 = 峡谷，灰 = 高原；黑线 = 区域边界），
  浅色 = 道路，橙 = 出入口，棕 = 桥面，深灰 = 隧道，白点 = 地点，红点 = 住所，品红 = 回不来，青 = 多余通路。
- `height.png`：同旧模式；另加道路（浅黄）、桥（棕）、隧道（深灰斜纹）、回不来（品红）、多余通路（青）。
- `shear_overview.png`、`view_*.png`（1080p 相机视野）、`mid_*.png`（80m × 60m，8 px/m）：每个桥、隧道、出入口、洞穴和小屋一张。

## 当前 checksum（改算法后要同步更新测试和 LocalSetup）

| 种子 | checksum |
| --- | --- |
| 20261109（默认） | `0xEE3CECBB` |
| 7 | `0x3DD96B8A` |
| layout `Test8x8.json`（seed 20261110） | `0xF6290339` |

## 地形生成管线（`RegionGenerator.Run`）

1. Biome 布局：程序自由发挥（北山地 → 针叶林 → 混交林；中南人类区；西南沼泽）。
   山地边界 = 山地分数 > 0：两层域扭曲 + 中频 + 南北向指状噪声做山嘴 / 山湾；按 4m 块量化 + 随机抖动。
2. Biome 权重：3 次盒式模糊（半径 16），过渡带约 40m。
3. 宏观高度：Biome 参数混合 + 域扭曲低频 / 中频噪声（山里压低 70%）。
   山前：陡段整崖 4–8 层；其余段先 2–3 级 1 层台阶（每级 5–8m 宽）再接剩余崖高。
   冲沟：5–7 条，从山体内部沿下坡追踪，出山后再延伸约 14m，宽 4–8m；冲沟里山体抬升只留 35%、清除台地。
4. 微起伏：按 Biome 混合幅度（0–1 层 ≈ 0.6，0–2 层 ≈ 1.2）。
5. 台地：每级独立块状噪声，覆盖率随位置缓慢变化；Close(2) → 4m 块量化 → Open(4) → 嵌套内缩 3m → 去小块。
   残丘：山外 7–80m，2 层，越远越稀。
6. 量化与清理（去单格凸起 / 小坑）。
7. 高台（4–5 层）；孤峰（4–7 个，山脊 / 最高处且靠崖边，直径 4–8m，高 6–10 层，外缘低 1–2 层，脚下 1 层碎石台阶）。
8. 水系：池塘 / 浅洼（水面比岸低 1 层）、人类区一个湖、一条从山里冲沟流出的河（沿低频噪声零线弯曲，水面单调不升，标记落差点）。
9. 悬崖背面遮挡带：抹平 1 层起伏，不放斜坡和坡道。
10. 连通性：小封闭区填平，大的在笔直崖边放 2–3 格宽坡道（不朝北）；再按 Biome“每块台地的坡道”数补足。
11. 斜坡（GDD 6.3【已定】：1 层高差默认是台阶）：目标 2.5% 地表格（`SlopeTargetRatio`），
    成段 2–6 格同向，优先水边 / 通行路线 / 谷底 / 长而缓的起伏带；**自然地形不允许连续两格斜坡**（坡道口除外）。

阶段 B：`RegionVoxels` 体素化（整格 + 4 向斜坡，地下到 −16 层），`ChunkMesher` 出网格（按材质分子网格，Unity 绕序）。

## 已做的决定（含对【暂定】数值的理解）

- 对 GDD 6.3 起伏参数表的数值化见 `Biome.cs`；叠层“偶尔”= 1.35 级。
- 山前“降成 2–3 级台阶”理解为：先 2–3 级 1 层台阶，再接剩余崖高（负责人未否定，可再确认）。
- 所有池塘水面比岸低 1 层；8×8 区域没有海岸，河流流到南边界结束。
- 斜坡比例起始值 2.5%，负责人看图后可直接改。
- Unity：生成物 `HideFlags.DontSave` 不进场景；运行时建纯色 URP Lit 材质；水面无碰撞；
  角色跳跃高度 1.25（保证能跳上 1m）；CharacterController 坡度上限 50°；相机高 80m。
- Packages 已精简（去掉 ai.assistant / ai.inference / pipeline / collab-proxy / multiplayer.center / visualscripting）。
- 云端环境：个人环境 “WORT1.0” 放行了 GitHub LFS 主机（lfs.github.com 等），否则推不了 .fbx/.jpeg。

## layout 管线（`LayoutGenerator`，Docs/WorldGen.md）

文件：`Json.cs`（极简 JSON 读取）、`WorldLayout.cs`（数据模型 + 有机形状）、`LayoutGenerator.cs`、`LayoutChecks.cs`（可站立表面图 + 检查）；
数据：`Assets/Wort/Data/Layout/Test8x8.json`、`SiteTemplates.json`。

第一版（自己生成开放区高度）被否：开放区只有 0 层 + 微起伏，没了大走势；测试 layout 是机制清单拼出来的（直条台地、蛋形台地、东西向峡谷）。
现在的做法：**自然地形沿用 `RegionGenerator`**，给它加了可选的 `TerrainHooks`（Biome 图、保留区、河的终点、印章回调、出生点），
不传钩子时与旧版逐格一致（checksum 未变）。

1. 区域形状：椭圆 / blob（控制点 + Chaikin 3 次平滑）/ stroke（粗折线），**坐标扭曲**（大尺度约为尺寸的 18%，小尺度 1–4m）+ 线条宽度 0.55–1.6 倍变化 + 崖线粗糙；
   1m 栅格化；小于 48 格的碎块和结构区域的孤岛并入周围区域。
2. Biome 图取所在开放区；峡谷等结构区域不写 biome 时沿用下面的开放区。
3. 保留区：结构区域外扩 4m、地点 + buffer、桥和隧道走廊 → 旧生成器在这里不放台地、残丘、高台、孤峰、池塘，河绕开。
4. 印章（河之后、连通性修复之前）：海（0 层水面，海底 −3 到 −6）、峡谷（周围平滑地面 + level，谷底相邻格差 ≤ 1，边界 ≥ 2 层崖）、
   盆地（外圈最低 + level）、台地区（外圈最高 + level）、地点压平、道路、手写出入口、桥和隧道、和海相连的低于 0 层的地面灌海水。
5. 坡道与连通性、斜坡、出生点都走旧流程；出生点改成 layout 的住所；河只在南半部的海结束。
6. 检查：每格最多两个节点（地面 + 悬空层顶面）；浅水里按水面高度走；往上 1 层、往下按坠落规则，连通只用无伤边（≤ 3 层）。
   有向连通、地点地形、步行距离、高度范围；手写了出入口时另查多余通路。

2026-11-10 第三轮（负责人反馈）：沼泽移到内陆；大峡谷底下有大河、北端切进山里、往南逐渐变浅；峡谷下段右侧融一个小盆地；
删除所有斜坡（`TerrainHooks.NoSlopes`：不跑 AssignSlopes，坡道改台阶）；海崖；峡谷河取代程序河（`SkipRiver`）。
顺带修掉的问题：旧连通性把深水当成可走（layout 模式下深水不连通）；地点保留区在台地群里留下坑（地点不再保留，改成按占地最常见高度压平 + 外圈收台阶）；
峡谷谷底低于 0 层时海水倒灌（谷底至少 1 层，入海口除外）；残留小陷坑自动填平（FillTrapPits）。

第四轮：台地太规整 → layout 模式下去掉 4m 块量化、改用圆形开闭运算（`Grid.OpenRound / CloseRound`），坡道放置只要求坡道宽度的直边。

第五轮：Biome 大区 + 不规则小斑块（`ApplyBiomePatches`），过渡带 32m（`TerrainHooks.BiomeBlurRadius = 13`），山前悬崖带只按山地大区（`MountainMask`），
湿地宏观高度改为比混交林低 1 层（`MacroBase`），大湿地缩小并改成不规则形；预览新增 `biome_raw.png`（每格主导 Biome）。

测试 layout 结果：检查全部通过；大峡谷两侧 60m 的两点步行 406m；全图 0 个斜坡体素；在开阔地挖 3 层深的坑，检查报 100 个“回不来”。

## 未决问题 / 可调项

- layout 管线：桥和隧道只支持南北或东西走向；随机区域、固定 chunk 划分还没接进来；
  旧生成器的程序河（不用 layout 时）仍是 4 方向寻路、转弯偏直角；layout 里的河跟着峡谷的平滑曲线走。

- 4m 块量化让斜向边界呈 4 格一阶的锯齿；嫌方可改 3m。
- 森林里有几条 1 层等高线与山前大致平行（宏观高度过渡造成），是否打散待定。
- 残丘偏方；山里仍有少量走不到的格子（约 150 格）。
- GDD 第 16 节“不包含”的内容（随机区域、环境场、植物、半墙、正式美术、完整 chunk 编辑工具）都还没做。
- GDD 第 14 节的待讨论问题（高度裁切过渡、遮挡虚化、雪山呈现、VisualCameraTilt / TiltShift 是否沿用等）。

## 下一步候选（等负责人决定）

- layout 管线：负责人看图确认后接 Unity（TerrainLab 加一个 layout 模式），补 2 层矮台地、自然 3–4 层台地、随机区域组。

- GDD 6.2：环境层（湿度、地表分区、土壤、扰动）与植物层。
- 第 7 节：高度裁切、遮挡虚化原型。
- chunk 数据格式与编辑工具（6.3 工具链需求）。
