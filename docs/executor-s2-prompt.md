<!--
用途：S2 开工指令。把本文件从「# 角色」开始整段交给执行 Agent。
前置：docs/shortcut-display-spec.md（唯一需求源）、docs/stage-gate.md（闸门协议）。
S1 结论：通过（有条件）——第一节的 5 项整改必须先做完再动 S2 功能。
-->

# 角色与本次任务

你是本项目的执行 Agent。S1 已验收通过，本次做 **S2：四条显示规则**。

只做规则，不做渲染，不接平台信号，不写数据档案。**S3 的活不要提前干。**

---

# 零、与既有规划的对应关系（先看这节，防止跑偏）

| S2 要做的 | 对应规格章节 | 说明 |
|---|---|---|
| 长按阈值状态机 | §5 全节 | 转移表在 §5.2，逐行实现 |
| 已牢记过滤 | §4 全节 | 两个作用点见 §4.3，别只做列表过滤 |
| 前台应用隔离 | §3 全节 | §3.2 的「单一入口」是硬约束 |
| 游戏判定（纯函数） | §2.1–§2.2 | 只写判定，不采信号（采信号是 S4） |
| 验收用例 | §7 的 1–11、15 | 12–14 的纯函数部分顺带写掉，真机验证留 S4 |

**映射不上的东西，一律视为跑偏，不要做。**

发现规格有矛盾或缺缺口：在交付里写 `[SPEC-GAP] <位置> <问题> <你采用的最小解释>`，**不要改规格原文，也不要自行扩需求**。

---

# 一、开工前置：S1 遗留整改（5 项，先做完再写 S2）

| # | 整改 | 位置 | 判定 |
|---|---|---|---|
| 1 | 内存口径改三列并列：私有工作集 / 提交大小 / 总工作集，删除「私有工作集 = 提交大小」的错误断言 | `src/HotkeyHud.App/Diagnostics/SelfTest.cs:133`、`README.md:17` | 实测值：私有工作集 5.2 MB、提交 8.6 MB、总工作集 32.6 MB（空载无面板） |
| 2 | 「60 秒连续输入压力测试」改为「进程内输入通路冒烟」，并保留「跨进程真实键盘待人工复核」 | `README.md` 验证章节 | 措辞不得暗示已覆盖跨进程 |
| 3 | 自检/注入/截图三个入口用 `#if DEBUG` 包住，Release 不发布 `--selftest` | `Program.cs`、`Diagnostics/` | `dotnet build -c Release` 后 exe 无自检入口 |
| 4 | `--capture` 只认 `--capture=path`，空格分隔被静默忽略 | `Program.cs:81` | 未知格式必须报错并返回非零，不许静默 |
| 5 | `tests/HotkeyHud.Tests` 目前是 1 行空壳 | `tests/Program.cs` | S2 的 15 条用例放这里，不放进产品程序集 |

第 1 项不是文字游戏：**S6 优化会拿这个数当基线**，口径错了后面全是错的。

---

# 二、S2 范围

## 要做

`src/HotkeyHud.Core/` 六个文件，纯 C#，**不引用 `System.Windows.Forms` / `System.Drawing`**：

| 文件 | 职责 | 规格 |
|---|---|---|
| `Keys.cs` | 修饰键分类、组合解析、VK 名称映射 | §3.2、§4 |
| `ShortcutModel.cs` | 条目 / 档案 / HUD 模型 | §6 |
| `KnownShortcutFilter.cs` | 熟记名单 | §4 |
| `ShortcutRepository.cs` | 按进程名索引 | §3.2 |
| `GameClassifier.cs` | 游戏判定纯函数 | §2.1–§2.2 |
| `HudBuilder.cs` | 查询 + 过滤 + 分组 | §3.2、§4.3 |
| `TriggerStateMachine.cs` | 状态机 | §5 |

`tests/HotkeyHud.Tests/` 15 条用例。

## 不要做

- 渲染、面板布局、配色（S3）
- 平台信号采集：`SHQueryUserNotificationState`、窗口矩形、进程路径（S4）
- `Data/apps/*.json` 真实档案（S3）
- 托盘、配置、自启（S5）
- 任何对 `HudApp` / `OverlayForm` / `RawInputSource` 的**功能改动**（只允许第一节的整改）

S2 结束时，`HudApp` 仍然是「F9 手动显隐」，不接状态机。接线是 S2.5，由下一份提示词决定。

---

# 三、接口契约（锁定，S3/S4 直接依赖）

字段名、参数顺序、事件名不得改动。要改先提 `[SPEC-GAP]`。

```csharp
public enum Modifier { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

public readonly record struct Combo(Modifier Modifiers, int VirtualKey);   // VirtualKey = 0 表示纯修饰键
public readonly record struct KeyEvent(int VirtualKey, bool IsDown, long TimestampMs);

public enum HudState { Idle, Armed, Visible, Flashing }

public sealed class TriggerStateMachine
{
    public TriggerStateMachine(int dwellMs = 300, int flashMs = 500);
    public int DwellMs { get; set; }
    public int FlashMs { get; set; }
    public HudState State { get; }
    public Modifier TriggerModifier { get; }
    public Combo? FlashedCombo { get; }
    public bool Suppressed { get; set; }                 // 置 true 立即收起
    public Func<Combo, bool>? FlashPredicate { get; set; }

    public event Action<Modifier>? ShowRequested;
    public event Action? HideRequested;
    public event Action<Combo>? ComboFlashed;

    public void OnKey(in KeyEvent e);
    public void OnTick(long nowMs);                      // 时钟外部注入，禁止内部计时
    public void OnMouseButton();
    public void OnForegroundChanged();
    public void Reset();
}
```

其余契约：

| 类型 | 成员 |
|---|---|
| `KnownShortcutFilter` | `ctor(IEnumerable<string>? combos = null)`、`bool IsKnown(Combo)`、`bool IsKnown(string)`、`int Count` |
| `ShortcutEntry` | `string Keys`、`string Action`、`string? Group`、`Combo Combo`、`bool IsValid` |
| `AppShortcutProfile` | `string App`、`string DisplayName`、`string[] ProcessNames`、`List<ShortcutEntry> Shortcuts` |
| `ShortcutRepository` | `ctor(string appsDirectory)`、`AppShortcutProfile? FindByProcess(string?)`、`void Reload()`、`int ProfileCount` |
| `HudModel` | `string AppName`、`Modifier Modifier`、`IReadOnlyList<HudGroup> Groups`、`int TotalCount`、`int HiddenByKnownCount`、`bool HasProfile` |
| `HudBuilder` | `static HudModel Build(AppShortcutProfile?, Modifier, KnownShortcutFilter, string fallbackAppName = "")` |
| `GameSignal` | `bool ExclusiveFullscreen`、`bool CoversWholeMonitor`、`bool HasCaption`、`string? ProcessName`、`string? WindowClass`、`string? ExecutablePath` |
| `GameClassifier` | `static bool ShouldSuppress(in GameSignal, GameRules?, out string reason)` |

**三条不可协商**：

1. 状态机不得持有 `Stopwatch` / `DateTime.Now` / `Timer`。时间只有一个来源：`OnTick(nowMs)` 与 `KeyEvent.TimestampMs`。
2. `FindByProcess` 是查询前台应用快捷键的**唯一入口**，不存在「查不到退回全局表」的路径。
3. `HudBuilder.Build` 只保留 `Combo.Modifiers == modifier` 的条目，`Ctrl+Shift+K` 不得出现在 `Ctrl` 面板。

---

# 四、验收用例（15 条，一条不能少）

| # | 规则 | 操作 | 期望 |
|---|---|---|---|
| 1 | §5 | Ctrl 按下，推进 299ms | 无 `ShowRequested` |
| 2 | §5 | Ctrl 按下，推进 300ms | `ShowRequested(Ctrl)` 一次 |
| 3 | §5 | Ctrl 按下 90ms 后松开 | 全程无 `ShowRequested`，无 `HideRequested` |
| 4 | §5 | Ctrl 按下，60ms 时按 C | 无 `ShowRequested` |
| 5 | §5 | 承接 #4，全部松开后重新长按 300ms | `ShowRequested` 一次（阻塞已解除） |
| 6 | §4.3 | Visible 时按 C（Ctrl+C 在名单内） | **立即** `HideRequested`，无 `ComboFlashed`，无 500ms 停留 |
| 7 | §4.3 | Visible 时按 K（Ctrl+K 不在名单） | `ComboFlashed(Ctrl+K)`，状态 Flashing，500ms 后 `HideRequested` |
| 8 | §4.3 | 档案含 Ctrl+C / Ctrl+S / Ctrl+K，构建 Ctrl 面板 | 列表只剩 Ctrl+K；`HiddenByKnownCount == 2` |
| 9 | §3.2 | 两个档案（Word / Chrome），查 Word | 只出现 Word 条目，不含 Chrome 任何条目 |
| 10 | §3.2 | 查未收录进程 | `HasProfile == false`、`TotalCount == 0`，不混入其他应用 |
| 11 | §3.2 | 档案含 Ctrl+Shift+=、Ctrl+Alt+M | Ctrl 面板中都不出现 |
| 12 | §2.2 | `ExclusiveFullscreen = true` | `ShouldSuppress == true` |
| 13 | §2.2 | `ExecutablePath` 含 `\steamapps\common\` | `ShouldSuppress == true` |
| 14 | §2.2 | 有标题栏、不覆盖整屏、路径在 Program Files | `ShouldSuppress == false` |
| 15 | §5 | Visible 时前台窗口变化 | 立即 `HideRequested` |

**用例 3、4、5、6 是回归重点**，对应三个最容易写错的路径：短按、取消后复发、已牢记不闪烁。

---

# 五、测试宿主与组织

| 项 | 规定 |
|---|---|
| 宿主 | `tests/HotkeyHud.Tests`，控制台程序，**不引任何 NuGet 包** |
| 结构 | 每个用例一个方法，命名 `Case01_DwellJustBelowThreshold()` … `Case15_ForegroundChangedHides()` |
| 时钟 | 状态机用例只用手工推进的 `long`，**禁止 `Thread.Sleep`** |
| 档案 | 用例 9–11 用内存构造的 `AppShortcutProfile`，不依赖 `Data/apps` |
| 仓库 | `ShortcutRepository` 用例写临时目录下的 JSON，测完删除 |
| 退出码 | `Main` 返回失败数，0 表示全过 |
| 输出 | 每行 `[PASS]/[FAIL] CaseNN 描述`，末尾 `--- 通过 N，失败 M ---` |

---

# 六、验收命令与判定

```bash
dotnet build HotkeyHud.slnx -warnaserror
dotnet run --project tests/HotkeyHud.Tests
dotnet run --project src/HotkeyHud.App -- --selftest
```

| 命令 | 判定 |
|---|---|
| build | 0 警告 0 错误（`-warnaserror` 下不通过即失败） |
| tests | exit 0，15 行 PASS |
| selftest | exit 0，S1 的 8 条仍全绿（**回归门禁**） |

**S1 回归不过，S2 一律不算完成。** 状态机没接进 App，理论上不该影响 S1；一旦影响，说明你动了不该动的地方。

---

# 七、禁止事项

1. 不改 `docs/shortcut-display-spec.md`。发现缺口写 `[SPEC-GAP]`。
2. **不改 300 / 500 这两个默认值来让用例通过**。阈值是产品决策，不是测试参数。
3. 不弱化断言。用例不过就修实现，不许改期望值、不许加 `|| true`。
4. 不提前写 S3 的渲染与数据档案、不提前写 S4 的平台采集。
5. `Core` 里不出现任何 `System.Windows.Forms` / `System.Drawing` 类型。
6. 不在生产代码里加「测试专用分支」。测试只通过公开接口驱动。
7. 不删改 S1 已验证的窗口属性（`WS_EX_LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW`、`SW_SHOWNOACTIVATE`）。

---

# 八、交付格式（四件套）

```
## 产物
- <文件路径>  新增/修改，一句话说明

## 变更说明
- 与规格不一致处：<逐条列出 + 理由>
- [SPEC-GAP]：<有则写，无则写「无」>

## 验证
$ dotnet run --project tests/HotkeyHud.Tests
[PASS] Case01 ...
--- 通过 15，失败 0 ---
$ dotnet run --project src/HotkeyHud.App -- --selftest
--- 通过 8，失败 0 ---

## 下一步
请给出 S3 提示词（或 S2 返工提示词）。
```

最后一行是固定动作：**无论验收结果如何，交付时都要主动申请下一阶段提示词**，由闸门协议决定给哪一种。

---

# 九、开工顺序

1. 先做第一节 5 项整改，跑一遍 `--selftest` 确认 8/8 未退化。
2. 把 15 条用例写进 `tests/`，**此时应全红**（Core 还是空的）。
3. 按 `tdd` skill 逐条点亮：先 `Keys` / `Combo`，再 `KnownShortcutFilter`，再 `TriggerStateMachine`，最后 `HudBuilder` + `GameClassifier`。
4. 全绿后跑 S1 回归。
5. 按第八节交付。

**不要先写实现再补测试。** 15 条用例是这次唯一的验收凭据。
