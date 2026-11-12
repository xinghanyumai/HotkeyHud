<!--
用途：S2.5 开工指令（状态机接入 App）。整段交给执行 Agent。
前置：docs/shortcut-display-spec.md、docs/stage-gate.md。
S2 结论：通过（有条件）——第一节 4 项整改先做完。
-->

# 角色与本次任务

S2 已验收通过。本次做 **S2.5：把状态机接进 `HudApp`，替换 F9 手动开关**。

只做接线与时序。**不碰渲染，不接游戏判定，不做托盘。**

---

# 零、与既有规划的对应关系

| 本次要做 | 规格章节 |
|---|---|
| 状态机驱动显隐 | §5 全节 |
| 熟记名单接入（列表 + 闪烁两个作用点） | §4.3 |
| 前台应用查询 | §3.2 |
| 抑制开关（仅手动暂停） | §1 的 G1 |
| 游戏抑制 | **不做**，S4 |

游戏判定（§1 的 G2）本次**不接**。`Suppressed` 只有一个来源：用户手动暂停。

---

# 一、开工前置：S2 遗留整改（4 项）

| # | 整改 | 位置 | 判定 |
|---|---|---|---|
| 1 | `DwellMs` / `FlashMs` 增加范围校验（80–5000），越界抛 `ArgumentOutOfRangeException` | `Core/TriggerStateMachine.cs:28` | 设 0 或 10000 必须抛，不许静默接受 |
| 2 | 前台切换时清空 `_heldNonModifierKeys` | `Core/TriggerStateMachine.cs:127` | 漏掉 key-up 不会造成永久阻塞 |
| 3 | 确认「修饰键自动重复」是否真实存在 | `Diagnostics/SelfTest.cs` | 按住 F24 500ms，断言 Raw Input 事件数；结果写进 README |
| 4 | `README.md` 的 SPEC-GAP 章节补一条：`DwellMs` 越界的处理方式 | `README.md:149` | 与整改 1 一致 |

第 3 项的意义：若 Raw Input 不产生重复事件（大概率如此），那条 Critical 是误报，守卫是无害的死代码；若确实产生，守卫是必需的。**两种结论都要留证据**，否则将来换输入源时无法判断它是否还在起作用。

---

# 二、S2.5 范围

## 要做

| 文件 | 改动 |
|---|---|
| `Platform/RawInputSource.cs` | 暴露 `public long NowMs`（与 `TimestampMs` 同源） |
| `HudApp.cs` | 持有状态机 / 仓库 / 熟记名单 / 前台探针；事件接线；计时器按需启停 |
| `Program.cs` | 新增 `--integration-test`（走 `#if DEBUG`） |
| `Diagnostics/IntegrationTest.cs` | 验收证据生成器 |

## 不要做

- 不改 `OverlayForm` 的绘制（S3）。面板继续用现有的 `ShowNoActivate(bounds, color, label)` 显示一行调试文本。
- 不接 `GameClassifier` / `SHQueryUserNotificationState`（S4）。
- 不做托盘、配置、自启（S5）。
- 不改 `Core` 的任何规则（除第一节整改 1、2）。

---

# 三、接线契约（这一节决定成败）

## 3.1 时钟必须同源（最容易踩，且单测抓不到）

**现状**：`RawInputSource` 的 `TimestampMs` 来自它私有的 `Stopwatch`，从对象构造时开始计时，数值从 0 起。
**陷阱**：若 `OnTick` 传 `Environment.TickCount64`（系统开机毫秒，数值百万级），
则 `nowMs - _armedAtMs` 恒为极大值 → **第一个 tick 就弹面板**，300ms 规则完全失效。

**15 条单元测试抓不到这个问题**，因为它们用的是自己构造的同源时间。

**要求**：
- `RawInputSource` 增加 `public long NowMs => ElapsedMs();`
- `HudApp` 的 tick 回调一律用 `_input.NowMs`，**禁止**出现 `Environment.TickCount64` / `DateTime` / 第二个 `Stopwatch`
- 交付时在 README 记一行：时钟唯一来源 = `RawInputSource.NowMs`

## 3.2 计时器按需启停

```
Interval = 20ms
State ∈ {Armed, Flashing} → Start
State == Idle             → Stop
```

每次 `OnKey` / `OnMouseButton` / `OnTick` 之后同步一次启停状态。
**空闲时计时器必须是停的**，否则空载 CPU 永远不为 0。

## 3.3 事件接线

| 来源 | 调用 |
|---|---|
| `RawInputSource.Input`，`Kind == Key` | `machine.OnKey(new KeyEvent(code, isDown, evt.TimestampMs))` |
| `RawInputSource.Input`，鼠标键/滚轮 | `machine.OnMouseButton()` |
| `ForegroundWatcher.ForegroundChanged` | `probe.Invalidate(); machine.OnForegroundChanged();` |
| 计时器 Tick | `machine.OnTick(_input.NowMs)` |

## 3.4 显示与高亮

```
ShowRequested(mod)
  → Suppressed 为真则直接返回（不发任何 UI 调用）
  → profile = repo.FindByProcess(前台进程名)
  → model   = HudBuilder.Build(profile, mod, known)
  → label   = model.HasProfile
                ? $"{model.AppName} · {model.TotalCount} 条 {mod} 快捷键"
                : $"未收录 {前台进程名}"
  → overlay.ShowNoActivate(PanelBounds(), 按修饰键取色, label)

ComboFlashed(combo)
  → 把 label 换成 $"{combo} · {对应 Action}"（S3 换成真正的高亮渲染）
  → 重新 ShowNoActivate（同一位置，内容变了）

HideRequested → overlay.HideOverlay()
```

配色沿用规格 §6：Ctrl 蓝 `#4C8DFF`、Alt 橙 `#FF9F45`、Shift 绿 `#3DDC97`、Win 青 `#4CC9F0`。

## 3.5 F9 的处理

删除。若坚持保留，必须 `#if DEBUG` **且**在触发前调用 `machine.Reset()`，否则会和状态机抢状态。
交付时明确写：F9 已移除 / 保留但互斥。

---

# 四、验收用例（10 条）

`--integration-test` 用 `keybd_event` 注入真实按键（走 Raw Input），逐条断言。

| # | 操作 | 期望 |
|---|---|---|
| 1 | 注入 Ctrl 按下，等 100ms | 面板未出现 |
| 2 | 继续等到 350ms | 面板出现，label 含应用名与「条」 |
| 3 | 注入 Ctrl 松开 | 面板消失 |
| 4 | Ctrl 按下，60ms 后注入 C，再松开 | 全程面板未出现 |
| 5 | 面板显示时注入 C（Ctrl+C 在熟记名单） | 面板**立即**消失 |
| 6 | 面板显示时激活另一个窗口 | 面板立即消失 |
| 7 | 前台切到未收录进程后长按 | label 为「未收录 xxx」 |
| 8 | 打开手动暂停后长按 | 面板不出现 |
| 9 | 空闲 10 秒 | 进程 CPU 时间增量 < 100ms |
| 10 | S1 回归 | `--selftest` 8/8 |

**用例 1、2、4、5 是回归重点**：
- 1 和 2 一起才能证明 300ms 阈值真的生效（只测 2 会被「立刻弹」蒙混过关）
- 4 证明短按不弹
- 5 证明熟记名单作用于闪烁路径，而不是只过滤了列表

---

# 五、验收命令

```bash
dotnet build HotkeyHud.slnx -warnaserror
dotnet run --project tests/HotkeyHud.Tests          # 15/15，回归
dotnet run --project src/HotkeyHud.App -- --selftest        # 8/8，回归
dotnet run --project src/HotkeyHud.App -- --integration-test # 10/10
```

四项全绿才算完成。前两项是回归门禁，任何一项红了都不许交付。

---

# 六、禁止事项

1. 不为了过用例改 `DwellMs` / `FlashMs` 的默认值（300 / 500）。
2. 不用 `Thread.Sleep` 驱动状态机；等待一律靠 tick。
3. 不在 `ShowRequested` 里做阻塞操作（查档案、读文件）。仓库在启动时已 `Reload`，运行时只查内存。
4. 不给 `HudApp` 加「测试专用分支」；`--integration-test` 通过公开事件驱动。
5. 不改 S1 已验证的窗口属性与 `OverlayForm` 的绘制逻辑。
6. 不引入任何 NuGet 包。

---

# 七、交付格式

```
## 产物
- <文件>  改动说明

## 变更说明
- 时钟来源：<一句话说明同源方案>
- F9 处理：<移除 / 保留+互斥>
- 与规格不一致处：<逐条 + 理由，无则写「无」>

## 验证
$ dotnet run --project tests/HotkeyHud.Tests
--- 通过 15，失败 0 ---
$ dotnet run --project src/HotkeyHud.App -- --selftest
--- 通过 8，失败 0 ---
$ dotnet run --project src/HotkeyHud.App -- --integration-test
--- 通过 10，失败 0 ---

## 下一步
请给出 S3 提示词（或 S2.5 返工提示词）。
```

---

# 八、开工顺序

1. 先做第一节 4 项整改，跑 `tests` 与 `--selftest` 确认未退化。
2. 加 `RawInputSource.NowMs`，先写一条断言：`NowMs` 与 `InputEvent.TimestampMs` 同一 epoch（构造后 100ms 内 `NowMs` 应接近 100 而不是开机时长）。
3. 接状态机，只做显隐，不做高亮。
4. 补高亮与抑制开关。
5. 写 `--integration-test` 的 10 条。
6. 四项全绿后交付。

**第 2 步的断言不要省。** 它是唯一能在集成阶段抓住时钟不同源的东西。
