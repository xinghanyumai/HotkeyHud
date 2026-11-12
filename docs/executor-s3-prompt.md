<!--
用途：S3 开工指令（应用档案 + 面板真实渲染）。整段交给执行 Agent。
前置：docs/shortcut-display-spec.md、docs/stage-gate.md。
S2.5 结论：通过（有条件）——第一节 2 项整改先做完。
-->

# 角色与本次任务

S2.5 已验收通过。本次做 **S3：5 份应用档案 + 面板真正渲染**。

把 S2.5 的调试文本换成规格 §6 的面板。**不接游戏判定，不做托盘，不加动效。**

---

# 零、与既有规划的对应关系

| 本次要做 | 规格章节 |
|---|---|
| 面板视觉与布局 | §6 全节（圆角 14、底色 `#16181D` 92%、键帽高 22、3 列、最多 12 条） |
| 修饰键配色 | §6（Ctrl `#4C8DFF` / Alt `#FF9F45` / Shift `#3DDC97` / Win `#4CC9F0`） |
| 分组与「还有 N 个」 | §6 布局条 |
| 应用档案 | §3.1 数据模型 |
| 未收录 / 空态 | §3.3 方案 B（恒显示「未收录 X」，`showEmptyState` 开关留 S5） |

**面板位置**：本期只做「贴光标所在屏的下缘居中 + 40px 边距」。
「焦点在下半屏时自动翻到上缘」属于 §6 的增强项，**留 S6，本期不做**。

---

# 一、开工前置：S2.5 遗留（2 项）

| # | 整改 | 位置 | 判定 |
|---|---|---|---|
| 1 | `OnForegroundChanged` 同时清空 `_heldModifiers` | `Core/TriggerStateMachine.cs:127` | 修饰键 key-up 丢失后不会永久阻塞 |
| 2 | README 记一条：`--integration-test` 会抢前台，运行期间不要操作电脑 | `README.md` | 与 `FocusWindow` 的行为一致 |

第 1 项的理由：当前只有「前台切换清空非修饰键」，修饰键没有兜底。若系统真丢一次 Ctrl 的 key-up，`blockedUntilRelease` 永不解除，**HUD 彻底失效且无提示**。
前台切换时物理键状态本来就不可知，清空是正确语义，不是补丁。

补一条单元用例：`Case16 前台切换后修饰键状态被清空`——按住 Ctrl 进入 Armed → 前台切换 → 松开 Ctrl → 再长按仍能弹出。

---

# 二、S3 范围

## 2.1 应用档案（5 份）

`src/HotkeyHud.App/Data/apps/` 下：

| 文件 | 进程名 | 最少条数 |
|---|---|---|
| `winword.json` | `WINWORD.EXE` | 10 |
| `chrome.json` | `chrome.exe` | 10 |
| `code.json` | `Code.exe` | 12 |
| `explorer.json` | `explorer.exe` | 8 |
| `notepad.json` | `notepad.exe` | 6 |

**硬要求：每一条都必须能追溯到官方文档。** 在 `README.md` 建一张表：应用 / 文档 URL / 核对日期 / 核对人。
禁止凭印象写键位——写错的键位比没有更糟，用户会照着按然后发现没反应。

`DroppedShortcutCount` 必须为 0（所有 `keys` 都能解析）。

## 2.2 渲染

`OverlayForm` 新增 `public void ShowModel(HudModel model, Combo? highlight)`，实现规格 §6 的视觉。

| 区域 | 内容 |
|---|---|
| 头部 | 修饰键胶囊（配色见 §6）+ 应用名 + 右侧「松开隐藏」 |
| 分组 | 多于一组时显示组名（11px / 55% 白） |
| 条目 | 键帽（高 22、圆角 5、描边 `rgba(255,255,255,0.16)`）+ 效果文案（13px） |
| 高亮 | 左侧 2px 竖条 + 该行背景提亮 8% |
| 底部 | 分隔线 + 「还有 N 个未显示 · 松开 X 隐藏」 |
| 空态 | 「未收录 X」或「该应用没有 Ctrl 组合的快捷键数据」 |

保留现有 `ShowNoActivate(bounds, color, label)` 供 S1 自检使用，**不要删**——`--selftest` 依赖它。

## 2.3 不要做

- 游戏抑制（S4）
- 托盘、配置、自启（S5）
- 淡入动效、DPI 翻转定位、键位热力图（S6 / 非本期）
- 改 `Core` 的任何规则（除第一节整改）

---

# 三、三条硬约束（踩了会返工）

## 3.1 高亮不得改变面板尺寸

面板尺寸由**内容**决定，高亮只改颜色与背景，不改宽高。
否则每次按键都会重建 DIB → `UpdateLayeredWindow` 重新合成 → 肉眼可见的闪烁。

验收方式：连续按 5 个不同组合，断言 `GetWindowRect` 前后完全一致。

## 3.2 单次渲染 < 16ms

渲染在 UI 线程，超时会直接表现为输入卡顿。
在渲染前后打点，记录 p95；超过 16ms 在集成测试里报 FAIL，不是 WARN。

字体一律 `GraphicsUnit.Pixel`，**不要用 Point**——点值会随 DPI 二次缩放。

## 3.3 尺寸必须按内容算，位置按 DPI 算

现状 `PanelBounds()` 是硬编码 420×180（S1 遗留）。S3 要改成：

```
宽度 = min(540 * dpiScale, 工作区宽度 * 0.72)
高度 = 头部 + 分组 + 行数 * 26 + 底部 + 内边距
位置 = 光标所在屏工作区底部居中，边距 40 * dpiScale
```

100% 与 150% DPI 下都不能出现文字裁切或面板超出工作区。

---

# 四、验收用例（8 条）

| # | 项 | 判定 |
|---|---|---|
| 1 | 5 份档案可加载 | `ProfileCount == 5`、`DroppedShortcutCount == 0` |
| 2 | 正常态渲染 | 截图：圆角平滑、无黑边、文字不溢出、分组清晰 |
| 3 | 高亮态渲染 | 截图：目标行有竖条 + 背景提亮，其余行不变 |
| 4 | 空态渲染 | 截图：未收录应用显示「未收录 X」 |
| 5 | 长列表截断 | 12 条上限生效，底部显示「还有 N 个未显示」 |
| 6 | 高亮不改尺寸 | 连按 5 个组合，`GetWindowRect` 前后一致 |
| 7 | 渲染耗时 | p95 < 16ms（至少 30 次采样） |
| 8 | 三项回归 | unit 15/15（+新增 Case16）、selftest 8/8、integration 10/10 |

新增命令：`--render-gallery --out=<dir>`，一次性输出 4 张 PNG（正常/高亮/空态/长列表），供人工核验。

---

# 五、验收命令

```bash
dotnet build HotkeyHud.slnx -warnaserror
dotnet run --project tests/HotkeyHud.Tests
dotnet run --project src/HotkeyHud.App -- --selftest
dotnet run --project src/HotkeyHud.App -- --integration-test
dotnet run --project src/HotkeyHud.App -- --render-gallery --out=<目录>
```

五项全绿 + 4 张截图通过目视，才算完成。

---

# 六、禁止事项

1. 不凭印象写快捷键。每条必须来自官方文档，并记录 URL 与核对日期。
2. 不用 `GraphicsUnit.Point` 设字号。
3. 不在渲染路径里读文件、查仓库、做 I/O。
4. 不删 `ShowNoActivate`（S1 自检依赖）。
5. 不为「更好看」改动规格 §6 给定的色值与尺寸。
6. 不引入图片资源或图标字体——全部矢量绘制。

---

# 七、交付格式

```
## 产物
- <文件>  改动说明

## 档案核对表
| 应用 | 文档 URL | 核对日期 | 条数 |

## 变更说明
- 与规格 §6 的偏差：<逐条 + 理由，无则写「无」>
- [SPEC-GAP]：<有则写>

## 验证
$ dotnet run --project tests/HotkeyHud.Tests          → 16/16
$ dotnet run --project src/HotkeyHud.App -- --selftest          → 8/8
$ dotnet run --project src/HotkeyHud.App -- --integration-test  → 10/10
$ --render-gallery → 4 张 PNG 路径

## 下一步
请给出 S4 提示词（或 S3 返工提示词）。
```

---

# 八、开工顺序

1. 先做第一节 2 项整改 + Case16，跑三项回归确认未退化。
2. 写 5 份档案，跑用例 1，确认 `DroppedShortcutCount == 0`。
3. 实现 `ShowModel` 的布局计算（先只画头部和条目，不画高亮与底部）。
4. 补高亮、底部、空态。
5. 接 `--render-gallery`，出 4 张图自查。
6. 补用例 6、7，跑满五项。

**第 3 步先只画静态，再补状态。** 一次性写完布局+高亮+动效，出问题时无法判断是布局错了还是状态错了。
