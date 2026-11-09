# Wort

写实低魔的 3D 开放世界生存 / 炼金 RPG。策划文档：Wort GDD 1.0.01（claude.ai Docs）。

- 引擎：Unity 6000.3.8f1，URP 17.3
- 本地如何克隆、切换分支、打开项目：见 [Docs/LocalSetup.md](Docs/LocalSetup.md)

## 目录

```
Assets/Wort/
├─ Art/Characters/Chibi/   人物模型（来自 topdowntemp）
├─ Input/                  InputSystem_Actions
├─ Rendering/              URP 资产、Renderer、Volume Profile
│  └─ Shaders/             VerticalTiltShift（Renderer Feature 默认关闭）
└─ Scripts/
   └─ Runtime/             Wort.Runtime 程序集：Camera / Presentation / Player / Rendering
Docs/                      项目文档
Tools/                     Unity 之外的工具（如云端地形预览）
```

相机、人物压扁、移动脚本来自 dashi0203/topdowntemp，GUID 保持不变。
