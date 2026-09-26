# Codex Token Meter

> 面向 Codex Desktop 的只读 token 用量与费用浮层。Windows / .NET 10 / WPF。

![状态](https://img.shields.io/badge/状态-可用-5BD6A0)

## 它做什么

在 Codex Desktop 窗口上贴一个浮层，实时显示**当前选中任务**的 token 用量与费用。
跟随窗口移动、不夺取输入焦点、点击面板外自动收起。

- 胶囊态：显示用户选中的指标（默认：输入 / 输出 / 缓存读取 / 上下文占用圆环 / 本轮费用）
- 展开态：本轮与会话累计的完整明细、上下文进度、消息统计、缓存命中率
- 托盘图标：显隐切换、面板开关、胶囊指标选择、浮层位置、设置入口、退出
- 可拖动：位置以「锚点 + 相对偏移」保存，Codex 移动或缩放时仍保持相对位置

**只读**：仅连接 Codex 的本地 IPC 与读取本地会话日志，不修改任何 Codex 数据，
不发起任何网络请求。

> 非官方项目，与 OpenAI 无关联。Codex Desktop 的 IPC 消息与 JSONL 结构属于
> 内部实现细节，未来版本可能变化。

## 快速开始

### 直接运行

从 `artifacts` 取对应压缩包解压运行。两种形态：

| 形态 | 大小 | 要求 |
| --- | --- | --- |
| `-standalone.zip` | 约 67 MB | 无，双击即可 |
| `-lite.zip` | 约 0.14 MB | 需安装 .NET 10 Desktop Runtime |

每个压缩包附 `.sha256` 校验文件。

### 从源码构建

需要 .NET 10 SDK。

```powershell
dotnet build
dotnet test

# 打包
.\scripts\Publish-App.ps1 -RuntimeIdentifier win-x64 -Variant Both
```

## 数据来源

全部来自本机文件，没有任何网络请求。

| 来源 | 位置 | 用途 |
| --- | --- | --- |
| IPC | `\\.\pipe\codex-ipc` | 得知当前跟随哪个会话 |
| 会话日志 | `$CODEX_HOME/sessions/**/rollout-*.jsonl` | 用量与费用 |

会话目录解析顺序：`--sessions <路径>` → `$CODEX_HOME/sessions` → `~/.codex/sessions`。

### 分叉会话

Codex 的「在新窗口继续此会话」类操作会产生**分叉会话**，文件名形如：

```
rollout-<时间>-<父会话id>.jsonl              普通会话
rollout-<时间>-<父会话id>_<子会话id>.jsonl    分叉会话
```

三点容易踩坑：

1. **IPC 广播的 `conversationId` 仍是父 id。** 文件名匹配不能只做
   `EndsWith(id + ".jsonl")`，否则只会命中父文件，浮层会跟随一个
   早已结束的旧会话。实测这个 bug 让浮层显示的是**前一天**的累计值
   （32,564,280），而实际活动会话只有 9,663,195。
2. **分叉会话的 `token_count` 累计值继承自父会话**，而
   `token_usage_record` 只记分叉后新增的调用。实测：

   | | tokens |
   | --- | --- |
   | 父会话末条累计 | 31,224,644 |
   | 分叉首条累计 | 31,327,157 |
   | 分叉末条累计 | 40,198,429 |
   | 分叉逐次记录求和 | 9,663,195 |

   因此「逐次求和 ≥ 累计值」这条不变量在分叉上不成立——两者口径不同：
   累计值描述整个对话历史，逐次记录描述本文件。

   **费用仍必须基于逐次记录**：分叉前的费用已在父会话里计过，
   改用累计值会重复计费。

3. **分叉可能在运行期间产生**，此时 thread id 不变、只有文件路径变化。
   定位结果因此不能被永久缓存，否则会一直命中旧父文件。
   当前实现每 2 秒复查一次（代价约 0.1 ms）。

### 日志定位

不递归遍历全部日期目录。Codex 按 `sessions/YYYY/MM/DD/` 分层存放，
从今天起按日期倒序检索，命中即停——更新的那份必然在更新的目录里。

实测差异（3650 个文件）：

| | 全量递归 | 按日期倒序 |
| --- | --- | --- |
| 单次耗时 | 109.5 ms | **0.125 ms** |
| 每 2 秒复查的 CPU | 5.48% 单核 | **0.006%** |
| UI 卡顿 | 110 ms/次 | 不可感知 |

定位运行在 UI 线程上，109 ms 会造成可见卡顿。
超过 60 天未命中时回退全量扫描，用一次代价换正确性。

### 关键约束：计费基于逐次调用记录

解析使用两类事件，**语义不同，不可混用**：

| 事件 | 粒度 | 用途 |
| --- | --- | --- |
| `token_usage_record` | 每次模型调用 | **计费依据** |
| `token_count` | 每次调用后的快照 | 上下文占用 |

上下文压缩发生时，压缩当次调用的真实消耗**不会计入** `token_count` 的累计值
（实测该次记录的累计增量为 0），但该次调用确实产生了费用。实测差额：

| 会话大小 | 逐次调用求和 | `token_count` 累计 | 差额 |
| --- | --- | --- | --- |
| 5.63 MB | 12,028,236 | 11,798,780 | 229,456 |
| 6.94 MB | 5,207,998 | 4,801,109 | 406,889 |

若改用累计字段计费，压缩越频繁低估越严重。同一约束也决定了日志必须整份读取：
早期版本只读尾部 4 MB，在 6.9 MB 的会话上少算了约 36% 的记录。

同理，`item_completed` 与 `response_item` 描述同一批条目，只计前者；
顶层 `compacted` 与 `item_completed/ContextCompaction` 是同一事件，只计后者。

## 计费口径

价格表单位 USD / 1M tokens，对齐上游 Sub2API 的内置价卡（即厂商官方牌价）。

### 输入只做一次减法

日志中的 `input_tokens` 是**总量**，包含缓存命中与缓存写入。Sub2API 在入库前
把它拆成互斥三桶，本实现沿用同一口径：

```
净输入 = 总量 − 缓存读 − 缓存写   （下限为 0）
```

这个减法**只能做一次**。漏做会让缓存 token 同时按普通输入价与缓存价重复计费；
多做一次会把输入费算成 0 或负数。

### 长上下文：整次请求涨价

触发阈值后**全部 token 按高价计**，不是只对超出阈值的部分。倍率乘在单价上，
缓存读与缓存写都跟随输入倍率（否则缓存命中越多漏计越多）。

判定用量为输入侧总量（净输入 + 缓存读 + 缓存写），不含输出。
`gpt-6-*` 与 `gpt-5.6-*` 使用严格大于阈值，阈值 272000。

### Fast 档：显式价与倍率互斥

`priority` / `fast` 档存在显式档位价时使用该价，**不再叠加倍率**——
两个都套会得到 4 倍价。某个子项缺失时该项独立回落到基础价。
`flex` 永远走 0.5 倍率，`default` 与未知档位按 1.0。

**当前不向计费传入档位，即假设 standard 档。** 原因：

- 会话日志里确实有 `service_tier`，但它位于
  `event_msg/thread_settings_applied` 的 `thread_settings` 中，
  而该事件**不带 `turn_id`**，描述的是「设置发生变更」而非
  「这次调用用了哪个档」。
- 带 `turn_id` 的 `turn_context` 事件**不含** `service_tier`。
- 实测事件顺序：`turn_context` → `token_usage_record` →
  … → `thread_settings_applied`。设置事件出现在部分调用记录**之后**，
  因此无法可靠地把它归属到某次调用。
- 实测全部 76 次出现均为 `default`，即默认情况下的计费是正确的。

如果要支持非默认档位，需要先找到能把档位与调用关联起来的字段。
不能仅凭「最近一次出现的值」推测——那会给已发生的调用套用后续设置，
而档位差异最大可达 2 倍价。

### 金额精度

全程 `float64` 不舍入，仅在展示与对账时量化到 8 位小数（half-away-from-zero），
与 Sub2API 的扣费口径一致。

## 配置

设置文件：`%LOCALAPPDATA%\CodexTokenMeter\settings.json`（首次改动时生成）。

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `Anchor` | `TopRight` | 浮层锚点，0=右上 1=右下 2=左上 3=左下 |
| `OffsetX` / `OffsetY` | `0` | 相对锚点的拖动偏移（DIP），正值指向窗口内部 |
| `TopInset` | `48` | 顶部内缩，用于避开 Codex 自绘标题栏 |
| `Margin` | `12` | 与窗口边缘的间距 |
| `AllowDrag` | `true` | 是否允许拖动浮层 |
| `FollowSystemTheme` | `true` | 是否跟随系统明暗主题 |
| `DarkTheme` | `true` | 不跟随时使用的主题 |
| `Scale` | `1.0` | 整体缩放，范围 0.6–2.0 |
| `CapsuleMetrics` | 见下 | 胶囊态显示的指标，键名数组 |
| `RateMultiplier` | `1.0` | 上游分组倍率 |
| `PricingOverridePath` | `null` | 价格覆盖文件路径 |

### 已知局限

- **分组倍率无法从本地推断。** 上游站点在牌价之上叠加自己的倍率，
  只能由用户在站点后台核对 `actual_cost / total_cost` 后填入 `RateMultiplier`。
  默认按 1.0 计算。
- **价格时效性无法保证。** 厂商调价后本地表不会自动更新。
  例如 `gpt-5.6-sol` 内置值停在旧牌价，与厂商现价不一致。
- **未收录的模型按未定价处理**，不计入合计并在浮层上警示，
  不会回落到同类模型的价格。
- 长上下文阈值与倍率取自上游价卡快照，若上游调整需同步更新。

## 项目结构

```
CodexTokenMeter/
├── src/CodexTokenMeter.Core/          无 UI 依赖的核心库
│   ├── Codex/                         会话与 IPC 数据源
│   │   ├── SessionLogAccumulator.cs   唯一的解析与状态累积实现
│   │   ├── SessionLogReader.cs        全量读取
│   │   ├── SessionLogMonitor.cs       增量读取与快照缓存
│   │   ├── SessionLogLocator.cs       按日期定位日志（避免全盘遍历）
│   │   ├── CodexIpcMonitor.cs         只读订阅 IPC
│   │   ├── ActiveConversationTracker.cs  活动会话追踪
│   │   ├── ProjectResolver.cs         由工作目录推导项目名
│   │   ├── SessionSnapshot.cs         会话快照
│   │   ├── TokenUsage.cs              单次用量
│   │   ├── TokenTotals.cs             计数聚合
│   │   └── SessionPathResolver.cs     会话目录解析与文件名匹配
│   ├── Formatting/NumberFormatter.cs  数值格式化（统一口径）
│   ├── Metrics/SessionMetrics.cs      派生指标
│   ├── Pricing/                       计费
│   │   ├── CostCalculator.cs          单次调用计费
│   │   ├── PricingCatalog.cs          内置价格与覆盖文件
│   │   ├── ModelPricing.cs            价格模型
│   │   └── SessionCostCalculator.cs   按 turn 关联模型并汇总
│   ├── Settings/                      设置与胶囊指标
│   │   ├── OverlaySettings.cs         设置持久化
│   │   ├── CapsuleMetric.cs           可显示指标的目录
│   │   └── CapsuleMetricGrouper.cs    指标分组与分隔线
│   └── Windowing/                     窗口识别与定位
│       ├── CodexProcessIdentifier.cs  按可执行文件路径识别进程
│       ├── CodexWindowSelector.cs     主窗口选择规则
│       ├── OverlayPlacement.cs        定位几何
│       └── NativeWindow.cs            Win32 调用
├── src/CodexTokenMeter.App/           WPF 应用
│   ├── App.xaml.cs                    窗口跟随循环与生命周期
│   ├── OverlayWindow.xaml(.cs)        浮层界面与交互
│   ├── OverlayDataProvider.cs         数据聚合
│   ├── CapsulePresenter.cs            胶囊内容与图标
│   ├── TrayIcon.cs                    托盘图标与菜单
│   ├── MouseHook.cs                   全局鼠标监听（点击外部收起）
│   ├── SelfCheck.cs                   开发期自检
│   └── Themes/                        明暗主题资源
├── tests/CodexTokenMeter.Core.Tests/  单元测试
└── tools/                             开发期诊断工具
```

## 实现要点

### 窗口识别

Codex Desktop 的**进程名是 `ChatGPT`**，不是 `codex`。
可执行文件位于 MSIX 包目录
`C:\Program Files\WindowsApps\OpenAI.Codex_<版本>_x64__<标识>\app\ChatGPT.exe`。

因此识别依据是**可执行文件路径**而非进程名——同名应用可能来自其它厂商。
同目录的 `codex*.exe` 是命令行子进程，不创建主窗口。

主窗口判定：同进程 + 可见 + 非最小化 + 无 owner + 类名 `Chrome_WidgetWin_1`
+ 面积不小于 500×400 + 非 toolwindow + 非 layered。
逐项都对应一种真实存在的干扰窗口（阴影合成层、托盘提示、对话框等）。

### 任务跟随

切换任务时 Codex 会连续发出两条广播——旧会话 `following=false` 与新会话
`following=true`，实测间隔约 4ms。

因此维护 `(sourceClientId, hostId) → conversationId` 映射，只在映射本身变化时
更新对外状态；若收到 `false` 就立即清空，浮层会在切换瞬间闪一下。
多窗口时取最后开始跟随的会话。

### 界面

两层结构：

- **胶囊态**（常驻）：显示用户选中的指标，指标之间按语义插入细分隔线。
  默认显示「本轮输入、本轮输出、缓存读取、上下文占用、本轮花费」。
- **展开态**：项目名与模型标签作为头部，本轮花费用大字号突出，
  下方是上下文进度条与本轮 / 会话累计的双列指标。

#### 选择胶囊指标

托盘菜单 → **胶囊显示指标**，按语义分组列出 18 项可选项，最多同时选 8 项。
配置存于设置文件的 `CapsuleMetrics` 字段（键名数组）。

菜单勾选后会**保持打开**，可以一次调整多项；点菜单外部或按 Esc 关闭。
达到 8 项上限后，未选中的项会置灰。

指标分组决定分隔线位置：

| 分组 | 指标 |
| --- | --- |
| 本轮 | 本轮输入、本轮输出、本轮总量、推理输出 |
| 累计 | 累计输入、累计输出、累计总量、缓存读取、缓存命中率 |
| 上下文 | 上下文占用、上下文用量 |
| 费用 | 本轮花费、累计花费 |
| 计数 | 消息数、工具调用 |
| 会话 | 项目名、模型、活跃时长 |

无论怎么勾选，胶囊都按固定顺序排列（本轮 → 累计 → 上下文 → 费用 → 计数 → 会话），
因此视觉节奏保持一致。

```jsonc
// %LOCALAPPDATA%\CodexTokenMeter\settings.json
{
  // 只保留关心的指标，键名见上表（小驼峰，大小写不敏感）
  "CapsuleMetrics": ["contextPercent", "cacheHitRate", "turnCost"]
}
```

无法识别的键会被忽略；一项都认不出来时回退到默认集合。

#### 移动位置

**直接拖动胶囊**即可。胶囊内侧的鼠标指针为四向箭头，
按住拖到满意位置，松开后自动保存。

拖动带 3 像素的死区：位移小于这个值当作单击（展开/收起面板），
所以不会因为手抖而误拖。

浮层**不会被拖出 Codex 窗口**：超出边界时会收敛回窗口内。
这是有意为之——一个跑到屏幕外的浮层既没用也无法再次抓到。

拖动保存的是**相对宿主的偏移量，不是屏幕坐标**。
这样 Codex 窗口移动或缩放时，
浮层会保持在与同一角落的相同相对位置，而不是掉队。

托盘菜单 → **浮层位置**：

| 菜单项 | 作用 |
| --- | --- |
| 窗口右上角 / 左上角 / 右下角 / 左下角 | 换个角落重新吸附（会重置拖动偏移） |
| 恢复默认位置 | 保留当前锚点，仅清除拖动偏移 |
| 允许拖动 | 关掉后可防止误拖 |

```jsonc
// %LOCALAPPDATA%\CodexTokenMeter\settings.json
{
  // 0=右上 1=右下 2=左上 3=左下
  "Anchor": 0,

  // 相对锚点的偏移（DIP）。正值一律表示「向窗口内部」：
  // 贴右边缘时为向左，贴左边缘时为向右。
  "OffsetX": 0,
  "OffsetY": 0,

  // 与窗口边缘的间距，以及避开 Codex 自绘标题栏的顶部内缩
  "Margin": 12,
  "TopInset": 48,

  "AllowDrag": true
}
```

#### 排版原则

- **费用是主角**。胶囊里只有它用强调色，面板里只有它是大字号。
- **不用均匀列表**。均匀的行高会让所有指标看起来同等重要，
  实际上用户最关心的是“这一轮花了多少”。
- **数值用等宽字体并固定列宽**。否则数字位数变化时浮层会忽宽忽窄，
  相邻两列也会错位。
- **图标共用固定视口**。若直接按路径边界缩放，
  各图标的视觉尺寸会参差不齐。
- **项目名来自 `turn_context.cwd`**，不是日志目录名——
  日志按年/月/日分层，目录名是日期而非项目。

### 健壮性

浮层是常驻后台进程，且没有主窗口。几个原则：

- **可选的配置损坏不应阻止启动。** 设置文件、价格表解析失败时回退到默认值，
  并在界面上提示。早期版本的损坏价格表会直接让进程退出（实测退出码
  `0xE0434352`），而用户得不到任何提示。
- **目录遍历失败不应崩溃。** 会话日志搜索可能因权限、路径过长或目录被并发
  删除而失败；失败时返回已有结果，下一轮重试。
- **未找到日志时节流重试。** 新会话的日志可能几秒后才落盘，
  不节流会让 700ms 的轮询变成每轮一次目录遍历。
- **已找到日志后定期复查。** 分叉会话可能在运行期间产生，
  不复查会一直跟随旧文件。
- **轮询成本必须远低于轮询间隔。** 全量目录扫描实测 109 ms，
  而轮询间隔是 700 ms；它运行在 UI 线程上会造成可见卡顿。
  按日期定位后降到 0.125 ms。
- **全局异常兜底。** 未处理异常写入临时日志，而不是让进程静默消失。

### 不夺取焦点

窗口使用 `WS_EX_NOACTIVATE`，并拦截 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。
点击浮层不会把焦点从 Codex 输入框移走。

点击面板外部收起面板无法用「失去激活」判断（浮层本就不激活），
因此安装低层鼠标钩子做坐标判断。

### 物理像素与设备无关单位

Win32 的窗口矩形是**物理像素**，而 WPF 的 `Left/Top/Width/Height` 是
**设备无关单位（DIP）**。在非 100% 缩放的显示器上两者相差一个缩放系数。

实机踩到：150% 缩放下把物理像素坐标当作 DIP 使用，浮层被画到物理 x=5106，
而屏幕只有 3840 宽，**用户完全看不到任何内容**。

所有跨越 Win32 与 WPF 边界的数值都必须显式换算，包括：

| 位置 | 来源 | 换算方向 |
| --- | --- | --- |
| 宿主窗口矩形 | `DwmGetWindowAttribute` | 物理 → DIP |
| 浮层窗口矩形 | `GetWindowRect` | 物理 → DIP（用于比对） |
| 鼠标钩子坐标 | 低层鼠标钩子 | 物理 → DIP |

### CPU 占用

实机优化前：在 570 个顶层窗口的桌面上每 60ms 全量枚举窗口，
**单核占用 99%**，且其中绝大多数轮次没有任何变化。

优化后降到约 3%，分三级预检：

1. 前景窗口句柄是否变化（1 次调用）
2. 已缓存宿主的矩形是否变化（1 次调用，实测约 3 微秒）
3. 浮层自身是否被外部挪动（1 次调用；显示器插拔、分辨率切换会让窗口位置失效）

任一命中才做完整枚举与重定位。实测三项合计约 3 微秒/轮，
相对全量枚举降低约 30 倍。

### 开发期诊断参数

均为可选项，普通使用无需关心。

| 参数 | 作用 |
| --- | --- |
| `--force-follow` | 忽略前台限制，始终跟随 Codex 主窗口 |
| `--trace-follow` | 把跟随循环的预检结果写入临时日志 |
| `--self-check` | 自动验证定位、尺寸、数据链路并输出报告 |
| `--check-placement` | 只报告锚点与偏移下的预期/实际位置，用于排查「设置改了但位置没变」 |
| `--start-expanded` | 启动后自动展开面板，便于截图 |
| `--theme-stress` | 反复切换主题后报告合并字典数量，验证不累积 |
| `--metrics a,b,c` | 指定胶囊指标，便于验证任意组合的渲染 |
| `--dump-settings` | 打印解析后的设置后退出，不依赖 Codex 是否运行 |
| `--toggle-metric <键名>` | 模拟菜单勾选一项后退出，验证持久化链路 |
| `--restore-default-metrics` | 模拟「恢复默认」后退出 |
| `--drag x,y` | 模拟把浮层拖到指定位置（DIP），验证拖动链路 |
| `--settings <路径>` | 使用指定的设置文件，便于隔离测试 |
| `--sessions <路径>` | 使用指定的会话日志目录 |
| `CTM_FOLLOW_MS` | 覆盖跟随间隔（毫秒）；设为 0 可完全关闭跟随循环 |
| `CTM_DATA_MS` | 覆盖数据刷新间隔（毫秒） |

诊断输出写入临时目录：

| 文件 | 内容 |
| --- | --- |
| `codex-token-meter-selfcheck.log` | 自检报告 |
| `codex-token-meter-follow.log` | 跟随循环预检（需 `--trace-follow`） |
| `codex-token-meter-crash.log` | 未处理异常（正常情况下不应出现） |

## 开发期工具

```
tools/
├── CodexTokenMeter.Probe/        真实会话数据的一致性核对、性能与规模测量
├── CodexTokenMeter.IpcProbe/     IPC 帧协议观测
├── CodexTokenMeter.WindowProbe/  窗口识别规则验证
├── CodexTokenMeter.GeometryCheck/ 定位几何验证
├── CodexTokenMeter.Screenshot/   窗口截图
├── CodexTokenMeter.ImageCheck/   截图像素分析
└── CodexTokenMeter.FollowCheck/  位置纠正验证
```

这些工具只用于开发期验证，不进入发布产物。

## 许可

MIT
