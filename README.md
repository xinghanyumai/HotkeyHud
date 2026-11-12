# HotkeyHud — 快捷键 HUD

长按修饰键（如 Ctrl）时在屏幕浮出当前应用的相关快捷键面板。需求源见
[`docs/shortcut-display-spec.md`](docs/shortcut-display-spec.md)，执行约束见
[`docs/executor-agent-prompt.md`](docs/executor-agent-prompt.md) 与各阶段提示词。

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
  Platform/RawInputSource.cs  Raw Input 采集（回调只解析+入队）
  UI/OverlayForm.cs           分层覆盖窗（穿透 / 不抢焦点 / 不进 Alt+Tab）
  Diagnostics/                自检与压力测试，仅 Debug 构建编译
  HudApp.cs                   编排
  Program.cs                  入口 + 严格参数解析 + 崩溃日志
tests/HotkeyHud.Tests/        §7 的 15 条验收用例
```

## 构建与运行

```bash
dotnet build HotkeyHud.slnx -warnaserror

# 常驻：F9 手动显隐覆盖窗（仅 Debug 构建保留，S2.5 接入状态机后移除）
dotnet run --project src/HotkeyHud.App
```

## 验证

```bash
# ① §7 的 15 条规则用例（唯一验收凭据）
dotnet run --project tests/HotkeyHud.Tests

# ② S1 回归门禁：覆盖窗四条硬指标 + Raw Input 通路 + 内存三口径
dotnet run --project src/HotkeyHud.App -- --selftest

# ③ 进程内输入通路冒烟（跨进程真实键盘打字待人工复核，见「偏差」）
dotnet run --project src/HotkeyHud.App -- --typing-test --seconds=60
```

全部以退出码表示失败数（0 = 全部通过）。`--selftest` / `--typing-test` / `--demo` 只在
Debug 构建存在，Release 下传入会以退出码 2 报错；未知参数与空格分隔的 `--capture path`
同样报错退出，不会静默忽略。

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
[PASS] Case08 熟记名单同时作用于列表与计数
[PASS] Case09 只显示当前应用条目
[PASS] Case10 未收录应用不混入其他条目
[PASS] Case11 双修饰键条目不出现在单修饰键面板
[PASS] Case12 独占全屏抑制
[PASS] Case13 发行平台目录抑制
[PASS] Case14 普通窗口不抑制
[PASS] Case15 前台窗口切换立即收起
--- 通过 15，失败 0 ---

$ dotnet run --project src/HotkeyHud.App -- --selftest
[PASS] 性能-空载内存三口径 — 私有工作集 5.3 MB / 提交大小 8.7 MB / 总工作集 33.6 MB
[PASS] S1-A1 Raw Input 通路 — F24 按下=True 松开=True，共收到 604 个事件
[PASS] S1-A2 输入回调不阻塞 — p50 = 0.018 ms（阈值 0.500），p99 = 0.472 ms（阈值 5.000）
[PASS] S1-B 鼠标穿透 — 可见=True，WindowFromPoint(屏幕中心) = 0x20720，覆盖窗 = 0x20786
[PASS] S1-C 不抢焦点 — 显示前 = 0x1310700，显示后 = 0x1310700
[PASS] S1-D 不进 Alt+Tab / 任务栏（样式机制） — WS_EX_TOOLWINDOW=True WS_EX_APPWINDOW=False
[PASS] S1-E 覆盖窗几何 — 期望 (1350,950) 420×180，实际一致
[PASS] S1-F 可收起 — SW_HIDE 后 IsWindowVisible=False
--- 通过 8，失败 0 ---
```

| 指标 | 目标 | 实测 |
|---|---|---|
| 空载私有工作集 | — | 5.3 MB |
| 空载提交大小 | ≤ 45 MB | 8.7 MB |
| WM_INPUT 回调 p50 / p99 | — | 0.018 / 0.472 ms |
| 按键到首帧 | ≤ 120 ms | 未测（S2.5 接线后打点） |
| 常驻句柄 | ≤ 500 | 未测（S5 托盘完成后测） |

**变异测试**（确认断言不是恒真）：

| 变异 | 结果 |
|---|---|
| `ShouldFlash` 恒返回 true | Case06 FAIL |
| 删除「全部松开解除阻塞」 | Case05 FAIL |
| 长按判定 `>=` 改成 `>` | Case02 FAIL，Case01 仍 PASS |
| S1：`UpdateLayeredWindow` 尺寸改 300×300 | S1-E FAIL，退出码 1 |

## 与规格的偏差

1. **验收①「记事本连续打字 5 分钟」被降级为 60 秒进程内 TextBox 注入测试**。理由：无人值守环境
   无法保证 5 分钟不被打断，跨进程读取记事本文本不可靠。**未覆盖跨进程真实键盘，仍需人工补测。**
2. **`HotkeyHud.slnx` 而非 `.sln`**：.NET 10 SDK 的 `dotnet new sln` 默认生成 slnx 格式，功能等价。
3. **新增 `Diagnostics/` 目录**（§三 未列）：验收要求「贴出命令 + 输出」，没有可执行的自检程序
   就交不出证据。该目录整体包在 `#if DEBUG` 内，不进入 Release。

非偏差，但需要记录的实现决定：

- **S1-D 验证的是窗口样式机制，不是 Alt+Tab 列表本身**。Alt+Tab 列表没有公开查询 API。
  真人按 Alt+Tab 确认仍属交付前手工步骤。
- **输入压力测试用 `KEYEVENTF_UNICODE` 注入**：用 VK 码注入字母时会被中文输入法吞掉
  （实测 2313 字符只到 587）。该注入不经过 Raw Input 通路，故回调耗时由 `--selftest`
  用 F24 独立测。
- **回调耗时用 p50 + p99 双判据**：单看 p99 会在机器繁忙时漂到 1.3 ms 造成误报。

## [SPEC-GAP]

| # | 位置 | 缺口 | 采用的最小解释 |
|---|---|---|---|
| 1 | §5.2 转移表 | `Flashing` 只列了「计时到点」与「非熟记组合按下」，缺「另一个修饰键按下 / 触发键松开 / 熟记组合按下」三行 | 一律按 `Visible` 同规则处理：收起 + 阻塞 |
| 2 | §5.2 | 「`Armed` + 另一个修饰键按下 → 取消」未说明是否阻塞，而「非修饰键按下」明确写了「+ 阻塞」 | 按字面实现：不阻塞 |
| 3 | §3.1 | 要求解析失败的条目「静默丢弃并计数」，未定义计数暴露在哪 | 增加 `ShortcutRepository.DroppedShortcutCount` |
| 4 | §4.4 | 未定义 `knownShortcuts` 里有无法解析条目时的行为 | 抛 `ArgumentException`，配置错误必须显式失败 |
| 5 | §3.1 | 未定义同一进程名出现在多个档案时的行为 | 抛 `InvalidDataException`，不静默择一 |

## 进度

| 阶段 | 状态 |
|---|---|
| S1 输入通路与「不干扰」 | 通过（含 5 项整改） |
| S2 四条规则 | 完成（15/15 用例通过，S1 回归 8/8） |
| S2.5 状态机接线 | 未开始（需下一份提示词） |
| S3 数据与渲染 | 未开始 |
| S4 游戏抑制接入 | 未开始 |
| S5 工程化 | 未开始 |
