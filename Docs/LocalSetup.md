# 本地环境搭建

## 1. 准备工具（只需一次）

| 工具 | 说明 |
| --- | --- |
| Git | 2.40 以上 |
| Git LFS | 模型、贴图用 LFS 存储。安装后执行一次 `git lfs install` |
| Unity Hub + Unity **6000.3.8f1** | 版本必须一致，否则会触发项目升级 |

检查 LFS 是否可用：

```bash
git lfs version
```

## 2. 克隆仓库

```bash
git clone https://github.com/dashi0203/WORT1.0.git
```

```bash
cd WORT1.0
```

如果克隆前没有装 LFS，`.fbx`、`.jpeg` 会是几百字节的指针文件。装好 LFS 后补拉：

```bash
git lfs pull
```

## 3. 切换到开发分支

云端会话推送的分支以每次会话指定的为准，当前是 `claude/bold-bardeen-vlwhjd`。

```bash
git fetch origin claude/bold-bardeen-vlwhjd
```

```bash
git switch claude/bold-bardeen-vlwhjd
```

之后云端每次推送新内容，在本地拉取：

```bash
git pull origin claude/bold-bardeen-vlwhjd
```

## 4. 用 Unity 打开

1. 打开 Unity Hub → Projects → Add → Add project from disk，选择 `WORT1.0` 文件夹。
2. 确认编辑器版本显示 6000.3.8f1，然后打开。
3. 第一次打开会导入资源、解析 Packages，需要几分钟。
4. 打开后检查 Console：应当没有红色报错。

## 5. 地形实验（8×8 chunk）

1. 菜单 **Wort → Create Terrain Lab Scene**：生成 `Assets/Wort/Scenes/TerrainLab.unity`（相机、Chibi 角色、灯光、Volume、Terrain Lab），并立即生成一次地形。
2. 选中 **Terrain Lab** 物体，Inspector 里有三个按钮：**生成**、**换种子并生成**、**清除**。
3. 点 Play：地形自动重新生成，角色放在出生点。WASD 移动，空格跳（能跳上 1m 台阶，2m 不行）。
4. 生成的地形不保存进场景文件（每次 Play 或点“生成”时重建），所以场景文件很小。

**核对云端预览**：每次生成后 Console 会打印一行 `[TerrainLab] seed … checksum 0x…`。
默认种子 20261109 的云端预览 checksum 是 `0xEE3CECBB`，种子 7 是 `0x3DD96B8A`。数字相同，说明 Unity 里的地形和云端预览图逐格一致。

**自动测试**：Window → General → Test Runner → EditMode → Run All。`GenerationTests.MatchesCloudPreview` 会自动比对上面两个 checksum。
`LayoutTests` 测 layout 管线（Docs/WorldGen.md）：8×8 测试 layout 的 checksum 应为 `0x8C73CCB3`，检查全部通过，盆地能进能出，没有斜坡体素，挖坑的反例要报错。
layout 管线目前只在云端预览和测试里用，TerrainLab 场景还是旧生成器。

**体素器 5×5 与崖面外观**：Terrain Lab 的 Inspector 里 `Generator` 选 `Terrace5x5`、`Seed` 填 3，生成后 checksum 应为 `0xDA849612`（不勾 `Random Chunk` 时）。
`Cliff Dressing` 勾上时显示崖面外观原型（方案 B：立方体 + 倒角、侧面外鼓、泥土顶面边缘一圈岩石），取消勾选是原来的方块；
勾上时地面用 `Wort/TerrainBlend` 着色器（泥土 / 岩石两张 128×128 贴图融合）；碰撞是 1m 格按同高矩形合并的 BoxCollider 加斜坡小网格（`Collider` 子物体）。
勾上 `Random Chunk`（默认勾上）时，右上角 chunk（3,3）是随机 chunk；Inspector 里点“重新生成随机 chunk”换一次（序号 +1），只重建它和周围一圈，
Console 打印 `[TerrainLab] 刷新 k  ID 3:xxxxxxxx：通过 / 失败 …`、之前失败的重试及原因，以及各步耗时。种子 3、序号 0 的刷新 ID 应为 `3:0FD0E8BD`，
随机区域 checksum 应为 `0x71E408BD`（与云端 `--random --seed 3` 的刷新 1 相同）。反馈时请附开关前后的截图。
**刷新 ID**：Inspector 里显示当前的刷新 ID，可点“复制”；把 ID 填进“重放 ID”再点“按 ID 重放”，就能复现那一次刷新（ID 里的世界种子和当前不同时会先换种子、整张重新生成）。
云端用 `--rid 3:0FD0E8BD` 复现同一次。反馈随机 chunk 的问题时请附上 ID。
**坡道与路面**（2026-11-12）：坡道画成和崖面同一套外观（坡面边缘倒角、岩石边、侧面鼓包墙），坡面和坡顶 / 坡底各 1 格显示路面；
勾 `Random Chunk` 时还会从出生点到随机 chunk 的两个候选出入口铺测试道路，随机 chunk 里沿房间连接图铺小径（连通测试）。
Console 会打印“固定世界道路：……铺通 / 铺不通”。路面用第三张贴图（`Wort/TerrainBlend` 的 `_RoadTex`，顶点色 g 通道），请看路边是否自然、有没有接缝。
**道路笔刷**（2026-11-12）：Terrain Lab 的 Inspector 底部“道路笔刷”。点“笔刷：开”后，在 Scene 视图里左键按住拖动刷路（Alt + 拖动照常转视角），
上面一排选“擦除 / 小路 1 格 / 次干道 2 格 / 主干道 3 格”（类型决定宽度；选擦除时下面一排选擦除宽度 1–3 格）；圆圈是笔刷位置。大多数路用小路。松开鼠标后只重建受影响的 chunk。Ctrl+Z 可撤销（撤销后整张重新生成）。
刷的结果存在 `Assets/Wort/Data/RoadOverlay_seed{种子}_{生成器}.asset`（第一次打开笔刷时自动创建），重新生成、换随机 chunk 时会自动叠加；
种子或生成器不一致时不叠加（Console 有提示）。叠加了覆盖层时 checksum 会和上面的数字不同，这是正常的。
覆盖层是设计数据：本地刷好后请提交这个 .asset（或发给云端会话），否则云端看不到。
**房间提示**（2026-11-12）：勾 `Show Room Hints`（默认开）时，随机区域每个房间铺一层薄薄的半透明色块（每间一种颜色、边缘描边）：
淡色 = 房间的平整区域；颜色更深（高 1cm 处再叠一层）= 往里收 1 格、以后凸起台地可以生成的区域。只是提示，不影响碰撞和 checksum。

## 6. 本地改动约定

- 本地项目暂时视为只读：不要提交本地改动，代码统一由云端会话推送。
- Unity 第一次打开时可能会改写 `Packages/packages-lock.json`，或新增少量 `.meta` 文件。这些属于正常现象，可以先不提交。要还原，执行：

```bash
git restore .
```

- 反馈问题时，请附上 Console 报错的完整文字（右键 → Copy）和画面截图。

## 7. 让 Codex 拉新（固定模板）

每次云端推送后，Claude 会在回复末尾附上下面这段，直接复制给 Codex 即可。分支变了时 Claude 会同步改这里。
本地仓库是单分支克隆（`git fetch origin` 只拉原来那条分支），所以第 2 步显式拉指定分支，第 4 步用 `merge --ff-only` 代替 `pull`；第一次建本地分支不加 `--track`（拉取规则不认这条远端分支，加了会报 “not a branch”）。

```text
只做下面这几步，不要操作 Unity 界面，不要用 computer use，不要新建、修改、提交或删除任何文件：

1. 在项目目录运行 git status，把输出原样发给我。
   - 如果被改动的文件只在下面这 5 个之内，直接继续：
     Assets/Wort/Rendering/PC_RPAsset.asset
     Assets/Wort/Rendering/SampleSceneProfile.asset
     Packages/packages-lock.json
     ProjectSettings/EditorBuildSettings.asset
     ProjectSettings/EditorSettings.asset
   - 如果还有别的被改动的文件（尤其是 .cs 或 .shader），先停下来告诉我，不要自己还原。
   - 未跟踪的文件一律不要动。
2. git fetch origin +refs/heads/claude/elegant-wright-xknxr5:refs/remotes/origin/claude/elegant-wright-xknxr5
3. git switch claude/elegant-wright-xknxr5
   - 如果报 “invalid reference” 或分支不存在，改用（不要加 --track，单分支克隆下会报 “not a branch”）：
     git switch -c claude/elegant-wright-xknxr5 origin/claude/elegant-wright-xknxr5
   - 如果报 “untracked working tree files would be overwritten”，停下来把列出的文件发给我，不要自己删。
4. git merge --ff-only origin/claude/elegant-wright-xknxr5
5. git lfs pull
6. 依次运行这三条，把输出都发给我：
   git log -1 --oneline
   git rev-parse --short HEAD
   git rev-parse --short origin/claude/elegant-wright-xknxr5
   后两条输出一样，就说明已经是最新；不一样就停下来告诉我。

做完就结束，不要打开或操作 Unity，也不要做其他检查。
```
