<!--
用途：S7.5 开工指令（用户自定义档案 + 自动读取适配器）。整段交给执行 Agent。
前置：docs/shortcut-display-spec.md（本次升 v1.8，新增 §3.4）。
用户决定：放弃微信/QQ/网易云/豆包的抄录，改做「用户自己加」的能力。
-->

# 角色与本次任务

用户判断：那 4 个应用的快捷键价值低，不值得人工抄。改为**把「加应用」的能力交给用户**。

本次做三件事：

| # | 项 | 价值 |
|---|---|---|
| 1 | 用户档案目录 + 合并规则 | 任何应用都能被用户补上 |
| 2 | 托盘「添加应用快捷键…」入口 | 不用手改 JSON 也能加 |
| 3 | VS Code / JetBrains 适配器 | **读出用户改过的真实键位**，这是内置档案做不到的 |

---

# 零、先划清「自动读取」的边界

**Windows 没有「读取任意应用快捷键」的通用 API。** 只有「应用自己把键位存在配置文件里」的才能读。

| 应用 | 能读吗 | 原因 |
|---|---|---|
| VS Code | ✅ `keybindings.json` | 用户覆盖层是明文 JSON |
| JetBrains 系 | ✅ `keymaps/*.xml` | 明文 XML |
| Office / 微信 / 浏览器 / 网易云 | ❌ | 无公开配置 |
| 其他 | ❌ | 无 |

**只做这两家。** 不要为"看起来支持更多"去猜文件路径——猜错产出错误键位，比没有更糟。

**还有一个必须说清的限制**：适配器只能读到**用户改过或新增的键位**，读不到应用内置的默认键位（那些在安装目录的只读资源里）。
所以：

```
内置档案（默认键位） + 用户档案（改过的键位） = 完整视图
```

VS Code 用户如果从没改过键位，`keybindings.json` 是空数组 → 适配器产出 0 条，这是**正确行为**，不要报错。

---

# 一、范围

## 1.1 用户档案目录

```
%APPDATA%\HotkeyHud\apps\
  _global.json      全局条目，所有应用可见，组名「自定义」
  <exe>.json        应用级条目，与同名内置档案合并
```

**格式与内置档案完全一致**（`app` / `displayName` / `processNames` / `shortcuts`），`_global.json` 省略 `processNames`。

合并规则：

| 规则 | 说明 |
|---|---|
| 顺序 | 内置先、用户后 |
| 去重 | **同 `keys` 用户覆盖内置**（用户改过的键位应该赢） |
| 降权豁免 | 用户显式写的条目不受「通用」降权影响，正常排在应用组内 |
| 失败 | 跳过该文件 + 记入 `LoadErrors`，不影响其他档案 |
| 进程名 | 从用户选的 exe 自动取 `Path.GetFileName`，**不许让用户手写** |

## 1.2 托盘入口

托盘 → 「添加应用快捷键…」：

1. `OpenFileDialog` 选 exe（过滤 `*.exe`）
2. 取进程名 → 尝试匹配适配器
3. 命中：生成 `<exe>.json`，气泡提示「已从 <应用> 读取 N 条」
4. 未命中：生成模板 JSON（含 3 条示例 + 注释），用默认程序打开
5. 完成后自动 `Reload()`

**生成的 JSON 必须能被用户看懂**：带 `// 说明` 注释（读取时已允许注释），示例条目用真实存在的键。

## 1.3 VS Code 适配器

读取 `%APPDATA%\Code\User\keybindings.json`：

```json
[ { "key": "ctrl+alt+f", "command": "editor.action.formatDocument" } ]
```

转换：

| 步骤 | 说明 |
|---|---|
| `key` → `keys` | `ctrl+alt+f` → `Ctrl+Alt+F`；多键序列（`ctrl+k ctrl+s`）**跳过**，本产品只支持单组合 |
| `command` → `action` | 取末段：`editor.action.formatDocument` → `formatDocument` |
| `when` | 忽略（上下文条件无法在 HUD 表达） |
| `command: "-xxx"` | 表示解绑，**跳过** |

**不要翻译 command id。** 用户认识自己设的命令，硬翻成中文反而对不上。在组名标「VS Code 自定义」。

## 1.4 JetBrains 适配器

读取 `%APPDATA%\JetBrains\<product>\keymaps\*.xml`：

```xml
<action id="SaveAll">
  <keyboard-shortcut keymap="$default" first-keystroke="control S"/>
</action>
```

转换：`first-keystroke` 的 `control`/`shift`/`alt`/`meta` + 键名 → `Ctrl+Shift+S`。
只取 `first-keystroke`，`second-keystroke` 的组合跳过。`action id` 直接作 `action`。

**多产品目录**（`IntelliJIdea2024.1`、`PyCharm2024.1`…）→ 让用户选，或取最新的一个并在气泡里说明选了哪个。

---

# 二、契约

| 变更 | 说明 |
|---|---|
| `ShortcutRepository` 增加用户目录参数 | 内置目录 + 用户目录，两个来源 |
| `ShortcutEntry` 增加 `Source` 字段 | `builtin` / `user`，用于合并去重与排序 |
| `HudModel` 不变 | 用户条目只是更早进入 groups |

---

# 三、验收（7 条）

| # | 项 | 判定 |
|---|---|---|
| 1 | 用户档案生效 | 手工在 `%APPDATA%\HotkeyHud\apps\notepad.json` 加一条 `Ctrl+Q 退出`，重载后记事本面板出现该条 |
| 2 | 用户覆盖内置 | 用户档案里写 `Ctrl+F 我的查找`，面板显示「我的查找」而非内置文案 |
| 3 | 降权豁免 | 用户档案写 `Ctrl+C 我的复制`，该条**出现在应用组**（不在「通用」组） |
| 4 | 损坏不影响 | 用户档案写坏 JSON，其他档案仍正常，气泡提示错误 |
| 5 | VS Code 适配器 | 用户改过键位的机器上，产出条目数 > 0 且键位与 `keybindings.json` 一致 |
| 6 | 托盘入口 | 选 `notepad.exe` → 生成模板 → 打开 → 填一条 → 重载后生效 |
| 7 | 回归 | tests 19/19、selftest 8/8、integration 10/10、paging 6/6、gallery 7/7、suppression 6/6、shell 8/8 |

第 5 条如果本机没改过 VS Code 键位，**允许先造一条**（在 keybindings.json 里加一条测试用），验证完删掉，并在交付说明写明。

---

# 四、禁止事项

1. 不许猜应用配置路径。只做 `Code.exe` 与 JetBrains 两类。
2. 不许翻译 command id / action id 成中文。
3. 不许把用户档案写进程序安装目录（必须是 `%APPDATA%`）。
4. 不许让用户手填进程名。
5. 不许在适配器失败时静默——必须给「未匹配，已生成模板」的明确提示。
6. 不改 §6.1 视觉参数、不改判定规则。

---

# 五、交付格式

```
## 产物
- <文件>  改动说明

## 适配器实测
| 应用 | 读取文件 | 产出条数 | 抽样 3 条（keys + action） |

## 用户档案实测
- 覆盖内置：<截图或日志>
- 降权豁免：<该条落在哪个组>

## 截图
- 托盘菜单（含新入口）
- 生成的模板 JSON（前 10 行）

## 验证
<七项门禁输出>

## 下一步
请给出 S8 发布准备提示词。
```

---

# 六、顺序

1. 用户档案目录 + 合并规则（Core，可单测）→ 跑回归。
2. 托盘入口（无适配器，只生成模板）→ 手测用例 1–4。
3. VS Code 适配器 → 用例 5。
4. JetBrains 适配器。
5. 跑满七项门禁。

**第 1 步必须先把「合并 + 覆盖 + 降权豁免」测透**：适配器只是往这个管道里灌数据，管道不对，灌什么都是错的。
