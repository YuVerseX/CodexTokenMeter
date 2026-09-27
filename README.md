# Codex Token Meter

面向 Codex Desktop 的 Windows 浮层，显示当前任务的 token 用量和**费用估算**。
胶囊与展开面板可切换；可从托盘选择指标、拖动定位和切换主题。
程序只读订阅 Codex 本地 IPC、读取本机会话日志，不修改 Codex 会话数据，
也不由程序发起网络请求。本项目非官方，与 OpenAI 无关联。

## 运行

需要 Windows、Codex Desktop，以及与设备架构匹配的 `win-x64` 或 `win-arm64` 包。
`standalone` 自带 .NET 运行时；`lite` 需要预先安装 .NET 10 Desktop Runtime。
解压包后运行 `CodexTokenMeter.exe`。托盘菜单可以显隐浮层、展开面板、
选择胶囊指标、设置锚点和退出。浮层只在 Codex 窗口位于前台时显示。

仓库不含可下载的二进制包：`artifacts/` 被 Git 忽略，是本地打包输出目录，
不是仓库下载入口。请从源码打包，或在正式发布后使用发行版提供的包。

## 从源码验证与打包

安装 .NET 10 SDK，在仓库根目录运行：

```powershell
dotnet restore CodexTokenMeter.slnx
dotnet build CodexTokenMeter.slnx -c Release --no-restore
dotnet test CodexTokenMeter.slnx -c Release --no-restore
dotnet format CodexTokenMeter.slnx --verify-no-changes --no-restore
dotnet list tests/CodexTokenMeter.Core.Tests/CodexTokenMeter.Core.Tests.csproj package --vulnerable --include-transitive
.\scripts\Publish-App.ps1 -RuntimeIdentifier win-x64 -Variant Both
```

脚本输出 `artifacts/CodexTokenMeter-win-x64-{standalone,lite}.zip` 及同名
`.zip.sha256`。校验文件每行是 `SHA-256  文件名`，例如：

```powershell
Get-FileHash artifacts/CodexTokenMeter-win-x64-standalone.zip -Algorithm SHA256
Get-Content artifacts/CodexTokenMeter-win-x64-standalone.zip.sha256
```

`-RuntimeIdentifier win-arm64` 可交叉打包；没有相应设备时，打包通过不等于
ARM64 实机验证。打包脚本可以指定 `-OutputDirectory`，只清理本次创建的
临时发布目录，不清理已有的其他输出目录。

## 设置与价格

设置文件位于 `%LOCALAPPDATA%\CodexTokenMeter\settings.json`，首次更改设置
时生成。用户常用项：`CapsuleMetrics`（最多 8 项）、`Scale`（0.6–2.0）、
`RateMultiplier`（上游分组倍率，默认 1）和 `PricingOverridePath`。
位置由 `Anchor`、`OffsetX`、`OffsetY`、`TopInset`、`Margin` 控制，偏移单位为 DIP。
进阶字段与诊断参数见[实现文档](docs/architecture.md#配置)。

价格覆盖文件是独立的 JSON 文件；在设置中填写其绝对路径，例如：

```json
{
  "PricingOverridePath": "C:\\Users\\me\\prices.json",
  "RateMultiplier": 1.0
}
```

覆盖文件例子（单位：USD / 1M tokens；以下数字仅演示格式，不代表实时价）：

```json
{
  "models": {
    "my-model": {
      "input": 2.0,
      "output": 10.0,
      "cacheRead": 0.2,
      "cacheWrite": 2.5
    }
  }
}
```

每个模型都必须提供四项基础价，均为非负有限数；可选的 `priority`、
`fastMultiplier`、`flexMultiplier`、`longContext` 见[计费口径](docs/architecture.md#计费口径)。
任一条目无效，**整份覆盖不生效**并在浮层提示，恢复到内置价；不存在、
不可读取或无有效条目也会提示。未知模型、缺少所属 turn 的可靠模型、
异常用量不会借其他模型猜价，合计金额显示“未定价”。

金额是**估算**，不是站点账单：内置价格是固定快照，不自动更新；
本地日志无法可靠把服务档位与单次调用关联，当前按默认档计算；
站点分组倍率也无法从日志推断，需要自行核对并设置 `RateMultiplier`。
如需对账，请核对模型、价卡日期、档位和倍率。浮层中的 token 用量与
费用口径不同，不能用 `token_count` 累计值直接推算费用。

## 排障

- 没有浮层：确认 Codex 主窗口在前台、托盘没有隐藏本程序、会话日志目录存在。
- 价格异常或“未定价”：检查价格文件警告、模型标识、四项价格、档位和倍率。
- 位置异常：托盘选择“恢复默认位置”，检查 Windows 缩放及设置中的偏移。
- 启动异常：检查 `%TEMP%\codex-token-meter-crash.log`；诊断时可用
  `--self-check --settings <临时设置路径>`，日志可能含本机路径，勿上传。

实现约束与开发期诊断参数见[实现与计费依据](docs/architecture.md)。

## 许可

MIT
