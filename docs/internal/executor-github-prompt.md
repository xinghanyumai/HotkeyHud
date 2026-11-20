<!--
用途：把代码发布到 GitHub 的指令。整段交给执行 Agent。
前置：GitHub connector 已连接；代码已冻结（v1.0.0）。
-->

# 角色与本次任务

在 GitHub 新建一个公开仓库，把 v1.0 的代码推上去，并写一份**面向陌生开发者**的 README。

本地 README 是给使用者看的（怎么装、怎么用）。GitHub README 是给**路过的人**看的：他为什么该停下来读第二段。

---

# 零、三个默认值（按此执行，除非用户另行指示）

| 项 | 默认 | 理由 |
|---|---|---|
| 可见性 | **Public** | 这个项目的可信度建立在「代码可查」上——一个监听键盘的程序，闭源等于自杀 |
| 许可证 | **MIT** | 工具类项目的通行选择；若用户要限制商用，改用 BSL 或 PolyForm |
| 仓库名 | **HotkeyHud** | 与产品名一致，URL 干净 |
| 二进制 | **不传** | 50 MB 不进仓库；未签名，不建 Release。签名后再发 |

**如果用户没明确说 public，先问一句再建。** 仓库一旦公开，git 历史就永久可查。

---

# 一、建仓库

```
仓库名：HotkeyHud
描述（Description 字段，一句话，≤ 100 字符）：
按住 Ctrl / Alt / Shift，看见当前应用所有能按的快捷键。不抢焦点，不挡点击。

Topics：windows  csharp  winforms  productivity  keyboard-shortcuts  overlay  tray-app
```

初始化选项：**不要**勾选「Add a README」「Add .gitignore」「Choose a license」——本地已有，勾了会冲突。

---

# 二、推送前必须做的清理

**这一步漏了会很难看，而且删不掉（git 历史永久）。**

| 检查 | 怎么做 | 判定 |
|---|---|---|
| 无密钥 | `git log -p \| grep -iE "token\|secret\|password\|api[_-]?key"` | 零命中 |
| 无大文件 | `git rev-list --objects --all \| git cat-file --batch-check \| awk '$3>1e6'` | 无 > 1 MB 的文件 |
| `dist/` 未入库 | 检查 `.gitignore` 含 `dist/` | 是 |
| `bin/` `obj/` 未入库 | 同上 | 是 |
| 无个人路径 | `grep -rn "C:\\\\Users\\\\XingHan" .` | 零命中（文档里的 `D:\Project\快捷键显示` 也换成相对路径或占位符） |
| 无内部流程文档 | `docs/executor-s*-prompt.md`、`docs/stage-gate.md` 是否要公开？ | **建议移入 `docs/internal/` 并在 README 不提**——这些是过程产物，对外只会显得杂乱 |

> 内部提示词不是见不得人，但它们是「给 AI 的工作指令」，混在产品文档里会让读者困惑「这仓库到底是工具还是实验记录」。
> 如果用户希望展示开发过程，可保留在 `docs/internal/` 并在 README 末尾一句带过。

---

# 三、README 规格

## 3.1 结构（按此顺序，不许加花哨章节）

```markdown
# HotkeyHud

> 一句话定位（不超过 25 字）

[主截图：面板浮在文档上，占屏约 1/4]

三句话说明：它解决什么问题、怎么用、代价是什么。

## 为什么
## 特性
## 快速开始
## 支持的应用
## 自己加应用
## 配置
## 从源码构建
## 已知限制
## 与其他工具的区别
## 许可
```

## 3.2 每一节写什么

**一句话定位**：不写「革命性」「最强」。写事实。

> 按住 Ctrl 半秒，看见这个应用所有能按的快捷键。

**为什么**：讲清痛点，3–4 句。
快捷键记不住是常态，查文档要切窗口，查完还得回来。这个工具把答案放在你手边，且不打断你。

**特性**：6–8 条，每条一行，**每条都必须是可验证的事实**，不许形容词堆砌。

- 不抢焦点：面板出现时你还能继续打字
- 点击穿透：不挡鼠标，点哪都点到下面的窗口
- 只显示当前应用：Word 里不会出现 Chrome 的键位
- 分页浏览：`↑` `↓` 翻页，翻页键不会传给前台应用
- 支持 7 个应用 + 系统快捷键，可自己加
- 能读出 VS Code / JetBrains 里**你改过的**键位
- 常驻约 12 MB 内存，空载 CPU < 0.5%
- 零第三方依赖，单文件 49 MB（含 .NET 运行时）

**快速开始**：三步，带命令或路径。

```
1. 下载 HotkeyHud.zip 解压（整个文件夹，不要只拷 exe）
2. 双击 HotkeyHud.App.exe，托盘出现图标
3. 打开任意应用，按住 Ctrl 半秒
```

加一句 Windows 拦截说明（未签名）：

> 首次运行 Windows 会提示「已保护你的电脑」，点「更多信息 → 仍要运行」。原因与校验值见下方「已知限制」。

**支持的应用**：表格，应用 / 条数 / 来源（官方文档）。

**自己加应用**：两条路径都要写。
1. 托盘 →「添加应用快捷键…」→ 选 exe
2. 手写 `%APPDATA%\HotkeyHud\apps\<exe>.json`，附一个最小示例

**配置**：4 个字段的表格（`dwellMs` / `knownShortcuts` / `extraGameProcesses` / `startWithWindows`），每个给默认值和范围。

**从源码构建**：

```
git clone ...
cd HotkeyHud
dotnet build HotkeyHud.slnx -c Release
```

**已知限制**：8 条，如实写。**这一节是可信度的来源**，不要藏。

**与其他工具的区别**：表格对比 3 个竞品，只写事实差异，不贬低。

| | HotkeyHud | PowerToys Shortcut Guide | Keyviz |
|---|---|---|---|
| 触发方式 | 按住修饰键 | Win+Shift+? 或长按 Win | 常驻显示 |
| 只显示当前应用 | ✅ | ✅（需 manifest） | ❌ |
| 不抢焦点 | ✅ | 部分 | ✅ |
| 读用户自定义键位 | ✅ VS Code / JetBrains | ❌ | ❌ |
| 平台 | Windows | Windows | 跨平台 |

**许可**：MIT，附一行「欢迎 issue / PR」。

## 3.3 语气

| 要 | 不要 |
|---|---|
| 陈述事实，给数字 | 「极致」「丝滑」「颠覆」 |
| 承认限制 | 假装没有缺点 |
| 短句 | 长定语从句 |
| 具体 | 「很快」→「冷启动 378 ms」 |

**禁写**：假的徽章（没有 CI 就别放 build passing）、编造的 star 数、未验证的性能数字、「全球首个」。

## 3.4 徽章

只放真实可验证的：

```markdown
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)
```

**不放** build status（没有 CI）、downloads（没有数据）。

---

# 四、截图

需要 3 张，放 `docs/images/`：

| 文件 | 内容 | 来源 |
|---|---|---|
| `panel.png` | 面板浮在文档上（主图，README 顶部） | `--render-gallery` 的 `01-normal.png` |
| `paging.png` | 第 2/4 页，页脚可见 | 同上，多页样本 |
| `settings.png` | 设置页 | `--settings-demo` 截图 |

**要求**：
- 宽度 ≤ 1200 px（GitHub 正文宽度），超过会被压缩
- PNG，体积 ≤ 300 KB/张
- 不要加边框、阴影、标注箭头——原始截图更可信

---

# 五、明确不做

| 项 | 为什么 |
|---|---|
| GitHub Release + 上传 exe | 未签名，公开分发会放大 SmartScreen 拦截与杀软误报。签名后再发 |
| CI（GitHub Actions） | 没有跨平台需求，加 CI 只会让「构建通过」变成一种表演 |
| Issue / PR 模板 | 用户量还不需要。等第一个 issue 来了再建 |
| CONTRIBUTING.md | 同上 |
| 英文 README | 目标用户是中文使用者。加英文版需要同步维护，不如不做 |

---

# 六、验收（6 条）

| # | 项 | 判定 |
|---|---|---|
| 1 | 仓库可访问 | 浏览器打开 URL，未登录也能看到 README |
| 2 | 无敏感内容 | 上述清理四项全部零命中 |
| 3 | README 主图可见 | 打开仓库首屏能看到面板截图，不滚动 |
| 4 | 事实可核 | 特性里每条都能在仓库里找到对应代码或文档 |
| 5 | 克隆可构建 | 在干净目录 `git clone` → `dotnet build` 成功 |
| 6 | 体积合理 | 仓库 < 10 MB（不含 .git） |

第 5 条要真做一遍：`cd %TEMP% && git clone <url> && cd HotkeyHud && dotnet build`。
**在本地仓库里构建成功不算**——本地有 bin/obj 残留，掩盖了缺失文件的问题。

---

# 七、交付格式

```
## 仓库
- URL：
- 可见性：
- 许可证：
- 提交数 / 首次提交：

## 清理检查
| 项 | 结果 |
| 密钥 | |
| 大文件 | |
| 个人路径 | |
| dist/bin/obj | |

## README
- 主图文件与尺寸
- 字数

## 干净克隆验证
$ git clone <url> /tmp/x && cd /tmp/x && dotnet build
<输出>

## 下一步
请给出发布检查单的剩余项（签名 / 干净机器 / VirusTotal）。
```

---

# 八、顺序

1. 先做清理检查（第二节）——**发现密钥就停下报告**，不要先推再删。
2. 整理文档（内部提示词移入 `docs/internal/`）。
3. 准备 3 张截图。
4. 写 README。
5. 建仓库、推。
6. 干净克隆验证。

**第 1 步和第 5 步之间不要跳。** git 历史一旦公开，删不干净。
