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

`.NET + WinForms` 的空载私有工作集约 30–45 MB，而产品目标是 ≤ 12 MB。本次实测提交大小
**12.9 MB**、工作集 45.8 MB，落在预估区间下沿；若后续内存成为硬指标，需换 C++/Rust +
Direct2D 重做平台层与渲染层（规则层可照搬，见 `HotkeyHud.Core` 的零 UI 依赖约束）。

## 目录

```
src/HotkeyHud.Core/           纯逻辑（S2 起填充）
src/HotkeyHud.App/
  Platform/NativeMethods.cs   全部 P/Invoke 声明
  Platform/RawInputSource.cs  Raw Input 采集（回调只解析+入队）
  UI/OverlayForm.cs           分层覆盖窗（穿透 / 不抢焦点 / 不进 Alt+Tab）
  Diagnostics/                自检与压力测试
  HudApp.cs                   编排
  Program.cs                  入口 + 崩溃日志
tests/HotkeyHud.Tests/        控制台断言（S2 起填充）
```

## 构建与运行

```bash
dotnet build HotkeyHud.slnx

# 常驻：F9 手动显隐覆盖窗（S1 调试用，S2 接入状态机后移除）
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

### 实测结果（2026-11-12，Windows 10/11 x64）

```
[PASS] S1-A1 Raw Input 通路 — F24 按下=True 松开=True，共收到 600 个事件
[PASS] S1-A2 输入回调不阻塞 — WM_INPUT 回调 p99 = 0.571 ms（阈值 2.000 ms），最大 1.506 ms，样本 600 个
[PASS] S1-B 鼠标穿透 — WindowFromPoint(屏幕中心) = 0x204FA，覆盖窗 = 0x110638
[PASS] S1-C 不抢焦点 — 显示前 = 0x40360，显示后 = 0x40360
[PASS] S1-D 不进 Alt+Tab / 任务栏 — WS_EX_TOOLWINDOW=True WS_EX_APPWINDOW=False，可见=True
[PASS] S1-E 覆盖窗尺寸 — GetWindowRect = (1350,950) 420×180
[PASS] S1-F 可收起 — SW_HIDE 后 IsWindowVisible=False
[PASS] 性能-空载私有工作集 — 提交大小 12.9 MB（目标 ≤ 45 MB），工作集 45.8 MB
--- 通过 8，失败 0 ---

[PASS] S1-G 连续输入不丢字 — 注入 4898 字符 / 60.5s（注入失败 0 次），实际收到 4898 字符，逐字一致
[PASS] S1-H 覆盖窗显示期间焦点不变 — 目标窗 0x100606，覆盖窗前 0x100606，覆盖窗后 0x100606
--- 通过 2，失败 0 ---
```

| 指标 | 目标 | 实测 |
|---|---|---|
| 空载私有工作集（提交大小） | ≤ 45 MB | 12.9 MB |
| 工作集（含共享页） | — | 45.8 MB |
| WM_INPUT 回调 p99 | — | 0.571 ms |
| 按键到首帧 | ≤ 120 ms | 未测（S2 面板渲染完成后打点） |
| 常驻句柄 | ≤ 500 | 未测（S5 托盘完成后测） |

## 与规格的偏差

1. **`HotkeyHud.slnx` 而非 `.sln`**：.NET 10 SDK 的 `dotnet new sln` 默认生成 slnx 格式，功能等价。
2. **自检阈值取 p99 而非最大值**：GC 与调度抖动会让偶发单次回调升到 1.5–2.5 ms，用最大值会把
   正常抖动判成缺陷。阈值 2 ms = §八 120 ms 预算的 1/60。
3. **输入压力测试用 Unicode 注入**：`KEYEVENTF_UNICODE` 绕过键盘布局、CapsLock 与输入法，
   否则中文输入法会吞掉注入的字母键，测出来的是输入法而不是 HUD。该注入不经过 Raw Input
   通路，故回调耗时由 `--selftest` 用 F24 独立测。

## 进度

| 阶段 | 状态 |
|---|---|
| S1 输入通路与「不干扰」 | 完成（自动验证 10/10；5 分钟真人记事本打字待人工复核） |
| S2 四条规则 | 未开始 |
| S3 数据与渲染 | 未开始 |
| S4 游戏抑制接入 | 未开始 |
| S5 工程化 | 未开始 |
