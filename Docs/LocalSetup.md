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

云端会话推送的分支是 `claude/focused-galileo-rr85m8`。

```bash
git fetch origin claude/focused-galileo-rr85m8
```

```bash
git switch claude/focused-galileo-rr85m8
```

之后云端每次推送新内容，在本地拉取：

```bash
git pull origin claude/focused-galileo-rr85m8
```

## 4. 用 Unity 打开

1. 打开 Unity Hub → Projects → Add → Add project from disk，选择 `WORT1.0` 文件夹。
2. 确认编辑器版本显示 6000.3.8f1，然后打开。
3. 第一次打开会导入资源、解析 Packages，需要几分钟。
4. 打开后检查 Console：应当没有红色报错。

骨架阶段仓库里还没有场景。第 3 步完成后，用菜单 **Wort → Create Terrain Lab Scene** 生成测试场景。

## 5. 本地改动约定

- 本地项目暂时视为只读：不要提交本地改动，代码统一由云端会话推送。
- Unity 第一次打开时可能会改写 `Packages/packages-lock.json`，或新增少量 `.meta` 文件。这些属于正常现象，可以先不提交。要还原，执行：

```bash
git restore .
```

- 反馈问题时，请附上 Console 报错的完整文字（右键 → Copy）和画面截图。
