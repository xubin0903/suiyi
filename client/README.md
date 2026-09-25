# client/

C# **.NET 8** WPF 客户端。一期只做 **Windows**：托盘常驻、全局热键、剪贴板翻译，以及快捷键裁剪后的 OCR 翻译（M3）。翻译本身调用 `engine/` 的本机 HTTP 服务（见 [HTTP API](../docs/engine/HTTP-API.md)），不在客户端内嵌模型。

在 Windows 上从源码跑起来并逐项验收，见 [M2 实机测试](../docs/client/M2-实机测试.md)；升级到 M3 框选翻译并验收见 [M3 实机测试](../docs/client/M3-实机测试.md)。

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
  | `Settings/` | 设置模型、校验、原子读写（已有） |
  | `Clipboard/` | 剪贴板监听、过滤规则、自身写入抑制（已有） |
  | `Popup/` | 浮窗 ViewModel、状态机、位置计算（已有） |
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

**菜单：** 状态行（灰）、翻译剪贴板、框选翻译（Ctrl+Alt+S）、暂停监听 ✓、目标语言 ▸ 中文 / English / 日本語、重启翻译服务、打开设置文件、打开日志目录、关于、退出。左键单击发 `ShowLastPopupRequested`。「框选翻译」发 `TranslateRegionRequested`，括号内是 `SetRegionHotkey` 设入的实际生效快捷键（禁用或注册失败时只显示「框选翻译」）。

**当前接线（`App.xaml.cs`）：** 暂停监听 → `ClipboardMonitor.Paused` 并写回 `clipboard.monitorEnabled`；快捷键注册失败 → 托盘气泡；翻译服务状态由 `EngineSupervisor`（#32）驱动：Starting / Restarting → 正在准备，Ready → 就绪，Failed → 异常并弹气泡（崩溃重启时也弹），映射见 `Tray/EngineTrayStatus`；「重启翻译服务」调用 `EngineSupervisor.RestartAsync()`；目标语言写回 `primaryTarget`（启动时从设置恢复，每次翻译时读取设置）；「翻译剪贴板」→ `TranslateFlowCoordinator.TranslateClipboard`（见[主流程](#主流程)）；「框选翻译」→ `TranslateFlowCoordinator.TranslateRegionAsync(Tray)`，与框选快捷键同一路径（见[框选翻译](#框选翻译)）；左键单击 → `PopupViewModel.ShowLast()` 重新显示上一次浮窗（从未显示过时只写日志）。打开设置文件：用记事本打开 `SettingsStore.FilePath`，文件不存在时先写出默认设置。

**手测：**

```powershell
dotnet run --project client/src/Suiyi.App -- --tray-demo   # 每 2 秒轮换四种状态
dotnet run --project client/src/Suiyi.App -- --hotkey "Ctrl+Shift+Y"
```

再启动一次会看到「随译已在运行」气泡，第二个进程立即退出。

**DPI：** `Suiyi.App/app.manifest` 声明 PerMonitorV2（回退 `true/pm`）。WinForms 分析器的 WFAC010 建议改用 `Application.SetHighDpiMode`，但这是 WPF 应用，只能用 manifest，已在 csproj 中忽略。

## 主流程

`Flow/TranslateFlowCoordinator`（Core，不依赖 WPF，#34）把「捕获文本 → 翻译 → 浮窗」串起来，只在 UI 线程调用，后台回调经构造参数 `dispatch` 切回 UI 线程。

**组合根顺序（`App.OnStartup`）：** 加载设置 → 托盘（正在准备）→ `EngineSupervisor.Start()` → 编排器（订阅监听 / 快捷键 / 托盘 / 浮窗事件）→ 注册快捷键、开始剪贴板监听。**退出（`OnExit`）：** 注销快捷键 → 停止监听 → 释放编排器、关闭浮窗 → 停止服务（最多等 3 s）→ 隐藏托盘。

**公开接口：**

| 成员 | 说明 |
|---|---|
| `OnTextCaptured(text, trigger, timestamp)` | 监听 / 快捷键捕获到文本；`timestamp` 为触发时刻（`TimeProvider.GetTimestamp()`），用于端到端计时 |
| `OnTextRejected(reason, length, trigger)` | 只处理 `TooLong`：监听来源每次运行只在托盘提示一次；快捷键 / 托盘来源浮窗显示「文本过长：N 字，上限 10000 字」 |
| `TranslateClipboard(ClipboardReadResult)` | 托盘「翻译剪贴板」：按 `ManualFilter`（1–10000 字，不去重）过滤，读不到 / 隐私 / 无文字时托盘提示 |
| `Retry()` / `TranslateWithSource(lang)` | 浮窗「重试」/ 语种标签；已自动订阅 `PopupViewModel.RetryRequested`、`SourceLanguageOverride` |
| `Latency`（`LatencyStats`） | 最近 20 次热路径端到端耗时，`Percentile(p)`、`Summary()`（「关于」里显示） |
| `IsWaitingForEngine` / `Completed` | 是否在等服务就绪；一次翻译显示完成（含 `EndToEnd`、`WaitedForEngine`） |

**行为：**

- **目标语言：** `TranslationService` 每次请求读取 `settings.primaryTarget` / `secondaryTarget`，检测到原文等于主目标时改译为次目标；托盘切换目标后下一次翻译即生效。
- **服务未就绪**（`IEngineStatus.State` 为 Starting / Restarting / Stopped）：浮窗「正在准备翻译服务…」（不自动消失）。只要浮窗没关，服务就绪后自动补译最后一次请求；新请求替换等待中的旧请求，用户关闭浮窗则取消。最多等 `TranslateFlowOptions.ReadyWaitTimeout`（默认 30 s），超时显示「翻译服务启动超时，点「重试」会重启翻译服务」。服务变为 **Failed** 时立即显示「翻译服务启动失败，点「重试」会重启翻译服务」（监管器因启动超时而失败时显示前一条）。浮窗不会一直停在「正在准备」（#50）。
- **重试时重启服务：** 服务 Failed、上一次是「服务未运行」，或上一次是启动超时且服务仍未就绪时，点「重试」调用 `IEngineStatus.RestartAsync()`（与托盘「重启翻译服务」同一条路径），再按上面的规则等待就绪、自动补译。`EngineSupervisor` 把一次重启算到服务再次进入 Ready / Failed / Stopped 为止，期间再点重试或托盘重启都被忽略，不会叠加；编排器在重启后的就绪事件到达前，新请求一律等待（服务可能仍短暂报告 Ready）。其他错误的重试只重发请求。
- **连接被拒**（`EngineErrorKind.Unavailable`，多半服务刚退出）：调用 `IEngineStatus.RequestHealthCheck()` 让看门狗立即探测，浮窗显示「正在准备」。状态转为 Starting / Restarting 则按上面的 30 s 就绪等待处理，恢复后自动重译；`TranslateFlowOptions.UnavailableConfirmTimeout`（默认 5 s）内服务仍自称就绪，则报「翻译服务未运行或已退出，点「重试」会重启翻译服务」。同一请求的多次就绪等待共用一个 30 s 时限（从第一次等待算起）。
- **最新优先：** 新请求取消旧请求（`CancellationToken`），并用代次号丢弃旧请求晚到的结果或错误。用户关闭浮窗也会取消进行中的请求。
- **暂停监听：** 忽略 `ClipboardTrigger.Monitor`，快捷键和托盘照常翻译。

**错误映射（`PopupErrorMapper`）：**

| `EngineErrorKind` | 浮窗 |
|---|---|
| `Unavailable` | 先催健康检查并观察（见上）；确认后为 `ServiceUnavailable`「翻译服务未运行或已退出，点「重试」会重启翻译服务」 |
| `Timeout` | `Timeout`「翻译超时，请重试」 |
| `UnsupportedPair` | `MissingModels`（带缺失模型 id） |
| `TextTooLong` | `TextTooLong`（带 `Limit` / `Length`，不可重试） |
| `DetectFailed` | `DetectFailed`「无法识别原文语种，请点击语种标签手动指定」 |
| `InvalidRequest` / `Internal` / `Unknown` / 其他异常 | `Other`，文案为 `EngineException.UserMessage` |
| 等待服务就绪超时 | `EngineStartTimeout`「翻译服务启动超时，点「重试」会重启翻译服务」 |
| 服务 Failed（启动超时，`EngineFailureReason.StartupTimeout`） | 同「等待服务就绪超时」 |
| 服务 Failed（其他原因） | `ServiceUnavailable`，文案 `PopupErrorMapper.EngineFailedMessage` |

**端到端计时：** 从 `WM_CLIPBOARDUPDATE`（监听）或快捷键按下，到浮窗显示译文后 WPF 完成布局（`Dispatcher` 的 `Loaded` 优先级回调）。每次写一行日志，不含正文：

```
翻译完成：trigger=Monitor chars=12 en→zh route=opus-mt-en-zh requests=1 e2e_ms=380 http_ms=210 server_ms=180；最近 10 次：P50 350 ms，P95 420 ms
```

等待过服务就绪的请求会标注「含等待服务就绪，不计入统计」。验收（P95 ≤ 2000 ms）：服务就绪后连续翻译 10 次，看最后一行日志或托盘「关于」里的 P95。

## 框选翻译

M3 主流程（#58）：框选快捷键 `hotkey.region` 或托盘「框选翻译」→ [框选截屏](#框选截屏)（#55）→ `IOcrTranslationService`（#56，`/ocr_translate`）→ `OcrResultMapper` → 浮窗围绕选区显示（#57）。编排仍在 `TranslateFlowCoordinator`（`TranslateFlowCoordinator.Region.cs`），与复制翻译共用服务等待、超时、重试重启、最新优先等全部逻辑。构造参数新增可选的 `ocr`、`region`；不传则框选翻译不可用（`IsRegionTranslateEnabled == false`，快捷键只截图）。

```
TranslateRegionAsync(trigger)
  → 遮罩已显示？忽略（RegionCaptureTrigger 同样忽略）
  → 取消进行中的请求（复制翻译或上一次框选），隐藏浮窗
  → IRegionCapture：Esc / 右键取消 → 结束，不弹浮窗；截屏异常 → 托盘提示「框选截屏失败，请重试（详情见日志）」
  → 服务未就绪？浮窗「正在准备翻译服务…」（围绕选区），就绪后自动继续
  → ShowOcrLoading(选区)「正在识别并翻译…」→ TranslateImageAsync(PNG)
  → ShowOcrResult（空结果为「未识别到文字」）| ShowError（Mode = Ocr，锚点仍为选区）
```

**新增公开接口：**

| 成员 | 说明 |
|---|---|
| `TranslateRegionAsync(RegionTranslateTrigger trigger, CancellationToken)` | `Hotkey` / `Tray`；只在 UI 线程调用 |
| `IsRegionTranslateEnabled` / `HasRetainedScreenshot` / `RetainedScreenshotBytes` | 是否接了 OCR 与框选；是否保留着截图（最多一张，语义见下方「截图生命周期」）及其字节数 |
| `OcrLatency`（`LatencyStats`） | 最近 20 次框选 `ocr_e2e_ms`，「关于」里显示 |
| `RegionCompleted`（`RegionTranslateCompletedEventArgs`） | 一次框选翻译显示完成（`Trigger`、`Outcome`、`Result`、`EndToEnd`、`WaitedForEngine`） |
| `RegionCaptureTrigger.Failed`（`RegionCaptureFailedEventArgs`） | 截屏异常（与用户取消区分） |
| `ITrayService.TranslateRegionRequested` / `SetRegionHotkey(string?)`、`TrayCommand.TranslateRegion`、`TrayState.RegionHotkey` / `RegionMenuText` | 托盘菜单项 |
| `PopupViewModel.ShowError(error, mode, anchor)` | 可选参数：编排器按请求类型指定 `Mode` 与选区锚点 |
| `PopupViewModel.SetRetryAvailability(text, ocr)` | 两种来源是否有可重发的请求；`CanRetry` 按当前 `Mode` 取值（默认都可用） |
| `PopupOcrResult.UntranslatedParagraphs` / `HasUntranslated` / `IsUntranslated(i)`；`PopupViewModel.UntranslatedHint` / `HasUntranslatedHint` | 译文缺失、以原文代替的段落 |

**行为：**

- **最新优先、互相取消：** 文本请求和框选请求共用一个代次号与 `CancellationTokenSource`，谁后来谁生效，旧请求晚到的结果被丢弃，不会互相覆盖。开始框选即取消进行中的请求并隐藏浮窗（截图里不会有随译浮窗）；遮罩显示期间忽略剪贴板监听、翻译快捷键、托盘「翻译剪贴板」。
- **重试按 `PopupViewModel.Mode` 分流：** `Ocr` 用原来那张 PNG 重发（不重新框选）；`Text` 走原来的文本重试。服务 Failed / 未运行 / 启动超时后的重试同样会先 `RestartAsync()`。
- **截图生命周期：** PNG 只在内存，内存里最多一张：跟着对应浮窗的内容一直保留（识别成功、失败、浮窗关闭后都还在），直到被下一次框选**成功拿到的**新截图替换（框选途中按 Esc 取消不影响旧截图）。托盘左键重新显示旧的框选错误浮窗时，点「重试」仍用这张 PNG 重发。浮窗改为显示复制翻译的内容（新的文本请求、「文本过长」提示等，即 `Mode` 变为 `Text`）时旧的框选内容不会再显示，截图随即丢弃；被忽略的文本捕获（暂停监听、遮罩期间）不影响。只丢引用，不主动清零。`--region-demo` 另存一份到临时目录，正式流程不落盘。
- **「重试」不会点了没反应：** 编排器通过 `PopupViewModel.SetRetryAvailability(text, ocr)` 告知两种来源是否还有可重发的请求，`CanRetry` 按当前 `Mode` 取值，没有截图（或没有上一次文本）时隐藏「重试」。
- **关闭浮窗即取消**进行中或等待服务的框选请求。
- **暂停监听不影响框选**（Issue 要求）。
- **目标语言：** 与复制翻译一样读 `primaryTarget` / `secondaryTarget`，识别出的主要语种等于主目标时由 `OcrTranslationService` 改译为次目标。
- **错误：** OCR 错误码经 `PopupErrorMapper` → `OcrResultMapper.MapError`：`image_too_large`「选区过大…」（不可重试）、`invalid_image` / `unsupported_media_type`「截图无法识别，请重新框选」、`ocr_unavailable`「OCR 模型未安装：…」+ 第二行修复方法（可重试，不触发重启，见下）；`Unavailable` / 超时 / 服务 Failed 与复制翻译一致（催健康检查、30 s 就绪等待、重试重启）。
- **OCR 不可用（`ocr_unavailable`）与 `/health.ocr_error`：** 原因取 503 `details.reason`，缺时取最近一次 `/health.ocr_error.reason`（`EngineClient.KnownOcrError`，由监管器就绪探测与看门狗的 `/health` 刷新，成功识别或 `Invalidate()` 后清空；缺失模型列表同理补齐）。浮窗第一行说明问题、第二行（`PopupError.Hint`）给命令：`models_missing` / 未知原因「OCR 模型未安装：…」+「请在随译仓库根目录运行 `python scripts\download_ocr_models.py download` 下载，完成后点「重试」」（服务端不缓存失败，补齐模型后重试即可，不用重启）；`models_invalid`「OCR 模型文件不完整或已损坏」+ 同一条命令重新下载（脚本会重下校验不过的文件）；`dependency_missing`「OCR 组件未安装」+「运行 `pip install -e "engine[ocr]"`，然后在托盘点「重启翻译服务」」；`manifest_unavailable`「OCR 模型清单不可用」+「请更新随译源码（git pull）后重启随译」。服务端 `message` 含服务端路径，只写日志不显示。服务就绪时若 `ocr_error` 非空，编排器写一行 Warning（只记 `reason` 与模型 id）。复制翻译的错误不受 `ocr_error` 影响。
- **未翻译段落：** 服务某段 `translation` 为空时用原文代替，`PopupOcrResult.UntranslatedParagraphs` 记下标，浮窗译文下方显示灰色小字「第 2、3 段未能翻译，显示为原文」（全部未翻译时「未能翻译，以上为识别出的原文」），复制内容不含提示。
- **隐私：** 日志不记识别出的原文和译文，也不记服务端错误说明；只记尺寸、字节数、段落数、语种、耗时、错误类别。

**计时：** `ocr_e2e_ms` 从拿到 PNG（遮罩松开鼠标、截图编码完成）开始，到浮窗显示结果后 WPF 完成布局；用户拖拽选区的时间不计入。每次一行日志：

```
框选翻译完成：trigger=Hotkey size=640x240 bytes=48213 paragraphs=2 empty=False untranslated=0 en→zh ocr_e2e_ms=820 http_ms=760 server_ocr_ms=410 server_translate_ms=300 server_total_ms=720；框选最近 10 次：P50 800 ms，P95 900 ms
```

验收（P95 ≤ 2500 ms，热路径，OCR 已预热）：服务就绪后对 ≤ 1280×720 的段落区域连续框选 10 次，看最后一行日志或托盘「关于」里的「框选翻译延迟」。

## 设置文件

`Suiyi.Core/Settings`（#27）。路径 `%APPDATA%\suiyi\settings.json`（漫游配置），环境变量 `SUIYI_CONFIG_DIR` 可覆盖目录（测试、便携模式）；路径规则只在 `SettingsPaths` 一处。

默认内容（UTF-8 无 BOM、两空格缩进、camelCase；托盘「打开设置文件」在文件不存在时会写出这份）：

```json
{
  "schemaVersion": 2,
  "primaryTarget": "zh",
  "secondaryTarget": "en",
  "clipboard": {
    "monitorEnabled": true,
    "debounceMs": 150,
    "minChars": 2,
    "maxChars": 2000
  },
  "hotkey": {
    "translate": "Ctrl+Alt+T",
    "region": "Ctrl+Alt+S"
  },
  "popup": {
    "autoHideSeconds": 8,
    "maxWidth": 480
  },
  "engine": {
    "port": 18780,
    "pythonPath": null,
    "command": null,
    "args": null,
    "modelsDir": null,
    "preload": "zh-en,en-zh",
    "preloadOcr": true
  },
  "startWithWindows": false
}
```

| 字段 | 默认 | 取值 / 说明 |
|------|------|-------------|
| `schemaVersion` | `2` | 设置文件版本。1 → 2 新增 `hotkey.region`（#55），见下方「版本升级」 |
| `primaryTarget` | `"zh"` | 目标语言 `zh` / `en` / `ja`；托盘「目标语言」写回这里 |
| `secondaryTarget` | `"en"` | 检测到的原文语种等于 `primaryTarget` 时改译为它；与 `primaryTarget` 相同时自动取 `primary == "zh" ? "en" : "zh"`（`SettingsRules.ResolveTargets`） |
| `clipboard.monitorEnabled` | `true` | 是否监听剪贴板；托盘「暂停监听」写回这里 |
| `clipboard.debounceMs` | `150` | 50–1000 |
| `clipboard.minChars` / `maxChars` | `2` / `2000` | 1–10000，且 `minChars ≤ maxChars`（否则两者都回落默认值） |
| `hotkey.translate` | `"Ctrl+Alt+T"` | 快捷键字符串，`""` 表示禁用；格式由 `HotkeyParser` 解析，这里不校验 |
| `hotkey.region` | `"Ctrl+Alt+S"` | 框选截屏快捷键（#55），`""` 表示禁用。读取时即用 `HotkeyParser` 校验：格式非法或类型不对回落默认值；与 `hotkey.translate` 相同（含回落后）时禁用并写 Warning，避免两个功能抢同一组合；此时启动后托盘弹一次气泡「框选快捷键 Ctrl+Alt+S 与翻译快捷键相同，框选快捷键已禁用。请在设置文件中把 hotkey.region 改为其他组合…」（`SettingsStore.TakeLoadNotices`，每次运行一次） |
| `popup.autoHideSeconds` | `8` | 0–60，0 表示不自动消失 |
| `popup.maxWidth` | `480` | 240–1920（设备无关像素） |
| `engine.port` | `18780` | 1–65535 |
| `engine.pythonPath` | `null` | 不填则按进程管理（#32）的查找顺序 |
| `engine.command` / `engine.args` | `null` | 高级：直接指定服务可执行文件与参数数组（为 M4 打包 exe 预留） |
| `engine.modelsDir` | `null` | 不填则不传 `--models-dir` |
| `engine.preload` | `"zh-en,en-zh"` | `""` 表示不预加载 |
| `engine.preloadOcr` | `true` | 启动时预热 OCR 模型（#58），`true` 时追加 `--preload-ocr`（#53）；环境变量 `SUIYI_ENGINE_PRELOAD_OCR=1/0` 可覆盖。缺 OCR 依赖或模型时服务只告警、照常启动，文本翻译不受影响，`/health.ocr_error` 带上原因（见[框选翻译](#框选翻译)的错误说明）。关掉可省内存（OCR 模型加载后服务内存增加，见 #54），代价是首次框选多一次 OCR 冷加载（超时按 `TimeoutPolicy` 放宽到 30 s） |
| `startWithWindows` | `false` | 预留，M2 不实现 |

**读取规则：**

- 文件不存在：使用默认值，不落盘；首次写入时创建目录。
- 允许 `//` 注释和尾逗号；未知字段忽略；缺失字段取默认值；属性名大小写不敏感。
- 单个字段类型不对或越界（如 `"primaryTarget": "fr"`、`"debounceMs": -1`）：只有该字段回落默认值，写一条 Warning 日志，其余字段保留；读取时不改写文件。
- 不是合法 JSON，或 `schemaVersion` 高于当前版本：原文件改名为 `settings.json.bad-yyyyMMddHHmmss`（重名时加 `-1`、`-2`），使用默认值并写日志，不崩溃。

**版本升级：** 读到低于当前版本的文件（例如 M2 写出的 `schemaVersion: 1`）时，缺失的新字段取默认值，写一条 Info 日志「设置文件版本 1 升级到 2：新增 hotkey.region…」；读取时同样不改写文件，托盘下一次写回时才以版本 2 落盘（其他字段原样保留）。M2 用户若已把 `hotkey.translate` 设成 `Ctrl+Alt+S`，升级后框选快捷键为禁用。

**写入：** 先写同目录临时文件并刷盘，再 `File.Replace` / `File.Move` 替换，写到一半失败不会破坏旧文件。`SettingsStore.Update(Func<AppSettings, AppSettings>)` 线程安全：校验 → 值有变化才写盘并触发 `Changed`（`OldSettings` / `NewSettings`）；写盘失败只记日志，内存中的值照样生效。

**生效时机：** M2 不做热重载，手工编辑后需**重启**生效。托盘改设置写回前，如果发现文件被外部修改过，会先重新读取再应用这次修改，不会覆盖手工编辑的内容（但其他字段仍要重启才生效）。

**当前接线（`App.xaml.cs`）：** 启动时 `Load()`，用 `clipboard.*` 构造监听器（含暂停状态），用 `hotkey.translate`、`hotkey.region` 注册两个快捷键，用 `popup.*` 构造浮窗，用 `engine.*`（`EngineSettings.ToEngineOptions()`，再叠加 `SUIYI_ENGINE_*` 环境变量）启动翻译服务进程（#32），用 `primaryTarget`、`monitorEnabled` 初始化托盘；托盘「暂停监听」「目标语言」通过 `Update` 写回。

## 译文浮窗

光标旁的轻量浮窗（#31）。状态机、计时、位置计算在 `Suiyi.Core/Popup`（注入 `TimeProvider` 单测），窗口与 Win32 在 `Suiyi.App/Popup`、`Suiyi.App/Interop/PopupNativeMethods`。

| 类型 | 位置 | 作用 |
|------|------|------|
| `PopupViewModel` | Core | 集成方调用 `ShowPreparing()`、`ShowLoading(sourceText)`、`ShowResult(PopupResult)`、`ShowError(PopupError)`、`ShowLast()`、`Close(reason)`；事件 `CopyTranslationRequested(Text)`、`RetryRequested`、`SourceLanguageOverride(Language)`、`Closed(Reason)`；窗口用 `Shown(Reposition)`、`PropertyChanged`、`TogglePin()`、`SetHovered()`、`RequestCopy()`、`RequestRetry()`、`RequestSourceOverride()` |
| `PopupKind` | Core | `None` / `Preparing` / `Loading` / `Result` / `Error` |
| `PopupResult` | Core | `Translation`、`Source`、`Target`、`SourceDetected`、`Elapsed` |
| `PopupOcrResult` | Core | 框选翻译结果（#57）：`SourceParagraphs` / `TranslationParagraphs`（一一对应）、`Source`、`Target`、`SourceDetected`、`Elapsed`；`SourceText` / `TranslationText`（段落间空一行）、`IsEmpty`、`Empty(target)` |
| `PopupPlacement.CalculateAroundRect` | Core | 以选区为锚点：右下外侧 → 下方 → 上方 → 左侧 → 都放不下时压住选区（下 / 上空间大的一侧），最后夹紧到工作区；返回位置与 `RectPlacementSide` |
| `OcrDraftContract` / `OcrTranslateResponse` / `OcrErrorCodes` | Core（`Ocr/`） | 按 #53 草案实现、已与定稿核对一致的 `/ocr_translate` 响应 DTO、错误码与解析 |
| `Flow/OcrResultMapper` | Core | 草案响应 → `PopupOcrResult`、草案错误码 → `PopupError`。与上一行、`Engine/EngineClient.Ocr.cs`、`HealthResponse.OcrLoaded` 是客户端里依赖草案的全部地方（见「OCR 调用」），接口变化时同步改这几处 |
| `PopupError` / `PopupErrorKind` | Core | `ServiceUnavailable`、`Timeout`、`MissingModels`（`MissingModels` 列表）、`DetectFailed`、`TextTooLong`（`Limit`/`Length`）、`Other`（`Detail`）、`EngineStartTimeout`（等服务就绪超时）；`Message` 为中文短提示，`CanRetry`（文本过长为否）。不依赖 `EngineException`，由 `Flow/PopupErrorMapper` 映射 |
| `PopupOptions` | Core | `MaxWidth` 480、`AutoHideSeconds` 8（0 不消失）、`CursorOffset` 16、`LoadingIndicatorDelay` 300 ms、`CopiedFeedbackDuration` 1 s |
| `PopupPlacement.Calculate` | Core | 光标点 + 窗口尺寸 + 工作区 → 左上角（物理像素，支持负坐标）：右下偏移，放不下翻到左 / 上，再夹紧 |
| `PopupText` | Core | 语种标签（`中文 → English`、`English（自动） → 中文`）、耗时、原文摘要、按语种的字体回退链 |
| `PopupDemo` | Core | `--popup-demo` 步骤（后半段为框选翻译：草案 JSON 经映射后显示，含展开原文、长文本、部分段落未翻译、空结果、OCR 错误，选区锚点为固定坐标；演示里没有可重发的请求，错误步骤不显示「重试」） |
| `PopupWindow` / `PopupTheme` | App | 无边框圆角阴影、置顶、不进任务栏；`WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`；`MonitorFromPoint` + `GetMonitorInfo` 取工作区；跟随 `AppsUseLightTheme` |

**行为：**

- **不抢焦点：** 未钉住时带 `WS_EX_NOACTIVATE`，`ShowActivated=false`。钉住后去掉该样式，可以激活窗口、选中文字、拖动标题栏。
- **Loading：** 浮窗原本隐藏时，整个窗口在 300 ms 后才出现；结果先到就直接显示结果，不闪加载态。浮窗已显示时立即更新原文摘要，300 ms 后出现进度条。
- **自动消失：** Result / Error 显示后 8 s 消失（Preparing、Loading 不计时：它们由主流程在时限内换成结果或错误）；悬停时暂停，移出后重新计满 8 s；钉住时不消失，取消钉住后重新计时；新内容会重新计时。
- **位置：** 每次新翻译（且未钉住）移到当前光标旁；钉住时原地更新内容。内容尺寸变化时按同一光标点重新夹紧。
- **关闭：** 点 ×；Esc 在浮窗获得焦点时（钉住后）生效，未钉住时仅在鼠标悬停于浮窗上期间临时注册全局 Esc（移出即注销，不影响在原应用里按 Esc）。点击浮窗外不关闭。关闭会取消钉住，内容保留，托盘左键可重新显示。
- **复制：** 发 `CopyTranslationRequested`，`App` 用 `ClipboardWriter.SetText` 写入（登记为自身写入，不触发监听），按钮显示「已复制」1 s。

**框选翻译结果（#57）：** 集成方（#58）调用 `ShowOcrLoading(selection)` →（`ShowOcrResult(result)` | `ShowError(error)`），`selection` 为选区（物理像素 `PopupRect`）。

- **加载：** 「正在识别并翻译…」，窗口延迟显示规则同复制翻译。
- **结果：** 译文为主体；下方「原文 ▸」默认折叠，点击展开 / 收起，本次浮窗内保持（复制、悬停、钉住、关闭后托盘重新显示都不变），新一次翻译重新折叠。标题栏有「复制译文」「复制原文」（`CopyOriginalRequested`，同样经 `ClipboardWriter` 写入，不自触发）。段落间空一行；译文加展开的原文超过最大高度（工作区一半）时整体滚动。
- **空结果：** `PopupKind.Empty`，灰色「未识别到文字」，非错误样式，无重试，照常自动消失。
- **错误：** `ImageTooLarge`「选区过大，请缩小选区后重新框选」（不显示重试）、`InvalidImage`「截图无法识别，请重新框选」、`OcrUnavailable`「OCR 模型未安装：…」；其余沿用复制翻译的提示。`Mode` 仍为 `Ocr`，集成方据此决定重试走框选流程。
- **语种标签：** 显示「中文（自动） → English」，取各段检测结果中最多的语种；框选翻译时**不可点击**（改原文语种需要重新识别，#58 之后再定）。
- **位置：** 放在选区旁（`CalculateAroundRect`，卡片与选区间距 8 DIP），夹紧到选区中心所在显示器的工作区；钉住时原地更新。
- **语种标签：** 点击后可选 中文 / English / 日本語，发 `SourceLanguageOverride`（`DetectFailed` 错误时显示为「指定原文语种 ▾」）。
- **字体：** 中文 `Microsoft YaHei UI` 优先，日文 `Yu Gothic UI` 优先，英文 `Segoe UI` 优先，彼此互为回退。

**当前接线：** 复制 → `ClipboardWriter.SuppressNext(1 s)` + `SetText`（不会被监听再次翻译，失败时撤销抑制并弹托盘提示）；托盘左键 → `ShowLast()`；重试、指定语种由 `TranslateFlowCoordinator` 订阅（见[主流程](#主流程)）。

**手测：**

```powershell
dotnet run --project client/src/Suiyi.App -- --popup-demo   # 每 3 秒一个状态，共一轮，停在最后一个错误态
```

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

**手测：** 目前 `Suiyi.App` 启动即开始监听，结果写日志（`剪贴板：接受 N 字` / `剪贴板：跳过（原因，N 字）` / `剪贴板：忽略自身写入`）。暂停 / 恢复用托盘菜单「暂停监听」（设置 `ClipboardMonitor.Paused`）。`TextCaptured` / `TextRejected` 接到 `TranslateFlowCoordinator`（见[主流程](#主流程)）；`ClipboardTextCapturedEventArgs.Timestamp` 是 `WM_CLIPBOARDUPDATE` 到达时刻，用于端到端计时。

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
| `HotkeyParser` | Core | `TryParse` / `Normalize`；`DefaultTranslate = "Ctrl+Alt+T"`、`DefaultRegion = "Ctrl+Alt+S"` |
| `HotkeyGesture` / `HotkeyModifiers` / `HotkeyKeys` | Core | 修饰键（取值同 `MOD_*`）+ 虚拟键码；`ToString()` 为规范写法 |
| `HotkeyManager` | Core | 管一个快捷键（翻译、框选各一个实例，`label` 分别为「快捷键」「框选快捷键」）：`Update(string)` 注册 / 重新注册 / 禁用；`Current`；事件 `Pressed`、`RegistrationFailed`（`Message` 为面向用户的提示，带 `label`） |
| `HotkeyTranslateAction` | Core | 取词流程 `ExecuteAsync()`；事件 `TextCaptured`、`Rejected`；上一次未结束时返回 `AlreadyRunning` |
| `IHotkeyRegistrar` / `Win32HotkeyRegistrar` | Core / App | `RegisterHotKey` 抽象与实现（自建仅消息窗口收 `WM_HOTKEY`；每个实例一个 id：翻译 `0x5359`、框选 `0x535A`） |
| `IKeyboardInput` / `Win32KeyboardInput` | Core / App | `GetAsyncKeyState` 查修饰键、`SendInput` 模拟 Ctrl+C |

**快捷键写法：** 修饰键 `Ctrl`（`Control`）、`Alt`、`Shift`、`Win`（`Windows`）加一个按键，用 `+` 连接，大小写与空格不敏感，例如 `Ctrl+Alt+T`、`Ctrl+Shift+F1`、`Win+Alt+Y`。按键可以是 `A`–`Z`、`0`–`9`、`F1`–`F24`、`NumPad0`–`NumPad9`、`Space`、`Enter`、`Tab`、`Esc`、`Backspace`、`Insert`、`Delete`、`Home`、`End`、`PageUp`、`PageDown`、方向键、`Pause`、`PrintScreen`。除 `F1`–`F24` 外必须带 `Ctrl`、`Alt` 或 `Win`（`Shift+T` 会打出大写字母，不允许）。空字符串表示禁用。

**修改：** 改 `settings.json` 的 `hotkey.translate` 后重启（启动时调用 `HotkeyManager.Update`）。命令行 `dotnet run --project client/src/Suiyi.App -- --hotkey "Ctrl+Shift+Y"` 可临时覆盖，不写回设置。

**被占用：** 注册失败时发 `RegistrationFailed`，提示「快捷键 Ctrl+Alt+T 已被其他程序占用，请在设置中修改」，程序继续运行；托盘弹气泡显示该提示并写日志。

**已知行为：**

- **不恢复剪贴板。** 选中文本时，模拟复制会用选中内容替换剪贴板原内容（恢复多格式内容复杂且容易丢数据，M2 不做）。
- **管理员窗口。** 目标窗口以管理员权限运行而随译没有时，`SendInput` 会被 UIPI 静默拦截，剪贴板不变，此时退化为翻译当前剪贴板，并写日志。
- 部分终端、远程桌面等程序对模拟 Ctrl+C 的处理不同，同样会退化为翻译当前剪贴板。

## 框选截屏

M3 框选翻译的入口（#55）：按 `hotkey.region`（默认 `Ctrl+Alt+S`）后出现全屏遮罩，拖拽选区，得到该区域的 PNG。截图交给[框选翻译](#框选翻译)（#58）识别并翻译，只在内存中，用完即丢弃，不落盘；日志只记尺寸、位置、显示器、缩放、耗时，不含图片。纯逻辑在 `Suiyi.Core/Capture`，WPF / Win32 在 `Suiyi.App/Capture`。

```
快捷键（RegionCaptureTrigger：遮罩显示中再按被忽略）
  → 隐藏随译浮窗（PopupWindow.HideForCapture）+ DwmFlush
  → EnumDisplayMonitors / GetDpiForMonitor：每台显示器的物理边界与有效 DPI
  → GDI BitBlt（SRCCOPY | CAPTUREBLT）逐台抓「冻结帧」（只在内存）
  → 每台显示器一个遮罩窗口（SetWindowPos 按物理像素铺满；显示冻结帧 + 50% 暗色，选区内还原并标注「宽 × 高」物理像素）
  → 拖拽（RegionSelection 状态机；坐标取 GetCursorPos 物理像素）
  → 松开：选区 ≥ 8×8 物理像素则完成，否则视为取消；Esc / 右键 / 切到其他程序 / 退出程序 → 取消
  → 关闭遮罩 → 从起始显示器的冻结帧裁出选区 → PNG（后台线程编码）→ 恢复浮窗
```

| 类型 | 所在 | 作用 |
|------|------|------|
| `IRegionCapture` | Core | `Task<RegionCaptureResult?> CaptureAsync(CancellationToken)`；取消返回 `null`；UI 线程调用 |
| `RegionCaptureResult` | Core | `Png`（`ReadOnlyMemory<byte>`，物理像素 1:1）、`Bounds`（`PixelRect`，虚拟桌面物理像素，可为负）、`Monitor`（`DisplayMonitor`）、`Scale`（1.0 / 1.25 / 1.5 / 2.0…） |
| `RegionCaptureTrigger` | Core | 快捷键入口：`RunAsync(ct)`、`IsCapturing`、事件 `Captured(Result, Elapsed)`；重复触发忽略；异常记日志按取消处理 |
| `RegionSelection` | Core | 状态机 `Idle → Dragging → Completed / Cancelled`：`Begin` / `Move` / `End` / `Cancel`、`Rect`、`Monitor`、`MinSize`（默认 8）；选区限定在起始显示器 |
| `ScreenCoordinates` | Core | 纯函数：`VirtualBounds`、`FindMonitor`（空隙取最近）、`PhysicalToLocalDip`、`LocalDipToPhysical`、`MonitorDipSize`、`ToFrameRegion` |
| `PixelPoint` / `PixelRect` / `DipPoint` / `DipRect` / `DisplayMonitor` | Core | 物理像素与 DIP 几何；`PixelRect.FromCorners` 与拖拽方向无关且包含两端像素 |
| `RegionCaptureDemo` | Core | `--region-demo` 的保存目录与文件名 |
| `Win32RegionCapture` / `RegionOverlayWindow` / `GdiScreenGrabber` / `MonitorEnumerator` | App | 上面流程的 Windows 实现；Win32 声明在 `Interop/ScreenCaptureNativeMethods` |

**坐标约定：** 屏幕坐标一律是物理像素的虚拟桌面坐标（主屏左上角为原点，副屏在左 / 上方时为负）。每台显示器一个遮罩窗口，窗口内 DIP 以该显示器左上角为原点：`物理 = 显示器原点 + DIP × 缩放`。混合 DPI 下不让 WPF 在多个显示器之间换算坐标，遮罩也按各自显示器的 DPI 渲染。

**跨显示器：** 选区**裁剪到起始显示器**（按下左键时所在的那台），不拼接。拼接要处理不同 DPI 的像素对齐，OCR 场景下收益很小。

**演示入口：** `dotnet run --project client/src/Suiyi.App -- --region-demo`：框选完成后把 PNG 存到 `%TEMP%\suiyi-region-demo\region-yyyyMMdd-HHmmss-fff-宽x高.png`，托盘气泡显示路径，用于逐像素比对。正式流程不保存。`--region-hotkey "Ctrl+Shift+F2"` 可临时覆盖框选快捷键（不写回设置）。

**已知限制：**

- 用 GDI `BitBlt`，不用 `Windows.Graphics.Capture`：受 DRM 保护的视频、部分硬件加速窗口在截图里可能是黑块。
- 遮罩拿不到前台（系统前台锁）时收不到键盘焦点；为此框选期间临时把 `Esc` 注册为全局快捷键兜底（注册失败则只能右键取消）。
- 遮罩显示期间如果显示器热插拔或改缩放，本次框选结果按抓帧时的布局计算；取消后重来即可。

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

### OCR 调用（#56，#53）

客户端最初按 Issue #53 草案实现，#53（PR #69）合入后的 `docs/engine/HTTP-API.md` 与草案的路径、参数、请求体、响应字段和错误码一致。相关代码集中在：`Ocr/OcrDraftContract.cs`（DTO、错误码、路径/参数名、上限常量）、`Engine/EngineClient.Ocr.cs`（请求构造、错误映射、预检）、`Flow/OcrResultMapper.cs`（→ 浮窗），外加 `HealthResponse.OcrLoaded`、`HealthResponse.OcrError`（`OcrHealthError`：`reason`、`missing_models`、`message`）两个字段。
- **`EngineClient.KnownOcrError`**：最近一次 `/health.ocr_error`；成功识别或 `Invalidate()` 后为 `null`。`IOcrTranslationService.KnownOcrError` 转发它（默认接口实现返回 `null`），编排器用它补全 `ocr_unavailable` 的原因。

- **`EngineClient.OcrTranslateAsync(png, source, target, fallbackTarget, ct)`** → `POST /ocr_translate?source=…&target=…[&fallback_target=…]`，请求体为**原始 PNG 字节**，`Content-Type: image/png`（草案不用 multipart / base64）。`fallbackTarget` 为空或与 `target` 同语种时不发。成功后记 OCR 已加载、各段 `route` 模型已加载。识别为空是正常结果（`paragraphs: []`）。
- **`EngineClient.OcrAsync(png, lang = "auto", ct)`** → `POST /ocr?lang=…`，只识别。
- **客户端预检**（`EngineClient.PrecheckImage`）：超过 8 MiB，或 PNG 头的宽×高超过 4096×4096 = 16 777 216 像素时，直接抛 `ImageTooLarge`（`IsClientPrecheck = true`，`Details` 与服务端 413 同形 `limit`/`actual`），不发请求。读不出 PNG 头时不判像素，交给服务端。
- **超时**：OCR 与候选翻译模型都已加载（`/health.ocr_loaded == true`）时 15000 ms，否则 30000 ms（旧引擎没有 `ocr_loaded` 也按 30000 ms）。见下文表格。
- **`OcrTranslationService`**（`IOcrTranslationService`）：`source=auto`、`target=主目标`、`fallback_target=次目标`，一次往返完成主/次目标规则；最新请求优先、`CancelCurrent()`；结果 `OcrTranslationOutcome`（响应、目标、次目标、是否改译、字节数、宽高、客户端耗时）。日志只记尺寸、字节数、段落数、耗时、错误码/状态码，不记识别文本和服务端错误说明。浮窗内容用 `OcrResultMapper.Map(outcome.Response, outcome.Target)`。主流程串接见 #58。

### 超时（`TimeoutPolicy`，纯函数）

候选路线：`source` 明确时为该语向；`auto` 时为 {zh, en, ja} 去掉 `target` 后到 `target` 的全部**可用**语向。按顺序取第一条命中的规则：

| 条件 | 超时 |
|------|------|
| 任一候选路线的模型不在 `loaded_models`（会懒加载），或 `/languages`、`/health` 未知 | 10000 ms（且不低于下面第 3 行对同一文本的值） |
| 文本 ≤ 120 字符、不含换行，且候选路线全部直连 | 1500 ms |
| 其他（段落、英文中转） | 3000 ms；超过 2000 字符后每字符 +1 ms，封顶 15000 ms |

字符数按 Unicode 码位计（与服务端 Python `len` 一致）。`/health`、`/languages` 固定 2000 ms。

OCR 请求（暂无 OCR 性能基线，等 #52 后按 P95 调整）：

| 条件 | 超时 |
|------|------|
| `/ocr_translate`：`ocr_loaded == true`，且 `source→target` 候选路线与 `target→fallback_target` 路线的模型都已加载 | 15000 ms（#56 建议值） |
| 其他（OCR 或翻译模型可能冷加载、`/health` / `/languages` 未知、旧引擎无 `ocr_loaded`） | 30000 ms（15000 + OCR 冷加载余量 + 翻译懒加载 10000） |
| `/ocr`：`ocr_loaded == true` / 其他 | 15000 / 30000 ms |

### 错误

失败统一抛 `EngineException`，按 `Kind`（`EngineErrorKind`）处理，界面显示 `UserMessage`；技术细节在 `Message`，只写日志；错误信封的 `details` 原样保留在 `Details`。浮窗侧 `PopupErrorMapper.Map` 对四个 OCR 错误类别转交 `OcrResultMapper.MapError(ErrorCode, Details)`，文案只维护一处。调用方自己取消时抛 `OperationCanceledException`，不是 `EngineException`。

| Kind | 来源 | UserMessage |
|------|------|-------------|
| `Unavailable` | 连接被拒绝、连接中断 | 翻译服务未运行或已退出 |
| `Timeout` | 超过 `TimeoutPolicy` 的时间（区别于调用方取消） | 翻译超时（N 毫秒），请重试 |
| `UnsupportedPair` | `unsupported_pair`，带 `MissingModels`、`SourceLanguage`、`TargetLanguage` | 未安装语向模型：opus-mt-en-zh（无缺失模型时：不支持该语向：ko→zh） |
| `TextTooLong` | `text_too_long`，带 `Limit`、`Length` | 文本过长：N 字，上限 M 字 |
| `DetectFailed` | `detect_failed` | 无法识别原文语种，请手动指定 |
| `ImageTooLarge` | 413 `image_too_large`（带 `Limit`=`details.limit`、`Length`=`details.actual`），或客户端预检拦截（`IsClientPrecheck`） | 选区过大，请缩小后重试 |
| `UnsupportedMediaType` | 415 `unsupported_media_type` | 截图格式不受支持 |
| `InvalidImage` | 422 `invalid_image` | 截图无法解码 |
| `OcrUnavailable` | 503 `ocr_unavailable`，带 `MissingModels`（`details.reason` 由 `OcrResultMapper` 读取） | OCR 模型未安装：ppocr-det（无列表时：OCR 模型未安装）；浮窗另加修复命令，见[框选翻译](#框选翻译) |
| `InvalidRequest` | `invalid_request` | 翻译请求无效 |
| `Internal` | `internal_error`，或 5xx 且正文不是错误信封 / 错误码未知 | 翻译服务内部错误 |
| `Unknown` | 框架 404 等非信封 4xx、未知 4xx 错误码、200 但 JSON 无法解析 | 翻译服务返回了无法识别的响应 |

### 联调测试

`tests/Suiyi.Core.Tests/Engine/EngineLiveTests.cs` 标记 `[Trait("Category", "Engine")]`，默认跳过。先启动引擎，再设置端口运行：

```bash
python -m suiyi_engine serve --preload zh-en,en-zh
SUIYI_ENGINE_PORT=18780 dotnet test client/Suiyi.sln -c Release --filter Category=Engine
```

OCR 联调（`EngineOcrLiveTests`，需 #53 服务端实现）另需 `SUIYI_ENGINE_OCR_PNG=<含中文的 PNG 路径>`，未设置时跳过。

## 开发模式下的翻译服务

客户端启动时由 `EngineSupervisor`（`Suiyi.Core/Engine`，#32）拉起并常驻翻译服务，退出时结束它。M2 仍从源码运行，需要能找到装了 `suiyi_engine` 的 Python。

**查找顺序**（`EngineCommandResolver`，命中即用）：

1. 设置 `engine.command` + `engine.args`（为 M4 打包 exe 预留）：`<command> <args…> serve --port … --preload …`
2. 设置 `engine.pythonPath`
3. 从客户端 exe 所在目录向上找仓库根（含 `engine/pyproject.toml`），用 `<仓库根>\.venv\Scripts\python.exe`
4. `py -3.11`（Windows Python Launcher），再退到 PATH 上的 `python`

Python 参数：`-m suiyi_engine serve --port <engine.port，默认 18780> --preload <engine.preload，默认 zh-en,en-zh> [--preload-ocr，engine.preloadOcr 为 true 时，默认带] [--models-dir <engine.modelsDir>]`；工作目录为仓库根（找不到时为 exe 所在目录）。进程以 `CreateNoWindow` 启动，不弹控制台，并加入 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Job Object（`Suiyi.App/Interop/JobObject.cs`），客户端被任务管理器强杀时服务随之退出。

以上 `engine.*` 取自设置文件（见「设置文件」）；开发时也可用环境变量临时覆盖（优先于设置）：`SUIYI_ENGINE_PORT`、`SUIYI_ENGINE_PRELOAD`、`SUIYI_ENGINE_PYTHON`、`SUIYI_ENGINE_MODELS_DIR`、`SUIYI_ENGINE_COMMAND`。

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
- `RestartAsync()`（托盘「重启翻译服务」、浮窗「重试」）结束托管进程并清零崩溃计数；一次重启持续到再次进入 Ready / Failed / Stopped，期间重复调用被忽略（`IsRestarting`）；`StopAsync()` 用 `Kill(entireProcessTree: true)` 结束托管进程，最多等 2 s；外部服务不动。
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
