# 崖壁 / 地面贴图实验目录

源图和云端流程（都不需要 Blender，依赖 numpy、pillow、scipy）：

| 文件 | 说明 |
| --- | --- |
| `IMG_9471` / `IMG_9473` / `IMG_9485_BaseColor.png` | 沼泽 / 旧森林 / 人类区崖壁原色图（512，1m） |
| `IMG_*_Normal_Periodic512.png` | 对应的无缝法线 |
| `Redo/` | 新森林崖壁（GPT）、人类区顶岩、三套泥土法线 |
| `Variants_GPT/<套>/<尺寸>_v<n>.png` | 崖壁变体（GPT 补图原图），处理后放在 `Assets/Wort/Art/ProcCliff/Variants/` |

| 脚本 | 用途 |
| --- | --- |
| `Tools/TilesetBake/periodic_facet_normal.py` | 原色图 → 高度（67% 向下延伸流程）→ 周期域三角化 → 无缝面法线；俯视贴图把延伸比例设 0 |
| `Tools/TilesetBake/make_tileable.py` | 四方连续（可选去低频明暗渐变） |
| `Tools/TilesetBake/variants.py` | 变体：取中间、色调对齐、外圈渐变回基础块、生成法线、平铺预览 |
| `Tools/TilesetPreview/proc_cliff.py` | 程序化崖壁的云端预览（`BIOME=1` 看 Biome 融合） |

早先 dot 的 Blender 流程（Collapse 减面 + Cycles 烘焙，`WORKFLOW.md`、`bake_seamless.py`、旧版法线）已被上面的云端流程取代，已删除，需要时从 git 历史找回。
