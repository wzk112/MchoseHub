# macOS 原生版移植说明

更新：2026-11-02。当前交付的是 Windows 版；尚未实现或在 Mac 上验证原生客户端。首个目标为 **K87 USB 有线**，先完成可靠的读取、备份和恢复，再接入原生界面。2.4G 后续单独验证。

## 现有内容如何复用

| 内容 | 处理方式 |
| --- | --- |
| `src/MchoseHub.Core/Protocol`、`Resources` | 复用型号 JSON、字段布局、命令、功能码、灯效和宏编码规则。K87 使用现有 schema，不能套用磁轴型号或假设存在多个板载槽。 |
| `src/MchoseHub.Core/Services` | 复用配置校验、三层按键、完整恢复比对、宏录制边界与失败后缓存失效规则。 |
| `KeyboardProfile`、`KeyboardProfileLibrary` | 保持 JSON 的版本、型号、Numbers/Buffers/Tables 及三层顺序，保留字节数组的序列化格式和未知保留字节；用双向导入导出验证兼容。配置不包含完整宏库。 |
| `tests/MchoseHub.Tests` | 仅依赖 Core，当前 Windows 离线基线为 64 项通过；可在 Mac 上先运行，再作为 Swift 实现的逐字节对照。尚未在 Mac 上跑过。 |
| `MchoseHub.App` | WinUI / Windows App SDK 界面、窗口处理和键盘录制入口需要重新实现。 |
| `MchoseHub.Platform.Windows`、`MchoseHub.Cli` | SetupAPI、hid.dll、Win32 句柄及当前 CLI 入口依赖 Windows，需要新增 Mac 传输和入口。 |

Core 和测试的目标框架是 `net8.0`；App、Platform.Windows 和 CLI 使用 Windows 专用目标。通用 .NET 目标与操作系统专用目标的区别见 [Microsoft 目标框架说明](https://learn.microsoft.com/en-us/dotnet/standard/frameworks)。C# Core 不是可直接导入 Swift 的模块：建议先保留它作为离线参考实现，将需要的业务逻辑移入 Swift，并逐项对照测试；若保留 C# 运行时，则还需单独设计桥接、进程生命周期和打包方案。

## 推荐原生结构

- **SwiftUI + AppKit**：设备列表、键盘图、灯光、性能、宏和命名配置使用原生控件；需要精确焦点和按键释放事件的录制区域可用 AppKit 视图。Apple 支持通过 `NSViewRepresentable` 等方式组合两者，见 [AppKit integration](https://developer.apple.com/documentation/swiftui/appkit-integration)。
- **独立配置服务**：UI 只提交编辑意图，设备操作串行执行；切换或拔出设备时取消旧任务，丢弃旧会话回调，失败后重新读取配置。预览模式使用独立数据，不触碰设备。
- **IOKit / IOHIDManager 传输**：以用户态 HID 枚举、feature 请求和 input 回调实现配置通道；匹配条件由型号、产品名、厂商集合和报告描述符共同确定，见 [IOHIDManagerSetDeviceMatching](https://developer.apple.com/documentation/iokit/1438371-iohidmanagersetdevicematching)。同步 HID 操作放在后台串行队列；移除时停止接收、结束在途操作后再释放句柄与回调缓冲。

上述为实施路径，本仓库暂未提供未经 Mac 验证的 Swift 或 IOKit 实现。

## K87 报告边界

下表描述**当前源码的接口约定**，不是对 Mac 实际回包长度的预先保证。移植时以 `IHidTransport.cs`、`KeyboardProtocol.cs`、`KeyboardSession.cs`、`KeyboardWirelessProtocol.cs` 和实机描述符为准。

| 通道 | 当前源码约定 |
| --- | --- |
| K87 有线 | 产品名 `MCHOSE K87 Gaming Keyboard`，已知 VID/PID `05AC:024F`；feature report ID `0x06`。发送 payload 为 519 字节，不含 ID；Windows 端完整报告为 520 字节。 |
| `SendFeature` / `SendOutput` | report ID 单独传入，payload 不含 ID。平台层负责适配原生 API。 |
| `GetFeature` | 当前返回数组包含首字节 ID。即使调用参数是 `GetFeature(id, 519)`，有线解析器也要求 520 字节，并检查 ID 和七字节命令回显。 |
| `InputReportReceived` | `ReportId` 单独提供，`Data` 不含 ID；不能套用 `GetFeature` 的返回约定。 |
| 2.4G | 独立 output/input 通道：ID `0x13`，逻辑包 19 字节，每包数据 14 字节；Windows 完整报告至少 20 字节。接收器识别得到 K87 签名 `030000000135` 后才能选用 K87 schema。 |

Apple 的 [IOHIDDeviceSetReport](https://developer.apple.com/documentation/iokit/1588656-iohiddevicesetreport) 同时接收 `reportID` 和缓冲区，并要求多报告设备在报告首字节设置 ID。因此不能因为 API 有独立 ID 参数就直接传入 519 字节负载，也不能在多层重复添加 ID。先核对描述符及原始数据，只在平台边界规范化一次。

[IOHIDDeviceGetReport](https://developer.apple.com/documentation/iokit/1588659-iohiddevicegetreport) 会更新实际返回长度；必须按该长度检查数据，不能补零掩盖截断。Input report 应走输入回调，并按回调提供的 ID、长度和实际缓冲内容转换为上述事件约定。

当前有线筛选要求厂商 UsagePage `>= 0xFF00`、规范化后的 `FeatureReportLength == 520` 及已识别产品名。Mac 枚举长度若采用不同口径，应在适配层明确转换。仅凭 `05AC` 或 VID/PID 不足以选择配置接口；物理设备分组也需用 Mac 的设备身份实现，不能沿用 Windows ContainerId。

## 第一次在 Mac 上开发

1. 克隆仓库，安装 Xcode 和适合本机架构的 .NET SDK；运行当前测试还需有 .NET 8 运行时。Apple Silicon 选择 Arm64，Intel 选择 x64，系统兼容范围以 [Microsoft macOS 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/macos) 为准。
2. 在仓库根目录先运行下面的离线命令，只构建 Core 和测试。现有完整 solution、Windows CLI 和 `build.ps1` 不能作为 Mac 构建入口。

```sh
dotnet --info
dotnet --list-runtimes
dotnet build src/MchoseHub.Core/MchoseHub.Core.csproj -c Release -p:TreatWarningsAsErrors=true
dotnet run --project tests/MchoseHub.Tests/MchoseHub.Tests.csproj -c Release -p:TreatWarningsAsErrors=true
```

3. 建立最小原生枚举程序，记录 Mac 型号、系统版本、设备固件、连接方式、产品名、VID/PID、Usage、报告 ID 和长度。先只枚举并测试拔插，不发送配置请求；原始设备标识和备份保留在本地，不提交到 GitHub。
4. 确认 K87 厂商配置集合后，只发已知读取请求：电量 → 性能 → 灯色 → DIY → Default/Fn/Fn2。逐次核对回显和长度，保存原始回包与解析结果。这里的“只读”允许发送读取请求，但禁止配置写命令；无法识别接口时停止，不扫描未知写命令。
5. 导出完整 `KeyboardProfile`，先离线验证型号、结构、字段长度与 JSON 往返一致，再读取第二次比对；出现变化时调查设备模式或外部程序，不拿旧基线覆盖当前设置。宏库不在这份可恢复备份内。
6. 进入有线可恢复测试后，每次只改一个字段：亮度小步变化 → 回读 → 恢复 → 再回读；随后才测试一个休眠字段和一个不常用按键。保留原始四字节键值和所有保留槽；写入间隔至少 1.5 秒，失败立即停止后续测试，按已保存的对应原值尝试恢复并验证，禁止无界重试。
7. 最后验证完整备份恢复：按现有 `RestoreProfile` 顺序恢复灯色、DIY、三层键表、性能，再全量只读比较。只排除经输入校验的协议头 `space0`；非当前灯效参数和保留槽也必须一致。差异应报告具体字段，不能自动再发一轮完整恢复。
8. 有线通过后接入 SwiftUI 页面，验证断连、换设备、窗口缩放、深浅色、录制焦点与释放事件；再独立推进 2.4G。记录真实灯光和按键行为，不能只凭配置回读宣称全部功能已验证。

## 验证范围

Windows K87 有线已有亮度、休眠和单键映射的恢复证据；这些结果不能替代 Mac 实测。宏先做本地编码、录制与容量测试，未能备份完整宏库时不覆盖设备宏库。2.4G 目前仅有离线协议验证，蓝牙未覆盖；无线接收包及 ACK 末字节的校验规则仍需证据，不凭猜测改动兼容行为。

音乐/GIF、云模板、固件升级、多设备灯光同步及程序关联切换仍未原生实现。首次 Mac 验证不包含固件升级、恢复出厂或新增板载配置槽。
