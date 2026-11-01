# MCHOSE HUB · Swift 原生版

使用 Swift 6、SwiftUI、AppKit、IOKit 实现现有 Windows 客户端的功能，不依赖 C#/.NET 运行时。最低部署目标 macOS 14；当前构建及运行环境为 Apple Silicon、macOS 27.2。

## 功能

- 原生设备列表，USB location / registryID 物理设备分组；只读枚举不打开 HID、不发送报告。产品名、已收录型号、厂商集合和报告描述符共同确认配置通道。
- 18 个已有键盘型号的 schema、布局、三层键表、166 项官方功能码、逐键 LED 索引、出厂配置与型号差异。
- USB feature 和 2.4G output/input 两套传输。无线先读取型号签名，再选择 schema；有线报告 ID 随型号使用 06 或 09，无线为 13。
- 灯效选择、亮度、速度、多彩、RGB、逐键颜色和全表填充、灯效参数修复、强制点亮草稿。
- 回报率、休眠、延迟模式、Win 锁定、Mac 模式；Z75 专用延迟选项和型号对应的 Mac 模式值。
- Default / Fn / Fn2 实体键及官方功能映射、恢复单键默认；Fn 层拒绝绑定层切换功能，保留未编辑的原始四字节键值和保留槽。
- 宏编辑、名称/触发方式、键盘/鼠标事件、延时、JSON 导入导出、AppKit 焦点内录制、丢失焦点和停止时补齐释放、容量限制、完整宏库上传及绑定。宏改动后自动使旧上传状态失效。
- 命名本地配置库、JSON 导入导出、默认配置草稿、完整备份恢复。预览库与真实配置库分开；配置保存、导入、载入和草稿编辑都不自动写设备。
- 应用前保存新备份，检查设备是否在编辑后发生变化，只写改变的配置块；恢复顺序为灯色、DIY、三层按键、性能。每块稳定回读，最后全量比较；失败清除缓存，停止后续写入，不自动再次完整恢复。
- 音箱/耳机的已有 HID 功能：结构化/第一代协议探测、原始状态、第一代状态解析、灯光、EQ 预设、播报、恢复默认、耳机信息和手工 output 报告。与 Windows 一样，第二代音频状态字段和 C-Media/THX 内核 DSP 并未实现。
- 已知只读命令、手工报告、原始日志、应用内浅色/深色/跟随系统、窗口缩放、断连取消及旧会话结果丢弃。

完整对照和验证边界见 [功能与验收记录](VALIDATION.md)。代码入口存在、离线对照通过及真实硬件验证是不同证据。

## 使用

打开 `MCHOSE HUB.app`。首次访问 HID 输入报告时，macOS 可能要求在“系统设置 → 隐私与安全性 → 输入监控”中允许此应用，之后退出并重新打开。应用不会自动修改系统权限。独立预览和本地编辑不需要该权限。

左侧选择设备，点击“读取完整配置”。读取顺序为电量、性能、灯色、DIY、Default/Fn/Fn2，每块要求连续两份一致，再做第二次全量读取比较。编辑后通过“应用当前草稿到设备”写入，确认框会说明备份和写入行为。恢复前备份保存在本机 `Library/Application Support/MchoseHub/backups/`。

宏上传会覆盖设备完整宏库，协议不能备份完整宏库，且上传后只能回读验证首个目录项；请先另存宏文件。宏未改动、上传目录通过且设备会话一致时，才允许绑定到按键草稿。绑定后仍需应用配置。实际事件和触发行为应另行验证。

CLI 与旁边的 `MchoseHubMac_MchoseCore.bundle` 必须放在同一目录：

```sh
./mchose list
./mchose watch --seconds 15
./mchose backup --device <registryID> --out /absolute/path/new-backup
./mchose validate --profile profile.json
./mchose roundtrip --profile profile.json --out new-profile.json
./mchose compare --expected before.json --actual after.json
./mchose restore --device ID --profile target.json --backup /absolute/path/new-before.json --experimental-writes
```

输出路径必须不存在。备份目录包含兼容 Windows 的 `profile.json`、配置请求/回包、设备描述符及本机环境记录。2.4G 另外保存原始分包；失败也保留已收到的原始资料。真实设备标识、报告和备份只应保留在本地，不提交仓库。

## 从源码构建

```sh
python3 tools/sync-macos-resources.py --check
cd macos
swift test -Xswiftc -warnings-as-errors
swift run MchoseHub
bash build-app.sh /absolute/path/new-package
```

打包脚本生成当前机器架构的 app 和 CLI，采用本地 ad-hoc 签名，没有 Developer ID 签名或公证。系统可能为每个签名变化后的构建重新要求权限。最低部署目标不代表已经在 macOS 14/15 或 Intel Mac 上实测。

## C# / Swift 对照

`tools/MchoseHub.MacFixtures` 只使用合成数据，导出 18 个型号配置及恢复报文、K87 读报告、宏编码和 60 组无线分包。Swift 对照每个字节，JSON 完成 C# → Swift → C# 往返验证。C# 只作为开发期参考，不随 Swift 应用运行。

```sh
dotnet build src/MchoseHub.Core -c Release -p:TreatWarningsAsErrors=true
dotnet run --project tests/MchoseHub.Tests -c Release -p:TreatWarningsAsErrors=true
dotnet run --project tools/MchoseHub.MacFixtures -c Release -- /tmp/new-fixtures
diff -ru macos/Tests/MchoseCoreTests/Fixtures /tmp/new-fixtures
cd macos
swift run mchose roundtrip --profile Tests/MchoseCoreTests/Fixtures/profile.json --out /tmp/new-swift-profile.json
dotnet run --project ../tools/MchoseHub.MacFixtures -c Release -- --validate /tmp/new-swift-profile.json
```

CI 检查资源与 fixtures 同步、运行 Core 和 Swift 测试、验证双向 JSON 兼容、构建原生 app 和 CLI。CI 不访问真实设备。
