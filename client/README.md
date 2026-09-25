# client/

C# **.NET 8** WPF 客户端。一期只做 **Windows**：托盘常驻、全局热键、剪贴板翻译，以及快捷键裁剪后的 OCR 翻译（M3）。翻译本身调用 `engine/` 的本机 HTTP 服务（见 [HTTP API](../docs/engine/HTTP-API.md)），不在客户端内嵌模型。

## 目录

```
client/
  Suiyi.sln                   解决方案（三个项目，名称固定）
  global.json                 固定 .NET 8 SDK（8.0.x，rollForward: latestFeature）
  Directory.Build.props       公共属性（可空、告警即错误、分析器、EnableWindowsTargeting）
  Directory.Packages.props    中央包版本管理：项目里只写包名，版本写这里
  .editorconfig               C# 代码风格与分析器严重级别（继承根 .editorconfig）
  .gitattributes              client/ 下统一 LF
  src/
    Suiyi.Core/               net8.0 类库：纯逻辑，Linux 上可测
    Suiyi.App/                net8.0-windows WPF 应用：窗口、托盘、Win32 互操作、组合根
  tests/
    Suiyi.Core.Tests/         net8.0 xUnit，测试 Suiyi.Core
```

## 分层约定

- **能脱离 Windows 测的逻辑都放 `Suiyi.Core`**：设置、HTTP 客户端、文本过滤、状态机、ViewModel、日志。`Suiyi.Core` 的目标框架是 `net8.0`，不引用 WPF / WinForms，不写 P/Invoke。
- `Suiyi.Core` 通过接口和事件与 `Suiyi.App` 交互。需要时间的逻辑注入 `TimeProvider`，需要文件路径的逻辑允许用构造参数或环境变量覆盖，便于单测。
- **Win32 互操作集中在 `Suiyi.App/Interop/`**：手写 `LibraryImport`（项目已开启 `AllowUnsafeBlocks`），不强制引入 CsWin32。
- **组合根在 `Suiyi.App/App.xaml.cs`**：后续 Issue 在 `OnStartup` 里创建并注册自己的组件，在 `OnExit` 里按相反顺序清理。不引入 DI 容器和 MVVM 框架。
- `Suiyi.Core` 建议子目录（命名空间与目录一致，如 `Suiyi.Core.Settings`）：

  | 目录 | 内容 |
  |------|------|
  | `Engine/` | HTTP 客户端与服务进程管理逻辑 |
  | `Settings/` | 设置模型与读写 |
  | `Clipboard/` | 剪贴板监听、过滤规则、自身写入抑制（已有） |
  | `Popup/` | 浮窗 ViewModel 与状态机 |
  | `Logging/` | 文件日志（已有） |

- 测试放 `tests/Suiyi.Core.Tests/`，目录结构与 `Suiyi.Core` 对应。
- 第三方包只允许 MIT / Apache-2.0 / BSD 等宽松许可证，版本统一写在 `Directory.Packages.props`。

## 代码风格与告警

- 构建即 lint：`TreatWarningsAsErrors=true`、`AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild=true`。未使用变量、多余 `using`、命名不符等都会让构建失败。
- 格式：`dotnet format --verify-no-changes`，相当于引擎的 `ruff format --check`。提交前先跑 `dotnet format client/Suiyi.sln` 自动修正。
- 私有字段 `_camelCase`；常量与 `static readonly` 字段 `PascalCase`；文件级命名空间；`if` 等一律加大括号。
- 公开成员建议写 XML 注释（骨架阶段不强制 CS1591，具体 Issue 可另行要求）。

## 日志

`Suiyi.Core.Logging.FileLogger`：UTF-8（无 BOM），按本地日期滚动，保留最近 7 天。

- 路径：`%LOCALAPPDATA%\suiyi\logs\client-YYYYMMDD.log`
- 环境变量 `SUIYI_LOG_DIR` 可覆盖目录（测试、便携模式）
- **不记录剪贴板正文等用户内容**，只记录长度、原因、耗时

## 剪贴板监听

复制即翻译的触发源（#30）。纯逻辑在 `Suiyi.Core/Clipboard`，Win32 部分在 `Suiyi.App/Interop`。

```
WM_CLIPBOARDUPDATE → 去抖（默认 150 ms，只处理最后一次）→ 序号未变则跳过
  → 自身写入 / SuppressNext 窗口内 → 忽略（不发事件）
  → 已暂停 → 忽略（不读剪贴板、不发事件）
  → 读取：带隐私标记 → 跳过，不读正文；无文本格式 → 跳过；被占用 → 每 30 ms 重试，最多 3 次
  → ClipboardTextFilter → TextCaptured(text, Monitor) 或 TextRejected(reason)
```

| 类型 | 所在 | 作用 |
|------|------|------|
| `IClipboardSource` | Core | 平台抽象：`Changed`、`StartListening/StopListening`、`GetSequenceNumber`、`TryReadText`、`TrySetText` |
| `Win32ClipboardSource` | App/Interop | 仅消息窗口（`HWND_MESSAGE`）+ `AddClipboardFormatListener`；直接用 Win32 读写 `CF_UNICODETEXT`，不用 WPF `Clipboard`（占用时会抛 `COMException`，也读不到隐私格式） |
| `ClipboardMonitor` | Core | 监听编排：`Start()`、`Stop()`、`Paused`、`Options`，事件 `TextCaptured`、`TextRejected` |
| `ClipboardWriter` | Core | 本程序写剪贴板的唯一入口：`SetText(text)` 写入后记录序号；`SuppressNext(TimeSpan)` 供快捷键模拟复制 |
| `SelfWriteTracker` | Core | 自身写入序号与一次性忽略窗口。`ClipboardWriter` 与 `ClipboardMonitor` 必须共用同一实例 |
| `ClipboardTextFilter` | Core | `Classify`（纯函数）与 `Evaluate`（含 2 s 去重） |
| `ClipboardFilterOptions` / `ClipboardMonitorOptions` | Core | 阈值与去抖，由集成层从设置映射 |
| `ClipboardTextCapturedEventArgs` | Core | `Text` + `Trigger`（`Monitor` / `Hotkey`），快捷键（#33）发同一形状的事件 |

`ClipboardMonitor.Start()` 要在 UI 线程调用：它捕获当前 `SynchronizationContext`，读取剪贴板和事件都回到 UI 线程。

**隐私：** 剪贴板含 `ExcludeClipboardContentFromMonitorProcessing`，或 `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` 的值为 0 时（密码管理器常用）一律跳过，不读正文。日志只记录长度、拒绝原因和耗时，**不记录剪贴板正文**。

**过滤规则**（规范化：去首尾空白，换行统一为 `\n`；长度按 Unicode 码位计）：

| 顺序 | 规则 | `RejectReason` |
|------|------|----------------|
| 1 | 空白 | `Empty` |
| 2 | 少于 `MinChars`（默认 2） | `TooShort` |
| 3 | 多于 `MaxChars`（默认 2000；快捷键可放宽到 10000） | `TooLong` |
| 4 | 只有数字、空白与 `+-.,:/()%¥$` 等（金额、日期、电话） | `NumericLike` |
| 5 | 没有任何文字（纯标点、符号、emoji） | `SymbolsOnly` |
| 6 | 单行且整体是网址（`http(s)://`、`ftp://`、`www.`） | `Url` |
| 7 | 单行且整体是邮箱 | `Email` |
| 8 | 单行 Windows 路径（`C:\`、`C:/`、`\\server\`）或无空白的 Unix 路径（`/`、`~/`、`./`、`../`） | `FilePath` |
| 9 | 单行 GUID / 十六进制哈希（`^\{?[0-9a-fA-F-]{16,}\}?$`） | `HexOrGuid` |
| 10 | 至少 3 个非空行，且超过一半以 `;`、`{`、`}` 结尾 | `CodeLike` |
| 11 | 与上一次被接受的文本相同，且间隔 < 2 s | `Duplicate` |

读取阶段的原因：`NoText`（图片、文件列表等）、`PrivateContent`、`ClipboardBusy`。

**手测：** 目前 `Suiyi.App` 启动即开始监听，结果写日志（`剪贴板：接受 N 字` / `剪贴板：跳过（原因，N 字）` / `剪贴板：忽略自身写入`）。占位窗口上有「暂停剪贴板监听」勾选框和「写入测试文本（不应触发）」按钮，托盘（#29）替换占位窗口时一并移除。翻译与浮窗由 #34 接到 `TextCaptured` 上。

## 全局快捷键

默认 `Ctrl+Alt+T`（#33）：有选中文本就复制并翻译它，否则翻译当前剪贴板文本。**暂停剪贴板监听时快捷键仍然可用。** 纯逻辑在 `Suiyi.Core/Hotkeys`，Win32 在 `Suiyi.App/Interop`。

```
WM_HOTKEY（RegisterHotKey + MOD_NOREPEAT，长按不连发）
  → 等待松开 Ctrl/Alt/Shift/Win（每 10 ms 查一次，最长 500 ms，超时仍继续）
  → ClipboardWriter.SuppressNext(1 s)：让剪贴板监听忽略这次模拟复制
  → 记录剪贴板序号 → SendInput 模拟 Ctrl+C
  → 300 ms 内序号变化：读新文本；没变：取消 SuppressNext，回退为当前剪贴板文本
  → 过滤（与监听同一套规则，上限放宽到 10000 字，不做 2 s 去重）
  → TextCaptured(text, ClipboardTrigger.Hotkey) 或 Rejected(reason)
```

| 类型 | 所在 | 作用 |
|------|------|------|
| `HotkeyParser` | Core | `TryParse` / `Normalize`；`DefaultTranslate = "Ctrl+Alt+T"` |
| `HotkeyGesture` / `HotkeyModifiers` / `HotkeyKeys` | Core | 修饰键（取值同 `MOD_*`）+ 虚拟键码；`ToString()` 为规范写法 |
| `HotkeyManager` | Core | `Update(string)` 注册 / 重新注册 / 禁用；`Current`；事件 `Pressed`、`RegistrationFailed`（`Message` 为面向用户的提示） |
| `HotkeyTranslateAction` | Core | 取词流程 `ExecuteAsync()`；事件 `TextCaptured`、`Rejected`；上一次未结束时返回 `AlreadyRunning` |
| `IHotkeyRegistrar` / `Win32HotkeyRegistrar` | Core / App | `RegisterHotKey` 抽象与实现（自建仅消息窗口收 `WM_HOTKEY`） |
| `IKeyboardInput` / `Win32KeyboardInput` | Core / App | `GetAsyncKeyState` 查修饰键、`SendInput` 模拟 Ctrl+C |

**快捷键写法：** 修饰键 `Ctrl`（`Control`）、`Alt`、`Shift`、`Win`（`Windows`）加一个按键，用 `+` 连接，大小写与空格不敏感，例如 `Ctrl+Alt+T`、`Ctrl+Shift+F1`、`Win+Alt+Y`。按键可以是 `A`–`Z`、`0`–`9`、`F1`–`F24`、`NumPad0`–`NumPad9`、`Space`、`Enter`、`Tab`、`Esc`、`Backspace`、`Insert`、`Delete`、`Home`、`End`、`PageUp`、`PageDown`、方向键、`Pause`、`PrintScreen`。除 `F1`–`F24` 外必须带 `Ctrl`、`Alt` 或 `Win`（`Shift+T` 会打出大写字母，不允许）。空字符串表示禁用。

**修改：** 设置（#27）接入前，用命令行 `dotnet run --project client/src/Suiyi.App -- --hotkey "Ctrl+Shift+Y"` 指定，或在占位窗口的输入框里改后点「应用快捷键」（调用 `HotkeyManager.Update`）。接入设置后改 `settings.json` 的 `hotkey.translate`。

**被占用：** 注册失败时发 `RegistrationFailed`，提示「快捷键 Ctrl+Alt+T 已被其他程序占用，请在设置中修改」，程序继续运行；集成（#34）用托盘气泡显示，目前显示在占位窗口上并写日志。

**已知行为：**

- **不恢复剪贴板。** 选中文本时，模拟复制会用选中内容替换剪贴板原内容（恢复多格式内容复杂且容易丢数据，M2 不做）。
- **管理员窗口。** 目标窗口以管理员权限运行而随译没有时，`SendInput` 会被 UIPI 静默拦截，剪贴板不变，此时退化为翻译当前剪贴板，并写日志。
- 部分终端、远程桌面等程序对模拟 Ctrl+C 的处理不同，同样会退化为翻译当前剪贴板。

## 环境

- .NET 8 SDK（`global.json` 允许 8.0 下更高的特性带）
- 运行 WPF 应用需要 Windows 10/11 x64
- Linux / macOS 上可以构建全部项目（`EnableWindowsTargeting=true`）并运行 `Suiyi.Core.Tests`，但不能运行 `Suiyi.App`

## 命令

在仓库根目录执行。

### bash

```bash
dotnet --version                                     # 8.0.x
dotnet restore client/Suiyi.sln
dotnet format client/Suiyi.sln --verify-no-changes   # 格式与代码风格检查
dotnet build client/Suiyi.sln -c Release             # 零告警零错误
dotnet test client/Suiyi.sln -c Release              # Linux 上可只跑 client/tests/Suiyi.Core.Tests
dotnet format client/Suiyi.sln                       # 自动修正格式
```

### Windows PowerShell

```powershell
dotnet --version
dotnet restore client/Suiyi.sln
dotnet format client/Suiyi.sln --verify-no-changes
dotnet build client/Suiyi.sln -c Release
dotnet test client/Suiyi.sln -c Release
dotnet run --project client/src/Suiyi.App            # 弹出「随译」占位窗口，关闭即退出
```

生成的可执行文件是 `client/src/Suiyi.App/bin/<配置>/net8.0-windows/Suiyi.exe`。

## CI

`.github/workflows/ci.yml` 的 `client（windows-latest）` job 在 `client/**` 或工作流有变更时运行：`dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build -c Release` → `dotnet test -c Release`。汇总检查「client 检查」与「engine 检查」对称：路径有变更时 client job 必须成功，没有变更时必须被跳过。
