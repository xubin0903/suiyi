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
  | `Clipboard/` | 剪贴板文本过滤规则 |
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
