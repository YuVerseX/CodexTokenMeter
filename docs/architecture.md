# 实现与计费依据

本文件记录必须保留的实现约束。安装、配置示例和构建命令见 [README](../README.md)。
本项目不是 Codex 或 Sub2API 的账单系统；金额只是基于本地日志和固定价卡的估算。

## 数据链路

- `CodexIpcMonitor` 只读订阅本地 `\\.\pipe\codex-ipc`，确定当前活动会话；
  不向 Codex 写入消息。
- 会话日志从 `--sessions <路径>`、`$CODEX_HOME/sessions`、
  `~/.codex/sessions` 中依次选取。首次读取整份 JSONL，随后增量消费新增行。
  只读文件尾部会漏掉先前调用，低估费用。
- `turn_context` 给出某个 turn 的模型与工作目录；`token_usage_record`
  记录每次调用的用量；`token_count` 是上下文与累计快照，**不能用来计费**。
  压缩上下文时，逐次用量与累计快照可能不一致。
- 文件末尾尚未写完的 JSON 行保留待续写，不计为永久损坏；换行后仍无法
  解析的完整行才记录为异常。费用只在日志快照变化后重新汇总。

### 分叉与切换

分叉文件名为 `rollout-<时间>-<父id>_<子id>.jsonl`，而 IPC 仍可能报告父 id。
定位器匹配父会话及分叉文件，按日期由近到远查找，并定期复查已找到的路径；
分叉在运行中出现时重置旧日志状态。分叉的 `token_count` 累计继承父会话，
其逐次调用记录只覆盖分叉后的请求，两者不能直接比较。

切换任务时，IPC 可能先报告旧会话 `following=false`，再报告新会话
`following=true`。追踪器按 `(sourceClientId, hostId)` 维护跟随关系，
避免短暂的取消跟随把活动会话清空。相关行为有模拟 IPC 的回归测试；
实机仍应在发布前观察当前 Codex 版本的广播。

## 计费口径

内置价格单位为 USD / 1M tokens。2026-09-27 对照固定的
[Sub2API 价卡资源](https://github.com/Wei-Shaw/sub2api/blob/a3eb7ef302961cba716dc78b39b93b60c467db0e/backend/resources/model-pricing/model_prices_and_context_window.json)
与[计费实现](https://github.com/Wei-Shaw/sub2api/blob/a3eb7ef302961cba716dc78b39b93b60c467db0e/backend/internal/service/billing_service.go)
核验：五个模型来自价卡资源，`gpt-6-astra` 来自后备价。没有该上游依据的
DeepSeek 价格不内置。快照不会自动更新，也不保证等于厂商现价或具体实例的
渠道自定义价。

一次调用的 `input_tokens` 包含缓存读取和缓存写入，只拆分一次：

```text
净输入 = 总输入 - 缓存读取 - 缓存写入
费用 = 四个互斥桶各自用量 × 单价 / 1,000,000
```

`gpt-6-*` 与 `gpt-5.6-*` 的内置规则在输入侧总量**严格超过** 272,000 时，
对整次请求使用长上下文倍率，缓存读写跟随输入倍率。`priority` / `fast`
的显式价与档位倍率互斥，`flex` 默认 0.5 倍率；计算过程不舍入。
界面费用按量级显示 2–6 位小数，对账时可量化到 8 位小数。

**应用当前按默认服务档位估算。** 日志中的 `service_tier` 设置事件不能可靠
归属到每次调用，不能把最近一次设置倒套到已经发生的请求。站点的分组倍率
也无法从日志推断，需按账单核对后在设置中填写 `RateMultiplier`。
图片、每请求计费和推理等级倍率等特殊计费路径不在本地文本 token 口径内。

每条用量只按所属 turn 的可靠模型定价，不用会话主导模型猜测。未知模型、
缺少 turn 模型或异常 token 会使费用合计不可用，而不是静默低估；
没有调用时费用使用占位符。价格覆盖文件每个模型必须有 `input`、
`output`、`cacheRead`、`cacheWrite` 四项非负有限基础价；任一条目无效则
整份覆盖不生效，回退内置表并告警。可选字段的约束由 `PricingCatalog` 校验。

## 窗口与刷新

Codex Desktop 主进程按可执行文件路径识别，不单凭进程名判断。浮层采用
不激活窗口样式；在 Codex 不处于前台时隐藏。跟随循环先检查前景句柄、
宿主矩形及浮层自身位置，必要时才重新枚举与定位。

Win32 矩形和鼠标钩子坐标是物理像素，WPF 的位置、尺寸与设置偏移使用 DIP；
跨边界时必须换算。锚点和相对偏移随宿主窗口移动，定位计算把浮层约束在
宿主内。面板展开、主题变化或内容尺寸变化会触发重新定位。

`OverlayDataProvider.Poll` 与释放资源用同一把锁；费用仅在日志快照改变时
重算，完整数据记录比较决定是否通知界面，未变化时跳过重新渲染。

## 配置

设置文件为 `%LOCALAPPDATA%\CodexTokenMeter\settings.json`，首次更改设置时
生成；可用 `--settings <路径>` 隔离测试。常用字段及价格覆盖示例见
[README](../README.md#设置与价格)。价格覆盖文件中的 `priority` 子项可省略；
`fastMultiplier`、`flexMultiplier` 必须大于 0；`longContext` 需要正的
`inputTokensAbove` 和非负的 `inputMultiplier`、`outputMultiplier`，可设置
`thresholdInclusive`。非法文件整份回退，界面显示原因。

## 诊断与验证

| 参数 | 用途 |
| --- | --- |
| `--self-check` | 验证宿主、尺寸、位置与数据链路后退出 |
| `--check-placement` | 比对锚点与偏移的预期/实际位置后退出 |
| `--force-follow` | 忽略前台限制，供截图和诊断使用 |
| `--start-expanded`、`--theme-stress` | 检查展开布局与主题资源切换 |
| `--metrics a,b,c`、`--drag x,y` | 隔离检查胶囊指标与位置保存 |
| `--dump-settings`、`--toggle-metric <键名>` | 检查设置读取与指标持久化 |
| `CTM_FOLLOW_MS`、`CTM_DATA_MS` | 覆盖跟随与数据轮询间隔（毫秒） |

自检、跟随跟踪和异常日志写在 `%TEMP%`，可能含会话标识或本机路径，
不应提交到 Git。`tools/` 中的探针只用于开发诊断，不进入发布压缩包。
验收命令见 [README](../README.md#从源码验证与打包)；实机测试应使用隔离设置，
并分别检查实际设备的 DPI、任务切换和托盘操作。
