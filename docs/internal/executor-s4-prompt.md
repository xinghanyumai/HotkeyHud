<!--
用途：S4 开工指令（游戏 / 全屏抑制接入）。整段交给执行 Agent。
前置：docs/shortcut-display-spec.md §2、§6.1（v1.2）、docs/stage-gate.md。
S3.5 结论：通过（有条件）——第一节 2 项视觉整改先做完。
-->

# 角色与本次任务

S3.5 已验收通过。本次做 **S4：把游戏 / 全屏抑制接进 `HudApp`**。

`GameClassifier` 在 S2 已写完并测过（纯函数）。本次只做**信号采集 + 接线 + 真机验证**，不改判定规则。

---

# 零、与既有规划的对应关系

| 本次要做 | 规格章节 |
|---|---|
| 采集 6 个抑制信号 | §2.1 |
| 5 条有序判定接入 | §2.2 |
| 抑制状态变化 → 立即收起 | §5.2「任意 / 抑制置真」 |
| 手动暂停（G1） | **已有，S2.5 做的，不动** |

`GameClassifier` 的判定逻辑一行都不许改。要改先提 `[SPEC-GAP]`。

---

# 一、开工前置：S3.5 遗留（2 项视觉整改）

两项都只动 `UI/HudPanel.cs` 与 `§6.1`，**不碰平台层与 `HudApp`**——与 S4 主体文件不重叠，不会互相掩盖。

| # | 整改 | 判定 |
|---|---|---|
| 1 | 底色改**完全不透明** `#16181D`（去掉 alpha） | 在「深色底 + 亮字」背景上截图，面板内不可辨读任何背景内容 |
| 2 | 分组改**组块不可跨列** | 任意面板的每一列，顶部要么是组名、要么是上一列的延续；**不存在"无组名的续列"** |

## 1.1 第 2 项的具体规则

现在的实现只保证「组名不落单在列底」，不够。实测 VS Code 面板：

```
列1: 导航 G Home End | 编辑 D          ← 编辑只有 1 条在列 1
列2: L / [ ] Space M                  ← 顶部无组名，6 条被误读为「导航」
列3: 查找 H | 视图 1
```

改成**组块整体换列**（等价 CSS `break-inside: avoid`）：

```
若当前列剩余格数 < 组块高度（1 组名 + N 条目），整组移到下一列
单组高度 > 整列高度时才拆分，续列顶部加「组名（续）」
```

验收：VS Code 面板第 2 列顶部必须是某个组名。

## 1.2 为什么不用「加（续）标记」

你提的方案（续列重复组名）能解决问题，但每组跨列都多占一格，条目数一变就可能多出一行、突破 260px。
**组块不可跨列**从根上消除跨列，不增加格数，代价只是列内可能留白——而列内留白本来就有，不新增视觉负担。

---

# 二、S4 范围

## 2.1 采集 6 个信号

扩展 `Platform/ForegroundProbe.cs`（或新增 `Platform/SuppressionProbe.cs`）：

| 信号 | 采集方式 | 注意 |
|---|---|---|
| `ExclusiveFullscreen` | `SHQueryUserNotificationState` ∈ {2,3,4} | 2=忙、3=D3D 全屏、4=演示；**不要只判 3** |
| `CoversWholeMonitor` | `GetWindowRect` vs `MonitorFromWindow` + `GetMonitorInfo().rcMonitor`，四边容差 2px | |
| `HasCaption` | `GWL_STYLE & WS_CAPTION` | |
| `WindowClass` | `GetClassName` | |
| `ExecutablePath` / `ProcessName` | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName` | 提权窗口返回 0 → 空串，**不许抛** |
| 用户名单 | `GameRules.ExtraProcessNames` | S5 才接配置，本期用代码常量 |

## 2.2 接线

```
前台变化（SetWinEventHook） → 重新采集 → GameClassifier.ShouldSuppress → machine.Suppressed = 结果
每 2 秒轮询一次            → 同上（全屏状态可能不伴随前台切换）
```

**缓存**：按 `HWND` 缓存采集结果，前台没变就不重复 `OpenProcess`。
**禁止**在 `ShowRequested` 里采集——那是按键路径，必须零 I/O。

## 2.3 不要做

- 不改 `GameClassifier` 的判定规则
- 不改 `Core` 其他文件
- 不做托盘 / 配置 / 自启（S5）
- 不做「后台有游戏进程就抑制」（§2.3 已明确不做）
- 不动 `HudPanel` 的布局（除第一节整改）

---

# 三、验收用例（8 条）

| # | 项 | 判定 |
|---|---|---|
| 1 | 独占全屏抑制 | 打开全屏视频 / PPT 放映（`QUNS_PRESENTATION_MODE`），长按 Ctrl 不弹 |
| 2 | 无边框全屏抑制 | 全屏无标题栏窗口（如 F11 浏览器全屏），不弹 |
| 3 | 游戏库路径抑制 | 用 `GameRules` 单测覆盖（真机需装游戏，可用单元断言替代） |
| 4 | 普通窗口不抑制 | 记事本 / 资源管理器，正常弹 |
| 5 | 开关可关 | `TreatBorderlessFullscreenAsGame = false` 时，无边框全屏正常弹 |
| 6 | 抑制期间解除 | 抑制中长按不弹；切回普通窗口后长按能弹 |
| 7 | 轮询开销 | 2 秒一次，单次采集 < 5 ms；空闲 10 秒 CPU 增量仍 < 100 ms |
| 8 | 四项回归 | unit 16/16、selftest 8/8、integration 10/10、gallery 7/7 |

用例 1、2 需要真机操作，**必须给截图或录屏证据**，不接受「已验证」三个字。

---

# 四、验收命令

```bash
dotnet build HotkeyHud.slnx -warnaserror
dotnet run --project tests/HotkeyHud.Tests
dotnet run --project src/HotkeyHud.App -- --selftest
dotnet run --project src/HotkeyHud.App -- --integration-test
dotnet run --project src/HotkeyHud.App -- --render-gallery --out=<目录>
```

---

# 五、禁止事项

1. 不改 `GameClassifier` 的判定规则与阈值。
2. 不在按键路径上做进程查询或文件 I/O。
3. 不因 `OpenProcess` 失败而抛异常或写日志刷屏——提权窗口是常态。
4. 不把「抑制」实现成「隐藏窗口」——必须是状态机 `Suppressed`，否则状态会错位。
5. 不为过用例放宽判定（例如把 `ExclusiveFullscreen` 判据从 2/3/4 缩到只判 3）。

---

# 六、交付格式

```
## 产物
- <文件>  改动说明

## 截图对比（S3.5 两项整改）
- 改前 04-long-list.png / 改后
- 深色亮字背景下的不透明度验证图

## 真机验证（S4）
- 用例 1：<应用名> + 截图
- 用例 2：<应用名> + 截图

## 变更说明
- 采集时机与缓存策略
- 与规格 §2 的偏差：<无则写「无」>

## 验证
<五条命令输出>

## 下一步
请给出 S5 提示词（或 S4 返工提示词）。
```

---

# 七、开工顺序

1. 先做第一节 2 项视觉整改，出图确认（尤其 VS Code 面板第 2 列顶部要有组名）。
2. 再扩 `ForegroundProbe` 的 6 个信号，用单元测试覆盖纯逻辑部分。
3. 接 `HudApp` 的抑制计算，先只做前台变化触发，再加 2 秒轮询。
4. 真机跑用例 1、2，截图。
5. 跑满五项门禁。

**第 1 步和第 2 步之间要跑一次全量回归。** 视觉改动虽然只碰 `HudPanel`，但它与平台层共用 `OverlayForm` 的合成路径，早发现问题成本低。
