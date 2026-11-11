# Wort 世界地形规划器（离线版 v2）

这是一个**抽象世界布局数据编辑器**，不是体素地形生成器。完整代码只有 `index.html` 一个文件，双击后通过现代浏览器离线运行，无需 Unity、服务器或安装任何依赖。

**PNG 只是总览图；真正交给 Claude / 体素生成器的是分层 JSON。**

## 现在的抽象层结构

这个规划器只负责记录几类上游数据：

1. **Biome 生态层**：二维硬边界色块，独立于 Chunk 网格。
2. **台地 / 盆地图层**：二维硬边界分类层，只记录 `普通 / 台地 / 盆地` 三态，**不记录真实海拔**。
3. **Chunk 标记层**：随机 / 固定 / 过渡 / 不可通行。
4. **水系分割层**：河流、峡谷中心线与规划宽度。
5. **路线层**：初步路线与最终道路，分开保存与显示。
6. **关键地点层**：圆形 / 方形图钉及其半径。

也就是说，这个工具只表达：

- 这里是什么生态；
- 这里是不是台地或盆地；
- 哪些 Chunk 是固定 / 随机；
- 关键地点、水系和路线大致在哪里。

**它不负责生成真实高程、坡度、通行性验证、生态噪声融合或最终地形。** 这些都留给后续体素生成器。

## 主要操作

| 快捷键 | 功能 | 操作 |
| --- | --- | --- |
| `B` | Biome 区域 | 选人类区 / 湿地 / 森林 / 针叶林 / 山地 / Moorland / 海岸，按住拖动涂色；右键拖动擦除；设置笔刷直径（米） |
| `T` | 台地 / 盆地图层 | 选台地或盆地，按住拖动涂抹；右键拖动擦除；设置笔刷直径（米） |
| `M` | Chunk 标记 | 选随机 / 固定 / 过渡 / 不可通行，**单击 Chunk 切换，再点同一格取消**；拖动可连续标记 |
| `R` / `C` | 河流 / 峡谷 | 按住拖动绘制带宽度的中心线 |
| `L` | 初步路线 / 最终道路 | 选择阶段，依次点击添加折点；`Enter` 或双击完成；可设置单向通行与道路类型 |
| `P` | 关键地点 | 点击放置带名称、类别、圆形 / 方形和可调半径的图钉 |
| `V` | 选择 | 点击选中对象并编辑；`Delete` 删除 |
| `H` | 平移 | 拖动移动视角；滚轮缩放（任何模式下空格 / 中键拖动可临时平移） |

`Ctrl+Z` 撤销、`Ctrl+Shift+Z` / `Ctrl+Y` 重做、`Esc` 取消当前未完成的折线。

## 图层规则

### 1. Biome 图层

- 与 Chunk 网格**完全分离**。
- 采用二维硬边界自由笔刷。
- 无描边、无羽化、无渐变、无噪声融合。
- 每个位置只属于一种 Biome 或“未涂色”。
- 重叠区域遵循**后画覆盖先画**。
- 右键只擦除 **Biome 图层**，不会影响台地 / 盆地图层。

### 2. 台地 / 盆地图层

- 与 Biome 图层**完全独立**。
- 同样是二维硬边界自由笔刷。
- 只记录三态：
  - `0 = 普通`
  - `1 = 台地`
  - `2 = 盆地`
- 它不是海拔图，也不是最终高差图。
- 它只表达一种抽象地形分割关系：这里是否属于局部抬升区或局部下陷区。
- 右键只擦除 **台地 / 盆地图层**，不会影响 Biome 图层。

### 3. Chunk 标记

Chunk 标记仍然是离散网格层，用来辅助划分：

- 固定 Chunk
- 随机 Chunk
- 过渡 Chunk
- 不可通行 Chunk

这层与前两种笔刷层也互不冲突。

## 保存与导出

- 自动保存到浏览器 `localStorage`，可能因清理浏览器数据而丢失；请定期手动导出 JSON。
- **导出 JSON**：包含全部可编辑的分层矢量数据，以及可直接读取的 Biome / 台盆分类栅格遮罩。它是核心交付物。
- **导入 JSON**：替换当前规划（支持旧版本布局）。
- **导出 PNG**：只作视觉总览；不能反推出原始规划数据。

## JSON 结构说明

规划器导出的 `layers` 目前包含：

- `biomes`
- `terrain`
- `chunkZones`
- `places`
- `hydrography`
- `routes`

其中最关键的是两个二维分类层：

### `layers.biomes`

- `marks`：原始可编辑笔画
- `raster`：按 `4m × 4m` 采样得到的二维离散分类遮罩
- `model = hard-edge-categorical-mask`

### `layers.terrain`

- `marks`：原始可编辑笔画
- `raster`：按 `4m × 4m` 采样得到的二维离散分类遮罩
- `model = hard-edge-terrain-split-mask`
- `codebook = ["none", "plateau", "basin"]`

注意：`raster` 只是结果快照；**真正的可编辑源数据是 `marks`**。

## JSON v2 示例（省略部分数组）

```json
{
  "schema": "wort-world-layout",
  "version": 2,
  "chunkMeters": 64,
  "world": { "columns": 16, "rows": 16 },
  "planning": { "speedMps": 4, "dayMinutes": 24 },
  "layers": {
    "biomes": {
      "model": "hard-edge-categorical-mask",
      "marks": [
        { "id": "b1", "shape": "stroke", "biome": "wetland", "width": 64,
          "points": [[100, 120], [160, 130]] }
      ],
      "raster": {
        "cellMeters": 4,
        "columns": 256,
        "rows": 256,
        "encoding": "rle-row-major",
        "codebook": ["unpainted", "human", "wetland", "forest", "conifer", "mountain", "moorland", "coast"],
        "runs": [[0, 6000], [2, 22]]
      }
    },
    "terrain": {
      "model": "hard-edge-terrain-split-mask",
      "marks": [
        { "id": "t1", "shape": "stroke", "terrain": "plateau", "width": 96,
          "points": [[220, 140], [300, 170]] }
      ],
      "raster": {
        "cellMeters": 4,
        "columns": 256,
        "rows": 256,
        "encoding": "rle-row-major",
        "codebook": ["none", "plateau", "basin"],
        "runs": [[0, 5400], [1, 180], [0, 220]]
      }
    },
    "chunkZones": [{ "x": 4, "y": 5, "zone": "random" }],
    "places": [{ "id": "home1", "name": "住所", "category": "home", "shape": "circle", "radius": 40, "x": 450, "y": 600 }],
    "hydrography": {
      "rivers": [{ "id": "r1", "kind": "river", "width": 12, "points": [[100, 40], [120, 100]] }],
      "canyons": []
    },
    "routes": {
      "draft": [{ "id": "d1", "name": "初步路线", "stage": "draft", "type": "trail", "oneWay": false,
        "points": [[200, 200], [250, 240]] }],
      "final": []
    }
  }
}
```

## 给 Claude / 生成器的解释

这份规划数据的定位是：**世界生成器的上游输入**。

推荐的理解方式：

- `biomes`：宏观生态分布
- `terrain`：宏观相对地形分割（普通 / 台地 / 盆地）
- `chunkZones`：制作方式和区域性质标记
- `hydrography`：河流与峡谷的空间约束
- `routes.draft`：第一轮抽象路线意图
- `routes.final`：在加入地形分割后重做的路线
- `places`：关键地点与影响范围

之后再由体素生成器基于这些层：

- 叠加整体地形起伏与噪声；
- 处理生态过渡与自然边缘；
- 把台地 / 盆地抽象信息变成真实高差结构；
- 再结合固定 / 随机 Chunk 继续后续制作。

**本工具不偷偷提前编码生成规则，只保存抽象布局数据。**