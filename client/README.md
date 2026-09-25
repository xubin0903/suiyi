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
  | `Engine/` | HTTP 客户端（已有）与服务进程管理逻辑 |
  | `Settings/` | 设置模型与读写 |
  | `Clipboard/` | 剪贴板监听、过滤规则、自身写入抑制（已有） |
  | `Popup/` | 浮窗 ViewModel 与状态机 |
  | `Hotkeys/` | 快捷键解析、注册管理、取词流程（已有） |
  | `Tray/` | 托盘状态、菜单模型、托盘控制器（已有） |
  | `Lifecycle/` | 单实例等进程生命周期逻辑（已有） |
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

## 托盘

常驻托盘，无主窗口（#29）。纯逻辑在 `Suiyi.Core/Tray`、`Suiyi.Core/Lifecycle`，WinForms `NotifyIcon` 渲染在 `Suiyi.App/Tray`（框架自带，不引入第三方包）。

| 类型 | 位置 | 作用 |
|------|------|------|
| `TrayStatus` | Core | `Preparing` / `Ready` / `Paused` / `Error` |
| `TrayState` | Core | 不可变记录：`Status`、`Detail`、`Paused`、`Target`；推导 `Effective`（异常、准备中优先于暂停）、`StatusText`、`ToolTip`（≤127 字符） |
| `TrayMenuBuilder` / `TrayMenuItem` / `TrayCommand` | Core | 按状态生成菜单模型（纯函数），含勾选与禁用 |
| `ITrayService` / `TrayController` | Core | 对外接口：`SetStatus(status, detail)`、`SetPaused`、`SetTarget`、`ShowNotification(title, message)`、`State`；事件 `TranslateClipboardRequested`、`PauseToggled`、`TargetChanged`、`RestartEngineRequested`、`OpenSettingsRequested`、`OpenLogsRequested`、`AboutRequested`、`ExitRequested`、`ShowLastPopupRequested` |
| `TrayLanguages` | Core | 菜单里的目标语言：`zh` 中文、`en` English、`ja` 日本語 |
| `TrayDemo` | Core | `--tray-demo` 的轮换序列 |
| `SingleInstanceGuard` | Core | 命名 Mutex `Local\Suiyi.Client` |
| `NotifyIconTrayView` / `TrayIconRenderer` | App | 图标与菜单渲染；图标运行时绘制（状态色方块 + 白色「译」字，多尺寸） |
| `InstanceActivation` | App | 命名事件 `Local\Suiyi.Client.Activate`：第二个实例通知第一个实例弹「随译已在运行」 |

**状态与悬停提示：**

| 状态 | 图标底色 | 悬停提示 |
|------|----------|----------|
| 正在准备 | 橙 | `随译 · 正在准备…` |
| 就绪 | 蓝 | `随译 · 就绪（中文）` |
| 已暂停监听 | 灰 | `随译 · 已暂停监听` |
| 服务异常 | 红 | `随译 · 翻译服务异常：<原因>` |

**菜单：** 状态行（灰）、翻译剪贴板、暂停监听 ✓、目标语言 ▸ 中文 / English / 日本語、重启翻译服务、打开设置文件、打开日志目录、关于、退出。左键单击发 `ShowLastPopupRequested`。

**当前接线（`App.xaml.cs`）：** 暂停监听 → `ClipboardMonitor.Paused`；快捷键注册失败 → 托盘气泡；翻译服务状态由 `EngineSupervisor`（#32）驱动：Starting / Restarting → 正在准备，Ready → 就绪，Failed → 异常并弹气泡（崩溃重启时也弹），映射见 `Tray/EngineTrayStatus`；「重启翻译服务」调用 `EngineSupervisor.RestartAsync()`；目标语言只存在托盘状态里（设置 #27 持久化，#34 翻译时读取 `State.Target`）；翻译剪贴板与左键单击暂时只写日志（#34 接线）。打开设置文件：`%APPDATA%\suiyi\settings.json`（`SUIYI_CONFIG_DIR` 可覆盖），不存在时打开目录。

**手测：**

```powershell
dotnet run --project client/src/Suiyi.App -- --tray-demo   # 每 2 秒轮换四种状态
dotnet run --project client/src/Suiyi.App -- --hotkey "Ctrl+Shift+Y"
```

再启动一次会看到「随译已在运行」气泡，第二个进程立即退出。

**DPI：** `Suiyi.App/app.manifest` 声明 PerMonitorV2（回退 `true/pm`）。WinForms 分析器的 WFAC010 建议改用 `Application.SetHighDpiMode`，但这是 WPF 应用，只能用 manifest，已在 csproj 中忽略。

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

**手测：** 目前 `Suiyi.App` 启动即开始监听，结果写日志（`剪贴板：接受 N 字` / `剪贴板：跳过（原因，N 字）` / `剪贴板：忽略自身写入`）。暂停 / 恢复用托盘菜单「暂停监听」（设置 `ClipboardMonitor.Paused`）。翻译与浮窗由 #34 接到 `TextCaptured` 上。

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

**修改：** 设置（#27）接入前，用命令行 `dotnet run --project client/src/Suiyi.App -- --hotkey "Ctrl+Shift+Y"` 指定（启动时调用 `HotkeyManager.Update`）。接入设置后改 `settings.json` 的 `hotkey.translate`。

**被占用：** 注册失败时发 `RegistrationFailed`，提示「快捷键 Ctrl+Alt+T 已被其他程序占用，请在设置中修改」，程序继续运行；托盘弹气泡显示该提示并写日志。

**已知行为：**

- **不恢复剪贴板。** 选中文本时，模拟复制会用选中内容替换剪贴板原内容（恢复多格式内容复杂且容易丢数据，M2 不做）。
- **管理员窗口。** 目标窗口以管理员权限运行而随译没有时，`SendInput` 会被 UIPI 静默拦截，剪贴板不变，此时退化为翻译当前剪贴板，并写日志。
- 部分终端、远程桌面等程序对模拟 Ctrl+C 的处理不同，同样会退化为翻译当前剪贴板。

## 与引擎通信

代码在 `Suiyi.Core/Engine/`，契约见 [HTTP API](../docs/engine/HTTP-API.md)，超时阈值依据 [性能基线](../docs/engine/性能基线.md)「给 M2 客户端」。

- **`EngineClient`**：整个进程一个实例，连接 `http://127.0.0.1:{port}`（默认 18780）。内部只有一个长寿命 `HttpClient`，处理器为 `SocketsHttpHandler { UseProxy = false }`（系统代理不能拦截回环地址），`HttpClient.Timeout` 为无限，每次请求由 `TimeoutPolicy` 算出超时并用 `CancellationTokenSource`（基于注入的 `TimeProvider`）取消。
  - `GetHealthAsync()`：刷新已加载模型；`GetLanguagesAsync()`：结果缓存；服务重启或模型目录变化后调用 `Invalidate()`。
  - `TranslateAsync(text, source = "auto", target)`：缓存未知时先各取一次 `/languages`、`/health`（失败按「可能懒加载」处理），请求体为 UTF-8 JSON；成功后把 `route` 里的模型记为已加载。
  - 响应 DTO 忽略未知字段（服务 0.0.x 内字段只增不改），不要打开 `UnmappedMemberHandling.Disallow`。
- **`TranslationService`**：界面调用的入口。目标语言由注入的 `Func<(primary, secondary)>` 每次读取。
  - 默认 `auto → 主目标`；服务检测到原文就是主目标语种（`route` 为空、原样返回）时，再以 `检测结果 → 第二目标` 请求一次。
  - `sourceOverride`：用户手动指定原文语种（如纯汉字日语被检测成 zh），不再自动检测；指定语种等于主目标时直接译为第二目标。
  - **最新请求优先**：新调用会取消上一条未完成的调用；被取消的调用抛 `OperationCanceledException`，不是错误，界面直接丢弃即可。
  - 结果 `TranslationOutcome`：译文、原文语种、是否自动检测、实际目标、是否改译、`route`、服务端 `elapsed_ms`（改译为两次之和）、客户端往返耗时、请求次数。
  - 不自动重试，重试由界面决定。

### 超时（`TimeoutPolicy`，纯函数）

候选路线：`source` 明确时为该语向；`auto` 时为 {zh, en, ja} 去掉 `target` 后到 `target` 的全部**可用**语向。按顺序取第一条命中的规则：

| 条件 | 超时 |
|------|------|
| 任一候选路线的模型不在 `loaded_models`（会懒加载），或 `/languages`、`/health` 未知 | 10000 ms（且不低于下面第 3 行对同一文本的值） |
| 文本 ≤ 120 字符、不含换行，且候选路线全部直连 | 1500 ms |
| 其他（段落、英文中转） | 3000 ms；超过 2000 字符后每字符 +1 ms，封顶 15000 ms |

字符数按 Unicode 码位计（与服务端 Python `len` 一致）。`/health`、`/languages` 固定 2000 ms。

### 错误

失败统一抛 `EngineException`，按 `Kind`（`EngineErrorKind`）处理，界面显示 `UserMessage`；技术细节在 `Message`，只写日志。调用方自己取消时抛 `OperationCanceledException`，不是 `EngineException`。

| Kind | 来源 | UserMessage |
|------|------|-------------|
| `Unavailable` | 连接被拒绝、连接中断 | 翻译服务未运行或已退出 |
| `Timeout` | 超过 `TimeoutPolicy` 的时间（区别于调用方取消） | 翻译超时（N 毫秒），请重试 |
| `UnsupportedPair` | `unsupported_pair`，带 `MissingModels`、`SourceLanguage`、`TargetLanguage` | 未安装语向模型：opus-mt-en-zh（无缺失模型时：不支持该语向：ko→zh） |
| `TextTooLong` | `text_too_long`，带 `Limit`、`Length` | 文本过长：N 字，上限 M 字 |
| `DetectFailed` | `detect_failed` | 无法识别原文语种，请手动指定 |
| `InvalidRequest` | `invalid_request` | 翻译请求无效 |
| `Internal` | `internal_error`，或 5xx 且正文不是错误信封 / 错误码未知 | 翻译服务内部错误 |
| `Unknown` | 框架 404 等非信封 4xx、未知 4xx 错误码、200 但 JSON 无法解析 | 翻译服务返回了无法识别的响应 |

### 联调测试

`tests/Suiyi.Core.Tests/Engine/EngineLiveTests.cs` 标记 `[Trait("Category", "Engine")]`，默认跳过。先启动引擎，再设置端口运行：

```bash
python -m suiyi_engine serve --preload zh-en,en-zh
SUIYI_ENGINE_PORT=18780 dotnet test client/Suiyi.sln -c Release --filter Category=Engine
```

## 开发模式下的翻译服务

客户端启动时由 `EngineSupervisor`（`Suiyi.Core/Engine`，#32）拉起并常驻翻译服务，退出时结束它。M2 仍从源码运行，需要能找到装了 `suiyi_engine` 的 Python。

**查找顺序**（`EngineCommandResolver`，命中即用）：

1. 设置 `engine.command` + `engine.args`（为 M4 打包 exe 预留）：`<command> <args…> serve --port … --preload …`
2. 设置 `engine.pythonPath`
3. 从客户端 exe 所在目录向上找仓库根（含 `engine/pyproject.toml`），用 `<仓库根>\.venv\Scripts\python.exe`
4. `py -3.11`（Windows Python Launcher），再退到 PATH 上的 `python`

Python 参数：`-m suiyi_engine serve --port <engine.port，默认 18780> --preload <engine.preload，默认 zh-en,en-zh> [--models-dir <engine.modelsDir>]`；工作目录为仓库根（找不到时为 exe 所在目录）。进程以 `CreateNoWindow` 启动，不弹控制台，并加入 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Job Object（`Suiyi.App/Interop/JobObject.cs`），客户端被任务管理器强杀时服务随之退出。

设置（#27）接入前，可用环境变量代替：`SUIYI_ENGINE_PORT`、`SUIYI_ENGINE_PRELOAD`、`SUIYI_ENGINE_PYTHON`、`SUIYI_ENGINE_MODELS_DIR`、`SUIYI_ENGINE_COMMAND`。

**状态**（`StateChanged` 事件，在线程池线程上触发，界面自行切回 UI 线程）：

```
Stopped → Starting ─┬→ Ready(External)   端口上已有随译服务（/health 为 ok）：直接复用，退出时不结束它
                    ├→ Ready(Managed)    自己拉起：每 200 ms 探测 /health，ok 即就绪（预加载在监听前完成）
                    └→ Failed(原因)      30 s 未就绪 / 就绪前退出 / 找不到 Python：不自动重试
Ready(Managed) → Restarting → Ready      进程意外退出或看门狗连续 3 次失败：退避 1 s / 2 s / 4 s 后重启
             → Failed(服务反复崩溃)       5 分钟内崩溃超过 3 次
```

- 就绪后在后台发一条 zh→en 短句预热并丢弃结果，失败不影响状态。
- 看门狗：`Ready` 期间每 10 s 探测 `/health`，连续 3 次失败且进程仍在 → 结束进程并按崩溃处理。外部服务连续 3 次失败 → 改为自己拉起。
- `RestartAsync()`（托盘「重启翻译服务」）结束托管进程并清零崩溃计数；`StopAsync()` 用 `Kill(entireProcessTree: true)` 结束托管进程，最多等 2 s；外部服务不动。
- 所有探测都用 #28 的 `EngineClient.GetHealthAsync`。

**日志**：服务的 stdout / stderr 写入 `%LOCALAPPDATA%\suiyi\logs\engine-YYYYMMDD.log`（stderr 行带 `[stderr]` 前缀），状态变化写客户端日志 `client-YYYYMMDD.log`。内存里保留最后 50 行，用于失败提示。

**手动起服务让客户端复用**（调试服务端时常用）：

```powershell
.venv\Scripts\python -m suiyi_engine serve --preload zh-en,en-zh
dotnet run --project client/src/Suiyi.App    # 状态为 Ready(External)，退出客户端后服务仍在
```

**常见失败提示**：

| 提示 | 原因与处理 |
|------|-----------|
| 未找到 suiyi_engine：请在仓库根目录创建 .venv 并执行 pip install -e engine | 解释器里没装引擎 |
| 缺少模型：opus-mt-xx-yy，请按 docs/engine/模型目录约定.md 转换 | `engine.preload` 的语向没有模型 |
| 端口 18780 被其他程序占用，请在设置中修改 engine.port | 端口上不是随译服务 |
| 未找到 Python：… / 无法启动 Python：… | 查找顺序都没命中，或 `engine.pythonPath` 写错 |
| 启动超时：翻译服务 30 秒内未就绪，已结束进程 | 冷盘或模型过大；看 engine 日志 |
| 服务反复崩溃（5 分钟内 4 次），已停止自动重启 | 看 engine 日志；修复后用「重启翻译服务」 |

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
dotnet run --project client/src/Suiyi.App            # 常驻托盘，右键菜单「退出」结束
```

生成的可执行文件是 `client/src/Suiyi.App/bin/<配置>/net8.0-windows/Suiyi.exe`。

## CI

`.github/workflows/ci.yml` 的 `client（windows-latest）` job 在 `client/**` 或工作流有变更时运行：`dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build -c Release` → `dotnet test -c Release`。汇总检查「client 检查」与「engine 检查」对称：路径有变更时 client job 必须成功，没有变更时必须被跳过。
