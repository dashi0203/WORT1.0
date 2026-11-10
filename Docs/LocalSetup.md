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

云端会话推送的分支以每次会话指定的为准，当前是 `claude/intelligent-noether-7f0v7r`。

```bash
git fetch origin claude/intelligent-noether-7f0v7r
```

```bash
git switch claude/intelligent-noether-7f0v7r
```

之后云端每次推送新内容，在本地拉取：

```bash
git pull origin claude/intelligent-noether-7f0v7r
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
`LayoutTests` 测 layout 管线（Docs/WorldGen.md）：8×8 测试 layout 的 checksum 应为 `0xFC883C97`，检查全部通过，盆地能进能出，抹掉盆地坡道的反例要报错。
layout 管线目前只在云端预览和测试里用，TerrainLab 场景还是旧生成器。

## 6. 本地改动约定

- 本地项目暂时视为只读：不要提交本地改动，代码统一由云端会话推送。
- Unity 第一次打开时可能会改写 `Packages/packages-lock.json`，或新增少量 `.meta` 文件。这些属于正常现象，可以先不提交。要还原，执行：

```bash
git restore .
```

- 反馈问题时，请附上 Console 报错的完整文字（右键 → Copy）和画面截图。
