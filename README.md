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
  Platform/RawInputSource.cs  Raw Input 采集；同时是全程序唯一的时钟来源（NowMs）
  Platform/ForegroundWatcher.cs  前台窗口变化监听（SetWinEventHook）
  Platform/ForegroundProbe.cs    前台进程名查询（缓存 + Invalidate）
  UI/OverlayForm.cs           分层覆盖窗（穿透 / 不抢焦点 / 不进 Alt+Tab）
  Diagnostics/                自检、集成测试、压力测试，仅 Debug 构建编译
  HudApp.cs                   编排：Raw Input → 状态机 → 覆盖窗
  Program.cs                  入口 + 严格参数解析 + 崩溃日志
tests/HotkeyHud.Tests/        §7 的 15 条规则用例
```

## 构建与运行

```bash
dotnet build HotkeyHud.slnx -warnaserror

# 常驻：长按 Ctrl / Alt / Shift / Win 弹出面板
dotnet run --project src/HotkeyHud.App
```

F9 手动开关已在 S2.5 移除——它会和状态机抢状态。

## 验证

```bash
# ① §7 的 15 条规则用例（唯一验收凭据）
dotnet run --project tests/HotkeyHud.Tests

# ② S2.5 集成验收：真实注入按键，走 Raw Input → 状态机 → 覆盖窗全链路
dotnet run --project src/HotkeyHud.App -- --integration-test

# ③ S1 回归门禁：覆盖窗四条硬指标 + Raw Input 通路 + 内存三口径
dotnet run --project src/HotkeyHud.App -- --selftest

# ④ 进程内输入通路冒烟（跨进程真实键盘打字待人工复核，见「偏差」）
dotnet run --project src/HotkeyHud.App -- --typing-test --seconds=60
```

全部以退出码表示失败数（0 = 全部通过）。`--selftest` / `--integration-test` / `--typing-test` /
`--demo` 只在 Debug 构建存在，Release 下传入会以退出码 2 报错；未知参数与空格分隔的
`--capture path` 同样报错退出，不会静默忽略。

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

$ dotnet run --project src/HotkeyHud.App -- --integration-test
[PASS] Case01 按住 100ms 面板不出现 — State=Armed，可见=False
[PASS] Case02 按住 400ms 面板出现且标题正确 — label="HudTestApp · 2 条 Ctrl 快捷键"
[PASS] Case03 松开触发键面板消失 — 可见=False
[PASS] Case04 短按组合全程不弹面板 — Ctrl 累计按住 460ms，面板从未出现
[PASS] Case05 熟记组合立即收起、非熟记组合高亮 — Ctrl+C 立即收起；Ctrl+K 高亮显示动作并 500ms 后收起
[PASS] Case06 前台切换立即收起面板 — 切换后 200ms 内已收起
[PASS] Case07 未收录应用显示「未收录」 — label="未收录 HotkeyHud.App.exe"
[PASS] Case08 手动暂停时长按不弹面板 — 期间收到 1 个输入事件，面板从未出现
[PASS] Case09 空闲 10 秒 CPU 增量 < 100ms — CPU 增量 15.6 ms，计时器已停
[PASS] Case10 S1 窗口属性保持不变 — LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW 全置位
--- 通过 10，失败 0 ---

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
| 按键到首帧 | ≤ 120 ms | 未打点（S3 面板渲染完成后测） |
| 常驻句柄 | ≤ 500 | 未测（S5 托盘完成后测） |

**变异测试**（确认断言不是恒真）：

| 变异 | 结果 |
|---|---|
| `ShouldFlash` 恒返回 true | 单元 Case06 FAIL |
| 删除「全部松开解除阻塞」 | 单元 Case05 FAIL |
| 长按判定 `>=` 改成 `>` | 单元 Case02 FAIL，Case01 仍 PASS |
| 删除「修饰键自动重复守卫」 | 单元 Case02 FAIL（面板被打掉） |
| 解除阻塞时不看非修饰键是否按住 | 单元 Case05 FAIL |
| 删除「前台切换清空非修饰键」 | 单元 Case15 FAIL（阻塞永不解除） |
| `OnTick` 改用 `Environment.TickCount64`（时钟不同源） | 集成 Case01 FAIL（100ms 就弹面板） |
| Armed 时按非修饰键不取消 | 单元 Case04 + 集成 Case04 同时 FAIL |
| 不接 `ComboFlashed` | 集成 Case05 FAIL（高亮 label 无动作文案） |
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

## 进度

| 阶段 | 状态 |
|---|---|
| S1 输入通路与「不干扰」 | 通过（含 5 项整改） |
| S2 四条规则 | 通过（15/15 用例，含 4 项整改） |
| S2.5 状态机接线 | 完成（集成 10/10，S1 回归 8/8） |
| S3 数据与渲染 | 未开始 |
| S4 游戏抑制接入 | 未开始 |
| S5 工程化 | 未开始 |
