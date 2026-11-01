# 官方数据快照提取

此工具仅静态解析官方 JavaScript，不执行下载的脚本。修复了变量名含 `$` 时漏掉 G87_V2、Z75 字段的问题。源文件地址、日期和 SHA-256 见 `snapshot.json`。

在此目录执行 `npm ci --ignore-scripts`，下载快照到项目外的工作目录，然后运行：

```powershell
node extract.mjs "C:\path\official-bundle.js"
```

生成三个 Core 嵌入资源：型号字段和布局、功能码和宏按键码、出厂默认值。官方快照发生变化时会拒绝覆盖，需要重新审查符号映射和协议。构建应用无需安装 Node.js。

无线协议来自同一快照的 `j3e`、`dpe`、`Uat`：0x13 报告、14 字节数据、校验和、型号标识和确认包。对应实现在 `KeyboardWirelessProtocol.cs` 与 `KeyboardWirelessChannel.cs`。
