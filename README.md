# HotkeyHud — 快捷键 HUD

长按修饰键（如 Ctrl）时在屏幕浮出当前应用的相关快捷键面板。需求源见
[`docs/shortcut-display-spec.md`](docs/shortcut-display-spec.md)，执行约束见
[`docs/executor-agent-prompt.md`](docs/executor-agent-prompt.md)。

## 技术栈与已知代价

| 层 | 选型 |
|---|---|
| 语言 / 运行时 | .NET 10（`net10.0` / `net10.0-windows`） |
| UI | WinForms + P/Invoke + GDI+ 分层窗口（`UpdateLayeredWindow`） |
| 核心逻辑 | `HotkeyHud.Core` 纯类库，零 UI 依赖 |
| 测试 | 控制台断言程序，不引测试框架 |
| 数据 | 每应用一份 JSON |

规格 §2.2 预估 `.NET + WinForms` 空载私有工作集 30–45 MB，产品目标是 ≤ 12 MB。本次实测
**提交大小 12.7 MB**（§八 的门槛口径），**总工作集 45.6 MB**——后者含 CLR / WinForms
程序集映射的共享页，不是私有口径，二者不可混用比较。

若内存成为硬指标，需换 C++/Rust + Direct2D 重做平台层与渲染层；规则层可照搬，
这就是 `HotkeyHud.Core` 禁止引用 `System.Windows.Forms` / `System.Drawing` 的原因。

## 目录

```
src/HotkeyHud.Core/           纯逻辑（S2 起填充）
src/HotkeyHud.App/
  Platform/NativeMethods.cs   全部 P/Invoke 声明
  Platform/RawInputSource.cs  Raw Input 采集（回调只解析+入队）
  UI/OverlayForm.cs           分层覆盖窗（穿透 / 不抢焦点 / 不进 Alt+Tab）
  Diagnostics/                自检与压力测试（非产品代码）
  HudApp.cs                   编排
  Program.cs                  入口 + 崩溃日志
tests/HotkeyHud.Tests/        控制台断言（S2 起填充）
```

## 构建与运行

```bash
dotnet build HotkeyHud.slnx

# 常驻：F9 手动显隐覆盖窗（仅 Debug 构建保留，S2 接入状态机后移除）
dotnet run --project src/HotkeyHud.App

# 显示 3 秒后自动退出；--capture 会把面板区域截图存盘
dotnet run --project src/HotkeyHud.App -- --demo --capture="%TEMP%\hud.png"
```

## 验证（S1 验收）

```bash
# ① 覆盖窗四条硬指标 + Raw Input 通路 + 回调耗时
dotnet run --project src/HotkeyHud.App -- --selftest

# ② 覆盖窗显示期间连续输入，验证不丢字、不抢焦点
dotnet run --project src/HotkeyHud.App -- --typing-test --seconds=60
```

两者均以退出码表示失败数（0 = 全部通过），输出同时写入
`%TEMP%\hotkeyhud-selftest.log` / `%TEMP%\hotkeyhud-typingtest.log`。

### 实测结果（2026-11-12，Windows 10/11 x64，屏幕 3120×2080）

```
[PASS] S1-A1 Raw Input 通路 — F24 按下=True 松开=True，共收到 600 个事件
[PASS] S1-A2 输入回调不阻塞 — p50 = 0.036 ms（阈值 0.500），p99 = 0.581 ms（阈值 5.000），最大 2.063 ms，样本 600 个
[PASS] S1-B 鼠标穿透 — 可见=True，WindowFromPoint(屏幕中心) = 0x204FA，覆盖窗 = 0x16806A2
[PASS] S1-C 不抢焦点 — 显示前 = 0x40360，显示后 = 0x40360
[PASS] S1-D 不进 Alt+Tab / 任务栏（样式机制） — WS_EX_TOOLWINDOW=True WS_EX_APPWINDOW=False，可见=True
[PASS] S1-E 覆盖窗几何 — 期望 (1350,950) 420×180，实际 (1350,950) 420×180
[PASS] S1-F 可收起 — SW_HIDE 后 IsWindowVisible=False
[PASS] 性能-空载提交大小 — 提交大小 12.7 MB（§八 目标 ≤ 45 MB）；总工作集 45.6 MB 含共享框架页
--- 通过 8，失败 0 ---

[PASS] S1-G 连续输入不丢字 — 注入 4979 字符 / 60.5s（注入失败 0 次），实际收到 4979 字符，逐字一致
[PASS] S1-H 覆盖窗显示期间焦点不变 — 目标窗 0x150296，覆盖窗前 0x150296，覆盖窗后 0x150296
--- 通过 2，失败 0 ---
```

| 指标 | 目标 | 实测 |
|---|---|---|
| 空载提交大小 | ≤ 45 MB | 12.7 MB |
| 总工作集（含共享页） | — | 45.6 MB |
| WM_INPUT 回调 p50 / p99 | — | 0.036 / 0.581 ms |
| 按键到首帧 | ≤ 120 ms | 未测（S2 面板渲染完成后打点） |
| 常驻句柄 | ≤ 500 | 未测（S5 托盘完成后测） |

`--selftest` 的 S1-E 做过变异测试：把 `UpdateLayeredWindow` 的尺寸改成 300×300 后该断言
报 FAIL 且退出码为 1，还原后恢复 PASS——确认它不是恒真断言。

## 与规格的偏差

真正偏离规格的地方：

1. **验收①「记事本连续打字 5 分钟」被降级为 60 秒进程内 TextBox 注入测试**。理由：无人值守环境
   无法保证 5 分钟内不被打断，且跨进程读取记事本文本不可靠。代价：未覆盖「其他进程」这一维度。
   5 分钟真人记事本测试仍需人工补做。
2. **`HotkeyHud.slnx` 而非 `.sln`**：.NET 10 SDK 的 `dotnet new sln` 默认生成 slnx 格式，功能等价。
3. **新增 `Diagnostics/` 目录**（§三 未列）：验收要求「贴出命令 + 输出」，没有可执行的自检程序
   就交不出证据。该目录不参与产品逻辑，不引用 Core。

非偏差，但需要记录的实现决定：

- **S1-D 验证的是窗口样式机制，不是 Alt+Tab 列表本身**。Alt+Tab 列表没有公开查询 API，
  能验证的是控制它的 `WS_EX_TOOLWINDOW` / `WS_EX_APPWINDOW` 组合。真人按 Alt+Tab 确认
  仍属交付前手工步骤。
- **输入压力测试用 `KEYEVENTF_UNICODE` 注入**：用 VK 码注入字母时会被中文输入法吞掉
  （实测 2313 字符只到 587）。该注入不经过 Raw Input 通路，故回调耗时由 `--selftest`
  用 F24 独立测。
- **回调耗时用 p50 + p99 双判据**：单看 p99 会在机器繁忙时漂到 1.3 ms 造成误报，
  单看 p50 又抓不到「偶发但阻塞」的路径。

## 进度

| 阶段 | 状态 |
|---|---|
| S1 输入通路与「不干扰」 | 完成（自动验证 10/10；5 分钟真人记事本打字 + Alt+Tab 目视待人工复核） |
| S2 四条规则 | 未开始 |
| S3 数据与渲染 | 未开始 |
| S4 游戏抑制接入 | 未开始 |
| S5 工程化 | 未开始 |
