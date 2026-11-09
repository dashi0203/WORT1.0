# Wort — Claude 工作须知

Unity 6000.3.8f1 + URP 17.3 的写实低魔 3D 开放世界生存 / 炼金 RPG。

**开始任何任务前，先读：**
1. GDD（策划文档，Claude Docs）：https://claude.ai/artifact/S6CtD759fRjLrJdhjF5inT
2. `Docs/DevLog.md`：开发记录、已做的决定、未决问题、下一步。

## 规则（来自项目负责人）
- 用中文交流。
- GDD 里标【已定】的不改；【暂定】的可以提建议，但要先问。
- 小的技术决定直接按自己的建议做，在回复里说明一句即可。只有三种情况要停下来问：
  1. 与 GDD【已定】冲突；
  2. 要改动【暂定】的设计；
  3. 要往仓库里加清单以外的新类别内容。
- 推送到会话指定的开发分支（当前一直在用 `claude/focused-galileo-rr85m8`）。
- 参考仓库：`dashi0203/topdowntemp` 已搬完；`dashi0203/insect` 只借鉴思路、按文件查看；
  `dashi0203/FactorioLike` 约 550MB，**绝对不要整仓克隆**，只用稀疏检出拉单个文件。
- 本地 Unity 项目视为只读，代码一律由云端会话推送；本地反馈会附 Console 报错和截图。

## 云端开发要点
- 云端没有 Unity。生成核心 `Assets/Wort/Scripts/Generation/` 是纯 C#（asmdef 设了 `noEngineReferences`），
  用 `Tools/TerrainPreview` 渲染预览图给负责人看，确认后再改 Unity 侧。
- 预览工具需要 .NET 8 SDK（`apt-get install -y dotnet-sdk-8.0`）。用法见 `Docs/DevLog.md`。
- **改了生成算法，checksum 会变**：要同时更新 `Assets/Wort/Tests/EditMode/GenerationTests.cs` 里的期望值和 `Docs/LocalSetup.md` 里的数字。
- 生成核心里不要用 `System.Random`、`Math.Pow/Sin/Exp` 等，保证 .NET 与 Unity（Mono）结果逐格一致。
- 模型、贴图走 Git LFS（规则见 `.gitattributes`）。
