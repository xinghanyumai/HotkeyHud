# HotkeyHud — 快捷键 HUD · 开发文档

> 面向用户的说明见根目录 [`../README.md`](../README.md)。本文件是开发日志：架构、决策、验收、偏差。


长按修饰键（如 Ctrl）时在屏幕浮出当前应用的相关快捷键面板。需求源见
[`shortcut-display-spec.md`](shortcut-display-spec.md)，执行约束见
[`executor-agent-prompt.md`](executor-agent-prompt.md) 与各阶段提示词。

## 技术栈与已知代价

| 层 | 选型 |
|---|---|
| 语言 / 运行时 | .NET 10（`net10.0` / `net10.0-windows`） |
| UI | WinForms + P/Invoke + GDI+ 分层窗口（`UpdateLayeredWindow`） |
| 核心逻辑 | `HotkeyHud.Core` 纯类库，零 UI 依赖 |
| 测试 | 控制台断言程序，不引测试框架 |
| 数据 | 每应用一份 JSON |

规格 §2.2 预估 `.NET + WinForms` 空载私有工作集 30–45 MB，产品目标是 ≤ 12 MB。

### 内存三口径（不可互相替代）

| 口径 | 含义 | 实测（空载，未显示过面板） |
|---|---|---|
| 私有工作集 | 进程独占的物理页，S6 优化的基线 | 5.3 MB |
| 提交大小 | 已提交的虚拟内存（含换出页），§八 的 45 MB 门槛按此口径 | 8.7 MB |
| 总工作集 | 私有 + 与系统共享的框架页（CLR / WinForms / GDI+） | 33.6 MB |

三者由 `GetProcessMemoryInfo` 的 `PROCESS_MEMORY_COUNTERS_EX2` 一次取全。
**不要用 `Process.PrivateMemorySize64` 当私有工作集**，那是提交大小。

若内存成为硬指标，需换 C++/Rust + Direct2D 重做平台层与渲染层；规则层可照搬，
这就是 `HotkeyHud.Core` 禁止引用 `System.Windows.Forms` / `System.Drawing` 的原因。

## 目录

```
src/HotkeyHud.Core/           纯逻辑，零 UI 依赖
  Keys.cs                     修饰键分类、组合解析、VK 名称映射（§3.2、§4）
  ShortcutModel.cs            条目 / 档案 / HUD 模型（§6）
  KnownShortcutFilter.cs      熟记名单（§4）
  ShortcutRepository.cs       按进程名索引（§3.2）
  GameClassifier.cs           游戏判定纯函数（§2.1–§2.2）
  HudBuilder.cs               查询 + 过滤 + 分组（§3.2、§4.3）
  TriggerStateMachine.cs      状态机，时钟外部注入（§5）
src/HotkeyHud.App/
  Platform/NativeMethods.cs   全部 P/Invoke 声明
  Platform/RawInputSource.cs  Raw Input 采集；同时是全程序唯一的时钟来源（NowMs）
  Platform/ForegroundWatcher.cs  前台窗口变化监听（SetWinEventHook）
  Platform/ForegroundProbe.cs    前台进程名查询（缓存 + Invalidate）
  Platform/SuppressionProbe.cs   §2.1 六个抑制信号采集（QUNS / 矩形 / 样式 / 类名 / 进程）
  Platform/ScreenAnchor.cs       按光标挑屏 + 上缘翻转（纯函数，可喂合成布局断言）
  Shell/AppConfig.cs             配置模型（只暴露 4 个字段）
  Shell/ConfigStore.cs           config.json 读写 + 损坏留证
  Shell/TrayIcon.cs              托盘图标（代码绘制 16×16）与 6 项菜单
  Shell/AutoStart.cs             HKCU Run 写入/删除
  Shell/SingleInstance.cs        单实例互斥
  Shell/UserApps.cs              用户档案模板与写入（%APPDATA%\HotkeyHud\apps）
  Shell/SettingsForm.cs          设置页：应用列表 + 条目表 + 按键录入 + 8 色预设（手写布局）
  HudColors.cs                   键帽预设色（8 个，含「默认」）
  Shell/Adapters/VsCodeAdapter.cs     读 keybindings.json（只读用户改过的键位）
  Shell/Adapters/JetBrainsAdapter.cs  读 keymaps/*.xml（只取 first-keystroke）
  Shell/ShellController.cs       外壳编排：配置 → HudApp → 托盘
  UI/OverlayForm.cs           分层覆盖窗：窗口、DIB、分层合成
  UI/HudPanel.cs              面板布局与绘制（不碰 HWND），尺寸按内容算
  Data/apps/*.json            6 份应用档案 + _system.json（系统快捷键），随 exe 输出
  Diagnostics/                自检、集成测试、渲染画廊，仅 Debug 构建编译
  HudApp.cs                   编排：Raw Input → 状态机 → 覆盖窗
  Program.cs                  入口 + 严格参数解析 + 崩溃日志
tests/HotkeyHud.Tests/        §7 的 16 条规则用例 + Case17–19
```

## 构建与运行

```bash
dotnet build HotkeyHud.slnx -warnaserror

# 常驻：长按 Ctrl / Alt / Shift / Win 弹出面板
dotnet run --project src/HotkeyHud.App
```

F9 手动开关已在 S2.5 移除——它会和状态机抢状态。

## 验证（九项门禁）

| # | 套件 | 命令 | 当前 |
|---|---|---|---|
| ① | 规则用例（§7） | `dotnet run --project tests/HotkeyHud.Tests` | 22/22 |
| ② | 集成（Raw Input → 状态机 → 覆盖窗） | `... -- --integration-test` | 10/10 |
| ③ | 渲染画廊（截图 / 高亮不改尺寸 / p95） | `... -- --render-gallery --out=<目录>` | 8/8 |
| ④ | 抑制（全屏 / 平台目录 / 采集开销） | `... -- --suppression-test` | 6/6 |
| ⑤ | 外壳（托盘 / 暂停 / 配置 / 自启 / 单实例 / 设置页 / 首次引导） | `... -- --shell-test` | 13/13 |
| ⑥ | 打磨（混合 DPI / 上缘翻转 / 淡入） | `... -- --s6-test --out=<目录>` | 6/6 |
| ⑦ | 分页（页脚 / 翻页 / 钩子 / 安全阀 / P7 / P8） | `... -- --paging-test --out=<目录>` | 8/8 |
| ⑧ | 自检（覆盖窗四条硬指标 / Raw Input / 内存） | `... -- --selftest` | 8/8 |
| ⑨ | 输入冒烟（进程内打字，跨进程待人工复核） | `... -- --typing-test --seconds=60` | 见输出 |

全部以退出码表示失败数（0 = 全部通过）。上面 `...` 指 `dotnet run --project src/HotkeyHud.App`。

**连跑约定**：抢前台的套件（②④⑤⑦）启动前会等前台连续 3 次采样不变（最多 1 秒），
单个用例失败会自动重试一次并在报告里标「（重试 1 次）」。**不用固定 Sleep**——慢机器上照样会红。

`--selftest` / `--integration-test` / `--render-gallery` / `--suppression-test` / `--typing-test` /
`--paging-test` / `--s6-test` / `--shell-test` / `--diag` / `--screen-shot` / `--adapter-check` /
`--template-preview` / `--settings-demo` / `--demo` 只在 Debug 构建存在，Release 下传入会以退出码 2
报错；未知参数与空格分隔的 `--capture path` 同样报错退出，不会静默忽略。

附：诊断入口（不属于门禁）

```bash
... -- --hook-probe                 # 钩子前提实验：吞掉后 Raw Input 是否仍收到
... -- --diag                       # 判定链逐条落盘 %TEMP%\hotkeyhud-show.log
... -- --adapter-check              # 用真实 %APPDATA% 跑 VS Code / JetBrains 适配器
... -- --settings-demo              # 设置页自检（打印内存增量，10 秒自动关）
... -- --make-icon --out=<目录>      # 生成多尺寸图标
... -- --screen-shot=%TEMP%\a.png    # 整屏截图（PerMonitorV2，物理像素）
```

**`--integration-test` / `--suppression-test` / `--shell-test` 会抢前台**：它们自己创建窗口并 `SetForegroundWindow`，
运行期间不要操作电脑，否则用例会以「无法把目标窗口切到前台」失败。

### 一条明确的「不干扰」例外

S1 立下的规矩是「不监听、不拦截、不抢焦点」。S7 起有**一处刻意违反**：

> **多页面板可见期间**，装 `WH_KEYBOARD_LL` 吞掉 `↑` `↓`，让它们翻页而不是传给前台应用。

范围被压到最小：只在 `PageCount > 1` 且面板可见时装；单页完全不装；收起 / 15 秒超时 / 进程退出
都会立刻卸钩。钩子回调里只做一次整数比较与一次 `PostMessage`，不做 I/O、不渲染。

**为什么必须有这个例外**：不吞的话，Ctrl+↓ 会一边翻页一边把前台文档的光标往下移。

**为什么钩子只吞不翻页**：实测（`--hook-probe`）证明**被 `WH_KEYBOARD_LL` 吞掉的键，Raw Input 收不到**。
所以翻页命令由钩子自己 `PostMessage` 投递，UI 收到后调 `TriggerStateMachine.RequestPage`——
两条通路（Raw Input / 钩子）共用同一个出口，不会各翻一次。

### 时钟唯一来源

`RawInputSource.NowMs`。状态机的 `KeyEvent.TimestampMs` 与 `OnTick(nowMs)` 都取自它内部的
同一个 `Stopwatch`，`HudApp` 里不出现 `Environment.TickCount64` / `DateTime` / 第二个 Stopwatch。
混用会让 `nowMs - _armedAtMs` 恒为极大值，**第一个 tick 就弹面板**，而 15 条单元测试抓不到
（它们用自造的同源时间）。这一条由集成测试 Case01 兜底：把 `OnTick` 换成 `Environment.TickCount64`
后 Case01 立即变红。

### 修饰键自动重复：实测结论

`--selftest` 会按住 F24 1200ms 并统计 Raw Input 按下次数：

```
[INFO] 按住 F24 1200ms 期间收到 1 次按下 —— 系统自动重复在注入路径下不存在，守卫按防御性代码保留
```

即**注入路径下不产生自动重复**，状态机里的重复守卫目前是无害的防御性代码。物理键盘可能不同
（重复由键盘类驱动生成），所以守卫保留。将来换输入源时重跑这条 INFO 即可判断它是否开始起作用。

### 应用档案核对表

每一条键位都必须能追溯到官方文档。**禁止凭印象写键位**——写错的键位比没有更糟，
用户会照着按然后发现没反应。

| 应用 | 文档 URL | 核对日期 | 核对人 | 条数 |
|---|---|---|---|---|
| Word | https://support.microsoft.com/en-us/office/keyboard-shortcuts-in-word-95ef89dd-7142-4b50-afb2-f762f663ceb2 | 2026-11-20 | 执行 Agent | 26 |
| Chrome | https://support.google.com/chrome/answer/157179?hl=en&co=GENIE.Platform%3DDesktop | 2026-11-20 | 执行 Agent | 21 |
| Edge | https://support.microsoft.com/zh-cn/microsoft-edge/microsoft-edge-键盘快捷方式-d356f99b-7fb4-7a30-f837-c5a35fd7cd1d | 2026-11-20 | 执行 Agent | 28 |
| VS Code | https://code.visualstudio.com/docs/reference/default-keybindings | 2026-11-20 | 执行 Agent | 27 |
| 文件资源管理器 | https://support.microsoft.com/en-us/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec | 2026-11-20 | 执行 Agent | 17 |
| 记事本 | 同上页面的「Text editing shortcuts」章节（微软未提供记事本专属快捷键页） | 2026-11-20 | 执行 Agent | 12 |
| Windows（`_system.json`） | 同上页面 + 「Text editing shortcuts」章节 | 2026-11-20 | 执行 Agent | 64 |

**未收录：微信 / QQ / 网易云音乐 / 豆包。** 这四家都没有公开的快捷键文档页——腾讯客服站
（`kf.qq.com`）只有聊天记录、登录、账号类条目，网易云与豆包同样没有官方键位表；网上能找到的
全是第三方教程（百度经验/知乎/下载站），按「禁止凭印象写键位」的规矩不能作为来源。

应用内 `设置 → 快捷键` 也逐个试过（S7 返工第二轮，Computer Use）：

| 应用 | 进程名 | 卡在哪 |
|---|---|---|
| 微信 | `Weixin.exe` | 启动后停在「进入微信」登录确认页；页面右上角齿轮只打开**网络代理设置**，快捷键页在登录后的主界面里 |
| QQ | `QQ.exe` | 未运行，启动即要求登录 |
| 网易云音乐 | `cloudmusic.exe` | 未登录态下主界面右上角只有 消息 / 勋章 / 主题 / 窗口按钮，左侧无菜单；`设置` 入口不可达（误触进了迷你播放器） |
| 豆包 | `Doubao.exe` | 未找到快捷键设置页 |

四家都**没有抄到一条**，所以没有新增档案、`docs/evidence/` 也没有截图——按「截图里没有的一条都不加」，
这比编一份看起来完整的档案更安全。要补的话，最省事的路径是登录后截一张 `设置 → 快捷键` 的图给我。

核对方式与已知限制：

- **Word / VS Code / 文件资源管理器**：官方页表格直接可读，逐条对照录入。
- **Chrome**：官方页表格由前端脚本折叠渲染，需带 `#zippy=` 锚点才能取到内容；已取到
  「Tab & window」「Feature」「Address bar」「Webpage」四张表并逐条对照。
- **记事本**：微软已不再维护「Keyboard shortcuts in apps」独立页（原链接跳回 Windows 总览页），
  因此采用该总览页中明确标注适用于文本编辑器的「Text editing shortcuts」表。这些组合在记事本里
  确实生效，但它们**不是记事本专属**，`Ctrl+G 定位行`、`Ctrl+E 查找` 等 Win11 新增项未收录。
- **§3.2 v1.5 起匹配规则是「包含」**：`Ctrl+Shift+X` 这类双修饰键条目会出现在 Ctrl 面板里，
  键帽只显示去掉触发修饰键后的部分（`Ctrl+Shift+K` → `Shift+K`）。因此档案收录完整组合，不再只收单修饰键。
- **Edge**：官方页给的是 Windows/Mac 合并表，只取 Windows 列；`Ctrl + 加号` 在档案里写作 `Ctrl+=`
  （键位解析器只认字符键，`Plus` 不是合法键名）。
- **系统档案 `_system.json` 用 `processNames: ["*"]` 通配**，不计入 `ProfileCount`；
  应用档案已有同一组合时按组合去重，不会出现两个同名键帽。

### 设置页（S7.6）

托盘 →「设置…」。WinForms 单窗体、手写布局、零 NuGet、非模态、关闭即 `Dispose`。

| 区域 | 内容 |
|---|---|
| 左 | 应用列表：`名称 条数 · 内置/我的`；底部「添加应用…」 |
| 右 | 条目表：键位 / 动作 / 颜色；底部编辑行 + `+ 添加` / `删除` |
| 底 | 提示 + `保存` / `关闭` |

三条交互：

- **内置只读**：保存时整份写 `%APPDATA%\HotkeyHud\apps\<app>.json`，安装目录的文件一个字节都不动。
- **按键录入**：点键位框进入录入态，直接按 `Ctrl+Alt+F` 自动填入（`Esc` 取消）；只按修饰键不产出组合。
- **8 色预设**：默认 / 蓝 / 橙 / 绿 / 青 / 紫 / 粉 / 红。非预设值按默认色处理并记一条加载警告。
  颜色只染键帽描边与键帽文字，动作文案保持主色。

内存：实测打开增量约 **2.4–2.9 MB**（`--settings-demo` 打印 before/after）。

### 用户自定义档案与自动读取（v1.8）

```
%APPDATA%\HotkeyHud\apps\
  _global.json     全局条目，所有应用可见，组名「自定义」
  notepad.json     应用级条目，与同名内置档案合并（文件名 = 进程名，不含 .exe）
```

| 规则 | 做法 |
|---|---|
| 顺序 | 内置先、用户后；同 `keys` **用户覆盖内置**，但保留内置里的位置（组顺序不被打乱） |
| 降权豁免 | 用户显式写的条目**不进「通用」组**——他主动写的就是他想要的 |
| 未收录应用 | `_global.json` 里有该修饰键的条目时不再显示引导空态（用户写的算内容） |
| 失败 | 单个文件解析失败只跳过它，原因进 `LoadErrors`，托盘气泡提示 |
| 进程名 | 托盘入口从选中的 exe 自动取，**不让用户手写** |

托盘「添加应用快捷键…」：选 exe → 试适配器 → 命中写用户档案并提示条数；未命中生成带注释的模板并打开。

**自动读取只对两家成立**（Windows 没有读任意应用快捷键的 API）：

| 适配器 | 读什么 | 产出 |
|---|---|---|
| VS Code（`Code.exe`） | `%APPDATA%\Code\User\keybindings.json` | 用户改过/新增的键位；多键序列与 `-command` 解绑跳过 |
| JetBrains（`idea64.exe` 等） | `%APPDATA%\JetBrains\<product>\keymaps\*.xml` | 只取 `first-keystroke`；带 `second-keystroke` 的两段式跳过 |

**只能读到用户改过的键位**——应用内置默认键位在安装目录的只读资源里，读不到。
所以完整视图 = 内置档案（默认键位）+ 用户档案（改过的）。
VS Code 从没改过键位时 `keybindings.json` 是空数组 → 适配器产出 0 条，**这是正确行为**。

### 排序、分页与「通用」组（v1.7）

| 规则 | 做法 | 为什么 |
|---|---|---|
| 排序 | **组优先**：按档案里的组顺序，组内按修饰键数量升序 | v1.5 的「全局按修饰键数」把同一组打散，Word 第 1 页出现 5 个组名 |
| 分页 | **按组对齐**：整组装得下就整组放，装不下就换页 | 「凑满 12 条」会横跨 3 个组，再被列宽切成「字体 / 字体（续）/ 段落 / 段落（续）」 |
| 通用组 | 命中熟记名单的组合**不剔除**，归入「通用」组，**强制另起一页**排最后 | 用户要求 Ctrl+C/V/Z 能看到；放首页又会把应用特有快捷键挤到第 2 页 |
| 单组页 | 整页只有一组时不标「（续）」 | 续列没有歧义，标了反而像三个组（Word 最后一页实测） |
| 列装填 | **顺序装列**（填满第 1 列再换列），不再「最短列优先」 | 最短列优先会把一组的续块塞回前面的空列，出现「段落（续）」排在「段落」左边的乱序 |

效果（Word，Ctrl，38 条）：第 1 页 `插入 / 字体 / 字体（续）` = **3 个组名**，共 5 页，最后一页是「通用」。

### 未收录应用的空态

`HudModel.HasProfile == false`（前台进程没有任何档案）时，面板**不再**拿系统档案里零星几条
Ctrl 条目冒充内容，而是给四行引导：

```
未收录 豆包.exe
这个应用还没有快捷键档案
· 按 Win 查看系统快捷键
· 在 Data/apps 添加档案（见 README）
```

例外：**Win 面板仍然显示系统快捷键**——那本来就是它的全部内容，引导语里「按 Win 查看」也才有落点。
已收录应用但该修饰键下没有条目时，仍显示原来那句「该应用没有 X 组合的快捷键数据」。

### 已知限制：不支持 Fn

**这是硬件限制，不是实现缺陷，明确不做。**

`Fn` 由键盘固件 / 嵌入式控制器在硬件层处理，它改变的是键盘上报的扫描码，本身**不产生任何按键事件**：

- Windows 没有 `Fn` 的虚拟键码
- `WH_KEYBOARD_LL` 收不到它，Raw Input 也收不到
- 微软自家的 PowerToys Keyboard Manager 同样不支持重映射 Fn
- `Fn+F1` 与 `F1` 在多数键盘上报同一个扫描码，无法区分

面板的触发条件是「检测到修饰键按下」，而 Fn 永远不产生这个信号，所以加进去也永远不显示——
**代码里不预留任何 Fn 占位**，预留只会让人以为将来能填。将来若再提这个需求，引用本节结论，
不要重新调研。

`Fn` 组合的实际效果会以标准媒体键到达（音量 `0xAF/0xAE/0xAD`、播放 `0xB3`、上下曲 `0xB1/0xB0`），
但这些是**单键**，不符合「长按修饰键触发面板」的模型，需要单独设计入口，不在当前范围。

### 诊断日志（`--diag`）

排查「某个应用里长按没反应」时用它：每条判定链写一行到 `%TEMP%\hotkeyhud-show.log`，
只观察、不改判定，**Release 构建不输出**（`Write` 标了 `[Conditional("DEBUG")]`，调用点整体消失）。

```
[17:53:03.925] stage=mod  | key=Ctrl | down=true | state=Idle | processName=WINWORD.EXE | held=None | blocked=false | suppressed=false | nowMs=8263
[17:53:04.351] stage=show | processName=WINWORD.EXE | profileHit=true | modifier=Ctrl | total=38 | pageCount=4 | suppressed=false | reason=- | armedAt=8263 | nowMs=8697 | elapsedSinceArm=434 | overlayRect=1020,1356,2100,1904
[17:53:05.140] stage=hide | processName=WINWORD.EXE | state=Idle
```

| 日志特征 | 结论 |
|---|---|
| 一行都没有（连 `stage=beat` 都没有） | 程序没跑，或没带 `--diag` |
| 有 `beat`、没有 `mod` | Raw Input 没收到键：前台进程完整性级别高于 HUD（看 `fg` 行的 `uipiBlocked`） |
| 有 `mod`、没有 `show` | 状态机没到阈值，或被 `blocked` / `suppressed` 挡住 |
| `profileHit=false` | 档案没匹配上（进程名不匹配） |
| `total=0` | 匹配上了但该修饰键下没有条目 |
| `overlayRect` 为空或在屏幕外 | 定位/尺寸算错 |

`stage` 取值：`start`（启动参数与自身完整性级别）、`beat`（60 秒心跳）、`fg`（前台切换，
会清空 held）、`mod`/`nonmod`（按键）、`after-key`（按键后的状态机快照）、`show`、`hide`。

### 实测结果（2026-11-12，Windows 10/11 x64，屏幕 3120×2080）

```
$ dotnet run --project tests/HotkeyHud.Tests
[PASS] Case01 按住 299ms 面板不出现
[PASS] Case02 按住 300ms 面板出现且标题为应用名
[PASS] Case03 按住 90ms 后松开，全程无事件
[PASS] Case04 Ctrl 后 60ms 按 C，面板不出现
[PASS] Case05 取消后全部松开再长按仍能弹出
[PASS] Case06 面板显示时按 Ctrl+C 立即收起且不高亮
[PASS] Case07 面板显示时按 Ctrl+K 高亮并 500ms 后收起
[PASS] Case08 熟记条目降权到「通用」组并排最后
[PASS] Case09 只显示当前应用条目
[PASS] Case10 未收录应用不混入其他条目
[PASS] Case11 双修饰键条目不出现在单修饰键面板
[PASS] Case12 独占全屏抑制
[PASS] Case13 发行平台目录抑制
[PASS] Case14 普通窗口不抑制
[PASS] Case15 前台窗口切换立即收起
--- 通过 15，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --integration-test
[PASS] Case01 按住 100ms 面板不出现 — State=Armed，可见=False
[PASS] Case02 按住 400ms 面板出现且标题正确 — label="HudTestApp · 2 条 Ctrl 快捷键"
[PASS] Case03 松开触发键面板消失 — 可见=False
[PASS] Case04 短按组合全程不弹面板 — Ctrl 累计按住 460ms，面板从未出现
[PASS] Case05 熟记组合与普通组合同样高亮（v1.7）— Ctrl+B / Ctrl+K 均高亮，500ms 后收起
[PASS] Case06 前台切换立即收起面板 — 切换后 200ms 内已收起
[PASS] Case07 未收录应用显示「未收录」 — label="未收录 HotkeyHud.App.exe"
[PASS] Case08 手动暂停时长按不弹面板 — 期间收到 1 个输入事件，面板从未出现
[PASS] Case09 空闲 10 秒 CPU 增量 < 100ms — CPU 增量 15.6 ms，计时器已停
[PASS] Case10 S1 窗口属性保持不变 — LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW 全置位
--- 通过 10，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --render-gallery --out=<目录>
[PASS] Gallery01 6 份档案可加载 — ProfileCount = 6，Dropped = 0，LoadErrors = 0（用户目录为空时不影响）
[PASS] Gallery02 正常态截图 — 01-normal.png 1080×604，10 条
[PASS] Gallery03 高亮态截图 — 02-highlight.png 1080×604，10 条
[PASS] Gallery04 空态截图 — 03-empty.png 1080×216，0 条
[PASS] Gallery05 长列表截断 — 04-long-list.png 1080×496，20 条，实画 12 条，高 248 逻辑 px，底部「还有 8 个未显示 · 松开 Ctrl 隐藏」
[PASS] Gallery06 高亮不改变面板尺寸 — 1080×444 连按 5 个组合，GetWindowRect 保持不变
[PASS] Gallery07 首帧 <25ms、高亮切换 <8ms — 首帧全量 p95 = 9.13 ms（< 25，32 次）；高亮切换 p95 = 1.06 ms（< 8，40 次）
--- 通过 7，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --s6-test --out=<目录>
[PASS] S6-1 混合 DPI：按光标挑屏与取缩放 — 副屏 150%：尺寸按 1.5 缩放、边距 60；上缘 y=60、下缘 y=968
[PASS] S6-2 上缘翻转 — 上半屏 → y=1408（下缘）；下半屏 → y=80（上缘）
[PASS] S6-3 淡入 120ms — 推了 7 帧（120ms / 16ms 一帧，ease-out）
[PASS] S6-4 关闭动画后无淡入 — 关闭动画后只推 1 帧（直接显示）
[PASS] S6-5 隐藏立即生效 — HideOverlay 后同步不可见，无淡出
[PASS] S6-6 本机显示器数量 — 1 块屏：\\.\DISPLAY1 3120×2080（主） —— **副屏截图未验证**
--- 通过 6，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --shell-test
[PASS] Shell01 托盘菜单 6 项可用 — 5 个可点击项 + 状态项；正常="正常 · 前台 chrome.exe"，暂停="已抑制（手动暂停） · 前台 chrome.exe"
[PASS] Shell02 暂停生效 — 暂停期间不弹，恢复后正常弹
[PASS] Shell03 暂停与游戏抑制互不覆盖 — 暂停 → 全屏 → 退出全屏后仍处于暂停，取消暂停后恢复
[PASS] Shell04 dwellMs 真的生效 — dwellMs=600：400ms 不弹、650ms 弹
[PASS] Shell05 knownShortcuts=[] 真的生效 — knownShortcuts=[] 后 Ctrl+C 出现在列表且会高亮
[PASS] Shell06 配置损坏不崩且留证 — 回落到默认值，原文件已改名为 config.broken.json
[PASS] Shell07 开机自启可逆 — 写入 "…\HotkeyHud.App.exe" → 删除后为空
[PASS] Shell08 单实例跨进程生效 — 子进程识别到已在运行并以退出码 3 退出
--- 通过 8，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --suppression-test --out=<目录>
[PASS] S4-1 普通窗口不抑制 — 面板正常弹出，GameSuppressed=False
[PASS] S4-2 无边框全屏抑制 — 面板未弹出；QUNS=2，命中原因="D1 系统报告独占全屏 / 演示模式"
[PASS] S4-3 切回普通窗口后恢复 — 退出全屏后抑制解除，面板正常弹出
[PASS] S4-4 D1 优先于 D5（关掉 D5 仍被抑制） — QUNS=2，原因="D1 …"；D5 在本机不会独立触发
[PASS] S4-5 单次采集 p95 < 5ms — p95 = 0.42 ms（含 OpenProcess，100 次）
[PASS] S4-6 空闲 10 秒 CPU 增量 < 100ms — CPU 增量 31.2 ms（含 2 秒轮询，共 5 次采集）
--- 通过 6，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --selftest
[PASS] 性能-空载内存三口径 — 私有工作集 5.9 MB / 提交大小 9.3 MB / 总工作集 35.2 MB
[PASS] S1-A1 Raw Input 通路 — F24 按下=True 松开=True，共收到 600 个事件
[PASS] S1-A2 输入回调不阻塞 — p50 = 0.039 ms（阈值 0.500），p99 = 0.555 ms（阈值 5.000）
[INFO] 按住 F24 1200ms 期间收到 1 次按下 —— 系统自动重复在注入路径下不存在，守卫按防御性代码保留
[PASS] S1-B 鼠标穿透 — 可见=True，WindowFromPoint(屏幕中心) = 0x204FA，覆盖窗 = 0x26076C
[PASS] S1-C 不抢焦点 — 显示前 = 0x40360，显示后 = 0x40360
[PASS] S1-D 不进 Alt+Tab / 任务栏（样式机制） — WS_EX_TOOLWINDOW=True WS_EX_APPWINDOW=False
[PASS] S1-E 覆盖窗几何 — 期望 (1350,950) 420×180，实际一致
[PASS] S1-F 可收起 — SW_HIDE 后 IsWindowVisible=False
--- 通过 8，失败 0 ---
```

| 指标 | 目标 | 实测 |
|---|---|---|
| 空载私有工作集 | — | 5.9 MB |
| 空载提交大小 | ≤ 45 MB | 9.3 MB |
| WM_INPUT 回调 p50 / p99 | — | 0.039 / 0.555 ms |
| 空闲 10s CPU 增量 | < 100 ms | 15.6 ms |
| 面板全量重绘 p95 | < 25 ms | 9.13 ms（32 次采样） |
| 面板高亮切换 p95 | < 8 ms | 1.06 ms（40 次采样） |
| 12 条面板高度 | ≤ `min(320, 屏高×30%)` | 248 逻辑 px（上限 298），列高 6/6/5 |
| 每页条目 | 12 | 12（多页时面板高度按格数预算固定，翻页不重建 DIB） |
| 翻页键吞噬 | 仅多页 + 仅可见 + 仅 ↑↓ | 见 `--paging-test` P4/P5：单页不装、多页装且收起即卸 |
| 淡入帧数 | 120ms / 16ms ≈ 8 帧 | 7 帧（ease-out）；关闭系统动画后 1 帧 |
| 单次抑制采集 p95 | < 5 ms | 0.42 ms（含 OpenProcess，100 次） |
| 按键到首帧 | ≤ 120 ms | 未打点（含 dwell 300ms + 首帧渲染） |
| 常驻句柄 | ≤ 500 | 未测（S5 托盘完成后测） |

面板在 200% DPI 下：Chrome 10 条是 1080×444 物理像素（540×222 逻辑），VS Code 12 条是
1080×496（540×248 逻辑）。高亮切换只重画高亮层：整帧重绘 9.1ms，每次按键都付这个代价会挤爆
8ms 预算，因此 `OverlayForm` 缓存了基图层（同一份模型引用 + 同尺寸 + 同 scale 时命中）。

`01-normal.png` 特意叠在一张白底黑字的铺满工作区的背景上截图——验收要求判定「浅色高对比背景下
背后文字是否可辨读」，桌面背景不可控，没法作为判据。

**变异测试**（确认断言不是恒真）：

| 变异 | 结果 |
|---|---|
| `ShouldFlash` 恒返回 true | 单元 Case06 FAIL |
| 删除「全部松开解除阻塞」 | 单元 Case05 FAIL |
| 长按判定 `>=` 改成 `>` | 单元 Case02 FAIL，Case01 仍 PASS |
| 删除「修饰键自动重复守卫」 | 单元 Case02 FAIL（面板被打掉） |
| 解除阻塞时不看非修饰键是否按住 | 单元 Case05 FAIL |
| 删除「前台切换清空非修饰键」 | 单元 Case15 FAIL（阻塞永不解除） |
| 删除「前台切换清空修饰键」 | 单元 Case16 FAIL |
| `OnTick` 改用 `Environment.TickCount64`（时钟不同源） | 集成 Case01 FAIL（100ms 就弹面板） |
| Armed 时按非修饰键不取消 | 单元 Case04 + 集成 Case04 同时 FAIL |
| 不接 `ComboFlashed` | 集成 Case05 FAIL（高亮组合不符） |
| `MaxVisibleItems` 改 99 | 画廊 Gallery05 FAIL（实画条数不符） |
| 高亮时把 bounds 撑宽 4px | 画廊 Gallery06 FAIL（GetWindowRect 1080→1088） |
| 打包行数 +1（模拟回到每组换行） | 画廊 Gallery05 FAIL（274 逻辑 px > 260） |
| 键帽不省略修饰键 | 画廊 Gallery05 FAIL（KeycapText 断言） |
| 只给第一组加组名（模拟无组名续列） | 画廊 Gallery05 FAIL（列首 导航 / (无组名) / (无组名)） |
| 三列高度差超 2 行（回到「填满第 1 列再换列」） | 画廊 Gallery05 FAIL |
| 配置里的 dwellMs 不接进状态机 | 外壳 Shell04 FAIL（400ms 就弹） |
| S1：`UpdateLayeredWindow` 尺寸改 300×300 | S1-E FAIL，退出码 1 |

> 注意：变异之间会互相掩盖。同时删掉「非修饰键取消」和「前台清空非修饰键」时，Case15 反而通过——
> 因为取消本身失效后阻塞根本没建立。判断某条断言是否有效，必须单独施加对应变异。

## 与规格的偏差

1. **验收①「记事本连续打字 5 分钟」被降级为 60 秒进程内 TextBox 注入测试**。理由：无人值守环境
   无法保证 5 分钟不被打断，跨进程读取记事本文本不可靠。**未覆盖跨进程真实键盘，仍需人工补测。**
2. **§3.3 默认方案 A 是「静默不弹」，本实现恒显示「未收录 X」一行**。理由：S2.5 任务书 §3.4
   明确要求该 label；`showEmptyState` 开关留到 S5 配置阶段再做，现阶段不做开关。
3. **集成测试 Case10 用「S1 窗口属性不变」代替「跑一遍 `--selftest`」**。理由：selftest 是独立
   进程入口，门禁里已单独执行（§五 第 3 条）；在集成测试里再起一个进程既慢又会抢 Raw Input。
   Case10 改为直接断言 `WS_EX_LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW` 仍全置位，
   防的是「S2.5 接线时顺手改坏 S1 属性」这类回归。
4. **`HotkeyHud.slnx` 而非 `.sln`**：.NET 10 SDK 的 `dotnet new sln` 默认生成 slnx 格式，功能等价。
5. **新增 `Diagnostics/` 目录**（§三 未列）：验收要求「贴出命令 + 输出」，没有可执行的自检程序
   就交不出证据。该目录整体包在 `#if DEBUG` 内，不进入 Release。
6. **面板视觉参数现已回写为规格 §6.1**：S3 时这些数字只存在于执行提示词里（圆角 14 / 键帽高 22 /
   3 列 / 12 条），违反「提示词不得引入新需求」，S3 验收时补录为 §6.1 并修正两处：底色 92%→96%、
   分组排布改为列内流式。本实现以 §6.1 为唯一来源。
7. **动作文案压到 6 个汉字以内**：3 列 × 540 逻辑宽下，每格扣掉键帽只剩约 6 个汉字的空间，
   更长的文案会被省略号截断。键帽改成只显示非修饰键后每格多出约 3 个汉字，但上限仍是 6。
8. **`HudApp.PanelBounds()` 更名为 `DebugPanelBounds()`**：真实面板已改由 `HudPanel.Measure` 按内容算；
   剩下的 420×180 硬编码只服务 S1 自检（它需要一个可预期的参照物来断言「窗口几何 = 请求的几何」）。
9. **高亮竖条画在命中格子的左缘，不是面板左缘**：3 列布局下一行有 3 个条目，画在面板边缘会让人
   分不清命中的是哪一个。行背景提亮仍按 §6.1 铺满整行。
10. **DPI 已改 PerMonitorV2**（S6）：面板按「光标所在屏」的 DPI 算尺寸、按该屏工作区定位；
    副屏截图未验证（本机单屏），定位/缩放用合成双屏做了纯函数断言。
11. **锚定在「显示时」定一次，只有跨屏才重锚**（S6 整改）：同屏内移动光标不再上下跳。
12. **布局 v1.3 用「最短列优先」，组的阅读顺序不再是从左到右**：VS Code 面板里「视图」会被填进
    第 1 列（因为那一刻它最短），于是列序是 导航+视图 / 编辑 / 编辑（续）+查找。
    这是「三列高度差 ≤ 2 行」的直接代价——想同时满足顺序与平衡，只能减少条目或加列。
13. **D1 在真机上先于 D5 命中，所以 D5 的开关被删掉**（§2.2 v1.3）：任何铺满屏幕的窗口都让
    `SHQueryUserNotificationState` 返回 2，D1 先命中，那个开关按下去没反应。
    **规则保留、开关删除**——暴露一个「按了没反应」的开关比没有开关更糟，
    而 D5 在 QUNS 不上报全屏的显示器/系统组合下仍可能独立生效。
14. **单实例只用 `Local\`**（S6 整改）：本工具是每用户每会话的——配置在 `%APPDATA%`、
    Raw Input 只收本会话输入、托盘图标属于本用户。快速用户切换时两人各应有一份 HUD，
    全局单实例反而是 bug；顺带省掉 `SeCreateGlobalPrivilege` 这条权限失败路径。
15. **配置越界改为钳制 + 气泡**（S6 整改）：`dwellMs` 越界只钳到边界并保留其它字段，
    气泡说明改成了什么。JSON 语法错 / 类型错 / 组合解析不了仍走损坏流程。
16. **未收录应用不再显示系统档案的零星条目**（S7 返工 §3.2）：Ctrl 面板只显示引导，
    不再用 1–3 条数据假装有内容。Win 面板例外，仍显示系统快捷键。见 [SPEC-GAP] 14。
17. **系统档案与应用档案按组合去重**：新增 10 条系统 Ctrl 条目后，Word 的 `Ctrl+Backspace`
    与系统档案同名条目会各占一格；`HudBuilder` 现在先收应用条目，再收系统里没有重复的组合。
18. **`_system.json` 不计入 `ProfileCount`**：它用 `processNames: ["*"]` 通配，单独收在
    `ShortcutRepository.SystemProfile`，所以画廊的「6 份档案」断言仍成立。
19. **不支持 Fn**：硬件限制，见「已知限制：不支持 Fn」，代码里不留占位。
20. **分页改为「按组对齐」，一页可能不足 12 条**（§3.2 v1.7 配套）：整组装不下就换页，
    Word 第 1 页只有 9 条而不是 12 条。换来的是「组名 ≤ 3」与「同组连续」——
    凑满 12 条必然把组切碎，两条要求在 12 条/页下互斥。
21. **列装填从「最短列优先」改回「顺序装列」**（S5 曾为平衡改成最短列）：最短列优先会把一组的
    续块塞回前面的空列，Word 实测出现「段落（续）」排在「段落」左边。顺序优先于平衡，
    列高差由统一容量保证。
22. **整页只有一组时不标「（续）」**：Word 最后一页（通用组）原本显示
    `通用 / 通用（续）/ 通用（续）` 三行标题。单组页的续列没有歧义，标了反而像三个组。
23. **微信 / QQ / 网易云 / 豆包 未收录**：无官方文档，应用内设置页也进不去（需登录），
    见「应用档案核对表」下的表格。**没有一条是凭印象补的。**
24. **桌面不算游戏**（S7 返工 2 的附带修复）：`Win+D` 之后前台是 `Progman`——铺满屏幕、无标题栏，
    D5 会把它当无边框全屏，于是在桌面上长按修饰键完全没反应。`GameClassifier` 现在先排除
    `Progman` / `WorkerW` / `Shell_TrayWnd` 三个外壳窗口类。Case19 锁定该行为，
    S1-B / S1-E 自检在「桌面在前台」这个状态下也恢复通过。
25. **用户档案文件名去掉 `.exe`**（`notepad.exe` → `notepad.json`）：与内置档案同名规则一致；
    仓库在用户档案没写 `processNames` 时也用文件名兜底。
26. **适配器读到 0 条时写空档案、不写示例**：VS Code 没改过键位就是 0 条，塞 3 条示例
    会变成面板里的假数据。空档案 + 气泡说明「改过键位后重载即可」。
27. **JetBrains 多产品目录取「最近写过」的那个**，并在气泡里写明选了哪个产品。
28. **设置页按 DPI 整体放大一次**（`OnLoad` 里 `Scale(DeviceDpi/96)`）：手写布局的尺寸是 96 DPI
    逻辑值，不放大时 200% 屏上窗口只有一半大、控件挤成一团（实测截图）。
29. **保存写「整份副本」而不是增量补丁**：用户一改就把合并后的整份写进用户档案，
    之后内置更新不会覆盖他的修改（这正是「我的档案」的语义）。
30. **P7 在本机只能验到「整条链路只前进一页」**：注入按键时低层键盘钩子不回调
    （同进程另装一个钩子也不回调，属环境限制）。钩子层的 `flags` 解析改由 Shell11
    用合成 `KBDLLHOOKSTRUCT` 锁定——变异验证过：去掉 `LLKHF_UP` 判断即 FAIL（通知 2 次）。
31. **多页时的窗口高度 = 格数预算 + 3 格装填余量**：顺序装列在一组卡到列尾时会浪费 1–2 格，
    不给余量的话同一模型的某页会多出一行（翻页时窗口高度变化，P2 实测 496→444）。
    余量只抬窗口高度，**内容按本页实际格数均衡铺三列**——否则内容少的页会整列空着。

非偏差，但需要记录的实现决定：

- **S1-D 验证的是窗口样式机制，不是 Alt+Tab 列表本身**。Alt+Tab 列表没有公开查询 API。
  真人按 Alt+Tab 确认仍属交付前手工步骤。
- **输入压力测试用 `KEYEVENTF_UNICODE` 注入**：用 VK 码注入字母时会被中文输入法吞掉
  （实测 2313 字符只到 587）。该注入不经过 Raw Input 通路，故回调耗时由 `--selftest`
  用 F24 独立测。
- **回调耗时用 p50 + p99 双判据**：单看 p99 会在机器繁忙时漂到 1.3 ms 造成误报。
- **F9 手动开关已删除**，不是「保留 + 互斥」：保留就得在触发前 `machine.Reset()`，
  两套状态机迟早打架，而它只在调试期有用。
- **集成测试要求独占前台**：它自己创建测试窗口并 `SetForegroundWindow`，抢不到就直接报
  「无法把目标窗口切到前台」而不是静默降级。每个用例开头都会先松开所有注入的键
  （`ReleaseAll`），避免上一个用例残留的按键状态污染下一个——这一点是实测出来的：
  不做的话约 1/5 概率出现「长按不弹」的假失败。
- **残留风险**：若系统层面真的丢了一次修饰键 key-up，`_heldModifiers` 会一直带着它，
  导致 `blockedUntilRelease` 永不解除。前台切换只清理非修饰键（S2 整改第 2 项的范围），
  修饰键没有对应兜底。Raw Input 走 `INPUTSINK`，key-up 丢失概率低，暂按已知限制记录。

## [SPEC-GAP]

| # | 位置 | 缺口 | 采用的最小解释 |
|---|---|---|---|
| 1 | §5.2 转移表 | `Flashing` 只列了「计时到点」与「非熟记组合按下」，缺「另一个修饰键按下 / 触发键松开 / 熟记组合按下」三行 | 一律按 `Visible` 同规则处理：收起 + 阻塞 |
| 2 | §5.2 | 「`Armed` + 另一个修饰键按下 → 取消」未说明是否阻塞，而「非修饰键按下」明确写了「+ 阻塞」 | 按字面实现：不阻塞；但入口挡住修饰键自动重复，避免「取消后又被重复事件重新 Armed」 |
| 3 | §3.1 | 要求解析失败的条目「静默丢弃并计数」，未定义计数暴露在哪 | 增加 `ShortcutRepository.DroppedShortcutCount` |
| 4 | §4.4 | 未定义 `knownShortcuts` 里有无法解析条目时的行为 | 抛 `ArgumentException`，配置错误必须显式失败 |
| 5 | §3.1 | 未定义同一进程名出现在多个档案时的行为 | 先加载者占用，后来者整份跳过并记入 `LoadErrors` |
| 6 | §3.1 | 未定义档案 JSON 损坏时的行为 | 跳过该档案并记入 `LoadErrors`，不让一份坏文件把程序拖垮；错误不静默 |
| 7 | §5.3.2 | 只给了 `DwellMs` 的范围 80–5000，未说越界怎么处理；`FlashMs` 干脆没给范围 | 两者都按 80–5000 校验，越界抛 `ArgumentOutOfRangeException`（构造与赋值都校验） |
| 8 | §6 | 原 §6 只有数据契约，没有任何视觉规格（圆角、底色、键帽、列数、条目上限） | S3 验收时已补录为 **§6.1 视觉规格（v1.1）**，本实现以它为唯一来源 |
| 9 | §6.1 列内流式 | 算法只说「组名占一格，填满当前列后换列」，没说组名恰好落在列底时怎么办 | v1.2 已改为「组块不可跨列」，此缺口关闭 |
| 10 | §6「UI 层不得自行过滤」 vs 12 条上限 | 「最多 12 条」必然要在某处截断，与「UI 不得过滤」字面冲突；`HudBuilder` 又被 S3 §2.3 禁止改动 | 截断放在渲染层 `HudPanel`，`HudModel.TotalCount` 保持完整，底部按 `TotalCount - 12` 显示「还有 N 个未显示」 |
| 11 | §6.1 面板高度门槛 | S3.5 的「≤260 逻辑 px」是拍脑袋写的（只是「12 条 4 行」的推算），与 v1.2 组块不可跨列互斥 | v1.3 已换成 `min(320, 屏高 × 30%)`，此缺口关闭；实测 248 px（上限 298） |
| 12 | §2.1 `GameRules.ExtraProcessNames` | S4 提示词写的是 `ExtraProcessNames`，但 S2 锁定的契约字段名是 `GameProcessNames`，且 S4 禁止改 `GameClassifier` | 保留 `GameProcessNames`，不新增同义字段 |
| 13 | §2.2 D5 与 D1 的关系 | 真机实测任何铺满屏幕的窗口都让 QUNS 报 2，D1 先命中，D5 不会独立触发 | v1.3 已删 `TreatBorderlessFullscreenAsGame`、D5 恒开，此缺口关闭 |
| 14 | §3.2「未收录应用 + 系统档案兜底」 vs S7 返工 §3.2「不要用 1 条数据假装有内容」 | 两条直接冲突：兜底必然给出零星条目 | 非 Win 修饰键一律走引导空态（`TotalCount=0`、`HasProfile=false`）；Win 面板仍显示系统档案，引导语「按 Win 查看系统快捷键」才有落点 |
| 15 | 需求「支持 Fn 快捷键」 | 物理上不可实现（Fn 不产生任何按键事件） | 明确不做，代码不留占位；README「已知限制」记录原因与替代方案 |

## 进度

| 阶段 | 状态 |
|---|---|
| S1 输入通路与「不干扰」 | 通过（含 5 项整改） |
| S2 四条规则 | 通过（15/15 用例，含 4 项整改） |
| S2.5 状态机接线 | 通过（集成 10/10，含 2 项整改） |
| S3 数据与渲染 | 通过（含 1 项整改） |
| S3.5 布局修正 | 通过（键帽省略修饰键 / 组块不可跨列 / 底色不透明） |
| S4 游戏抑制接入 | 通过（含 2 项整改） |
| S5 托盘 / 配置 / 自启 | 通过（含 2 项整改） |
| S6 打磨（混合 DPI / 上缘翻转 / 淡入） | 通过（含 2 项整改） |
| S7 覆盖度 + 分页 | 通过（含 1 项返工） |
| S7 返工（诊断日志 / 空态引导 / 系统档案 / Edge） | 通过（含 3 项返工） |
| S7 返工 2（组优先排序 / 通用组降权 / 分页按组对齐 / 桌面不算游戏） | 完成（4 个应用未抄录，见上表） |
| S7.5 用户档案 + 适配器 | 完成（用户档案实测生效；VS Code 适配器真机 2 条，JetBrains 本机无 keymap） |
| S7.6 修两个 bug + 设置页 | 完成（钩子 flags / 翻页不重算位置 / 设置页含按键录入与 8 色） |
| 发布准备（签名 / 安装包 / 用户 README） | 未开始 |
