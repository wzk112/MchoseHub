# Swift 移植功能与验收记录

日期：2026-11-02。参考分支起点：`803e694`。所有客户端业务逻辑和 macOS 传输均为 Swift；C# 用于生成离线对照资料。

| Windows 已有功能 | Swift 入口 | 当前证据 |
| --- | --- | --- |
| 设备枚举、物理分组、断连处理 | `MchoseHID/MacHID.swift`、`AppModel` | 原生枚举识别真实 2.4G 接收器；代码含取消与旧结果丢弃，中途拔插仍需实测 |
| 18 型号布局、读写 schema | `MchoseCore/Schema.swift`、生成资源 | 18 型号配置校验和六条恢复 payload 与 C# 逐字节一致 |
| USB feature 传输 | `MacFeatureTransport` | 构建通过；本轮没有 USB 有线设备实测 |
| 2.4G、签名识别、分包/ACK | `WirelessChannel`、原生 input 回调 | 真实 K87 签名、全量双读备份与三项可恢复写入通过；60 组发送分包对照通过 |
| 常规灯效、RGB、亮度、速度、多彩 | `FeaturePanels.lighting` | UI 草稿编辑通过，真实亮度字段修改/恢复通过；所有效果观感未逐项观察 |
| 逐键 RGB、填充、LED 图 | `KeyboardProfile.setColor`、`KeyboardDiagram` | 预览中为 A 设置粉色、选中 S 后 A 保持该颜色；设备逐键颜色写入未实测 |
| 修复灯效、强制点亮、恢复默认 | 配置草稿操作 | 代码与 schema 校验；不自动写设备；本轮未向设备执行 |
| 回报率、休眠、延迟、Win 锁、Mac 模式 | `FeaturePanels.performance` | 原始字段保持；真实休眠 3→5→3 分钟通过；其余设备写入未逐项验证 |
| Default/Fn/Fn2、166 功能、单键默认 | `KeyboardProfile.remap`、原生键盘图 | 离线保留槽/Fn 层限制；真实 Default.Scroll→F1→原四字节值通过；原生预览 A→F1 与换层清选择通过 |
| 宏名称、触发、事件、延时、容量 | `Macros.swift`、原生宏编辑器 | C# 编码对照、边界和容量测试通过 |
| 宏录制及丢焦点补释放 | `MacroRecorder.swift` | AppKit 区域录制 a、Backspace 得到四个按下/释放事件，原示例宏保留；多键/跨窗口持续录制仍需扩展实测 |
| 宏保存载入、完整上传、绑定 | `AppModel`、`KeyboardSession.uploadMacros` | JSON 兼容数值或字符串 Trigger；上传/绑定代码完成；本轮未覆盖真实设备宏库 |
| 命名配置、JSON、完整恢复比较 | `ProfileLibrary`、`KeyboardProfile`、配置页 | 保存和载入“Swift验收20261102”预览配置通过；C#→Swift→C# 全字段往返通过；完整六块设备恢复未实测 |
| 诊断读取、手工报告、日志 | `diagnostic`、`raw`、诊断页 | 已知只读接口及日志已接入；手工报告需显式确认，不自动发送未知命令 |
| 音频 HID 与原始 output 控制台 | `AudioProtocol`、`AudioService`、`AudioPanel` | 帧校验及旧状态解析离线检查通过；无音频设备实测 |
| 深浅色、窗口缩放 | SwiftUI 原生窗口 | 浅色、深色、较小窗口均完成视觉检查；键盘图完整显示 |

## 本轮实际验证

- C# Core：64 项离线检查通过，在本机 macOS 运行。
- Swift：31 项测试通过，警告视为错误；包括 18 型号、60 组无线样本、宏、备份校验、连续读、保留槽、写入间隔、取消和遇错停止。真实回调发现的非零 Data 起始索引已加入回归测试。
- 实机：Apple Silicon / macOS 27.2，K87 经 USB 接收器使用 2.4G；设备信息与原始回包仅在本地保存。
- 双次全量读取一致。真实写入仅经用户授权执行亮度 `3→4→3`、休眠原始值 `6→10→6`（3→5→3 分钟）、Default.Scroll `→F1→原始四字节值`。
- 每项测试后，全量比较全部已解析字段，均与原始备份一致。比较包含非当前灯效、保留数组、三层所有按键槽；仅排除已校验的协议头 `space0`。
- 这些结果证明上述字段的配置往返，不代替实际灯光观感和操作系统按键行为测试。
- 打包 GUI 首次访问 HID 被 macOS 输入监控权限拦截。CLI 运行所在环境已有权限；GUI 必须由用户在系统设置允许后重启。应用已提供清晰提示和设置入口，不能将 CLI 结果视为 GUI 已通过授权后的实机验收。

## 仍需验证或不属于 Windows 已完成功能的部分

USB 有线的真实报告口径、全量设备恢复、真实宏库上传及触发、实际灯光和键值行为、断连中途取消、其他型号及音频设备、Intel 和旧版 macOS、Developer ID 签名/公证尚未完成实机验收。

音乐/GIF、云模板、固件升级、多设备灯效同步、程序关联切换及 C-Media/THX 内核 DSP 原本就没有在当前 Windows 客户端完成，未把它们记为本次已移植功能。蓝牙也未覆盖。无线接收 checksum 和 ACK 末字节规则沿用已审查 C# 行为，不增加猜测性校验。
