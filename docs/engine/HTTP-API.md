# 本机 HTTP API

`python -m suiyi_engine serve` 在本机回环地址上提供翻译服务。复制翻译和框选翻译的客户端都调用这一套接口。翻译本身由 [翻译核心](翻译核心.md) 完成；`source: "auto"` 先走 [语种检测](语种检测.md)，再把具体语种交给翻译器。

服务不开启 CORS，不做鉴权，也不提供 WebSocket。默认关闭 `/docs`。

## 启动

在仓库根目录、已安装 `engine[dev]` 或运行时依赖的环境中：

#### bash

```bash
python -m suiyi_engine serve
python -m suiyi_engine serve --port 18781 --models-dir /path/to/models --preload zh-en,en-zh
```

#### Windows PowerShell

```powershell
python -m suiyi_engine serve
python -m suiyi_engine serve --port 18781 --models-dir C:\path\to\models --preload zh-en,en-zh
```

| 参数 | 默认 | 说明 |
|------|------|------|
| `--host` | `127.0.0.1` | 只接受 `127.0.0.1`、`::1`、`localhost`。其他值打印原因后非零退出，不会开始监听 |
| `--port` | 环境变量 `SUIYI_PORT`，否则 `18780` | 命令行优先于环境变量。端口被占用时非零退出 |
| `--models-dir` | `SUIYI_MODELS_DIR`，否则仓库根 `models/` | 传给 `Translator` |
| `--preload` | 不预热 | 逗号分隔的语向，如 `zh-en,en-zh`。缺模型时非零退出，不会开始监听 |
| `--max-text-chars` | `SUIYI_MAX_TEXT_CHARS`，否则 `10000` | 单条文本的字符上限 |
| `--dev` | 关闭 | 才挂载 `/docs` 与 `/openapi.json` |

进程起来后，标准输出有三行：监听 URL、模型目录、可用语向数量。可用语向只统计已经安装、现在就能翻译的方向（含英文中转）。

## 安全

- 监听地址写死在回环范围里。`--host 0.0.0.0` 或局域网地址会被拒绝。
- 没有鉴权 token。不要用反向代理把它转到公网或其他网卡。
- 不发送 `Access-Control-Allow-Origin`。调用方应是本机原生程序，不是浏览器页面。
- 未加 `--dev` 时，`/docs` 和 `/openapi.json` 不存在。

## 版本兼容

`GET /health` 的 `version` 是 `suiyi_engine.__version__`。当前是 `0.0.1`（0.0.x）。

在 0.0.x 内：

- 已经出现的 JSON 字段保持名字和含义。可以新增字段。
- 客户端必须忽略不认识的响应字段。
- 服务端忽略不认识的请求字段，不因此返回 `invalid_request`。以后的术语表等可选项可以加在请求体里，旧客户端不用改。
- 错误形状固定为 `error.code`、`error.message`、`error.details`。`details` 始终是对象，可以多出键。
- 不保证 0.0.x 与以后的 0.1 字段完全相同。升级次版本前先看本文。

`GET /languages` 里的 `route` 是字符串 `"direct"` 或 `"pivot"`。`POST /translate` 里的 `route` 是模型 id 数组。两处不是同一个类型。

## `GET /health`

进程活着就返回 200。没有已加载模型时仍然是 `ok`；它表示服务在听，不表示某个语向已经下载。

#### bash

```bash
curl -sS http://127.0.0.1:18780/health
```

#### Windows PowerShell

```powershell
Invoke-RestMethod http://127.0.0.1:18780/health
```

```json
{
  "status": "ok",
  "version": "0.0.1",
  "models_dir": "/path/to/models",
  "loaded_models": ["opus-mt-zh-en"],
  "uptime_s": 12.3
}
```

| 字段 | 含义 |
|------|------|
| `status` | 固定 `"ok"` |
| `version` | 引擎包版本 |
| `models_dir` | 本次进程使用的模型目录 |
| `loaded_models` | 已经加载进内存的模型 id，字典序。`--preload` 成功后这里能看到它们 |
| `uptime_s` | 自开始监听起的秒数，保留 1 位小数 |

翻译在线程池里执行，并且进程内同时只跑一路翻译。`/health` 不进入这把锁，长文本翻译时它仍应在 200 毫秒内返回。

## `GET /languages`

列出**当前目录里实际能翻译**的语向，包括清单声明的英文中转，以及清单没有写死、但 `src→en` 与 `en→tgt` 都已安装时的英文中转。

清单把某方向定成直连、可是该模型还没下载时，这里不会改列成中转。因此只装了 `zh→en` 和 `en→ja` 时，不会出现 `zh→ja`。这与翻译核心的路由一致。

`languages` 是这些语向里出现过的语种代码，按字母排序。同一语种到自己（原文返回）不占一条 pair。

#### bash

```bash
curl -sS http://127.0.0.1:18780/languages
```

#### Windows PowerShell

```powershell
Invoke-RestMethod http://127.0.0.1:18780/languages
```

```json
{
  "languages": ["en", "ja", "zh"],
  "pairs": [
    {
      "src": "ja",
      "tgt": "en",
      "route": "direct",
      "models": ["opus-mt-ja-en"]
    },
    {
      "src": "ja",
      "tgt": "zh",
      "route": "pivot",
      "models": ["opus-mt-ja-en", "opus-mt-en-zh"]
    },
    {
      "src": "zh",
      "tgt": "en",
      "route": "direct",
      "models": ["opus-mt-zh-en"]
    }
  ]
}
```

MVP 必测六个方向是 `zh↔en`、`zh↔ja`、`en↔ja`。它们是否出现，取决于对应模型（`ja→zh` 则是 `opus-mt-ja-en` 与 `opus-mt-en-zh`）是否已经放进模型目录。

## `POST /translate`

`Content-Type: application/json`。`text` 与 `texts` 必须有且只有一个。`source` 可以是 `"auto"` 或 ISO 639-1（`zh-CN` 会收成 `zh`）。`target` 不能是 `"auto"`。

多出来的字段（例如以后的术语表）会被忽略。

单条文本超过 `--max-text-chars`（默认 10000 个 Unicode 字符，按 Python `len`）返回 413。批量时每一条单独计，任一条超限则整次请求失败，不返回部分译文。

`source` 为 `auto` 时，对每一条文本调用语种检测。检测结果就是响应里的 `source`，`detected` 为 `true`。检测结果与 `target` 相同则原样返回，`route` 为空数组。检测结果为 `und` 时返回 `detect_failed`。显式指定语种时 `detected` 为 `false`。

`elapsed_ms` 是翻译器报告的墙钟毫秒，保留 1 位小数，包含该方向第一次加载模型的时间，不包含 HTTP 解析和语种检测。

空白文本在源语种已经明确、且与目标不同时，按翻译核心的约定返回空字符串，不检查模型是否下载。`source: "auto"` 的空白或纯符号通常无法识别，返回 `detect_failed`。

#### bash

```bash
curl -sS http://127.0.0.1:18780/translate \
  -H 'Content-Type: application/json' \
  -d '{"text":"今天天气很好。","source":"auto","target":"en"}'

curl -sS http://127.0.0.1:18780/translate \
  -H 'Content-Type: application/json' \
  -d '{"texts":["今天天气很好。","Hello."],"source":"zh","target":"en"}'
```

#### Windows PowerShell

```powershell
$payload = '{"text":"今天天气很好。","source":"auto","target":"en"}'
Invoke-RestMethod http://127.0.0.1:18780/translate -Method Post `
  -ContentType "application/json; charset=utf-8" `
  -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))
```

单条响应：

```json
{
  "text": "The weather is nice today.",
  "source": "zh",
  "detected": true,
  "target": "en",
  "route": ["opus-mt-zh-en"],
  "elapsed_ms": 85.2
}
```

批量响应只有 `results`，每一项与单条对象相同。英文中转时 `route` 长度为 2，例如 `["opus-mt-ja-en", "opus-mt-en-zh"]`。

## 错误

失败时 HTTP 状态不是 200，正文仍然是 JSON：

```json
{
  "error": {
    "code": "unsupported_pair",
    "message": "不支持的语向 en→zh，未下载模型：opus-mt-en-zh",
    "details": {
      "source": "en",
      "target": "zh",
      "missing_models": ["opus-mt-en-zh"]
    }
  }
}
```

| `error.code` | HTTP | 何时 | `details` |
|--------------|------|------|-----------|
| `unsupported_pair` | 422 | 语向没有可加载的模型 | `source`、`target`、`missing_models`（缺失的模型 id，可能为空）。批量时另有 `index` |
| `invalid_request` | 422 | 缺字段、`text`/`texts` 冲突、语种代码非法、`target` 为 `auto`、JSON 无法解析 | 字段错误时有 `field`；JSON 校验失败时有 `errors`（`loc`、`msg`、`type`） |
| `text_too_long` | 413 | 某条文本超过字符上限 | `limit`、`length`。批量时另有 `index` |
| `detect_failed` | 422 | `source` 为 `auto` 但检测结果不是可用语种（含 `und`） | `index`、`detected` |
| `internal_error` | 500 | 未预期的异常 | `{}`。响应里没有异常类型和栈 |

客户端可以用 `missing_models` 提示「未下载语向」，不要只显示语种代码。

未知路径仍是框架自己的 404，正文不是上面的信封。

#### bash

```bash
curl -sS -w '\n%{http_code}\n' http://127.0.0.1:18780/translate \
  -H 'Content-Type: application/json' \
  -d '{"text":"Hello.","source":"en","target":"zh"}'
```

#### Windows PowerShell

错误体用 Windows 自带的 `curl.exe` 查看即可（`Invoke-RestMethod` 在 4xx/5xx 时会抛异常）：

```powershell
curl.exe -sS http://127.0.0.1:18780/translate `
  -H "Content-Type: application/json" `
  -d "{\"text\":\"Hello.\",\"source\":\"en\",\"target\":\"zh\"}"
```

PowerShell 7 也可以给 `Invoke-RestMethod` 加 `-SkipHttpErrorCheck`，再读 `.Content`。

## 已知限制

- 没有鉴权、没有 TLS、没有流式输出、没有命名管道。
- 术语表等扩展字段目前会被忽略，不会参与翻译。
- 批量条数没有单独上限；每一条仍受字符上限约束。同时只执行一路翻译，多出来的请求在线程池里排队。`/health` 不排队。
- `internal_error` 不把异常文本返回给客户端。服务端日志里有栈。
- `elapsed_ms` 不含语种检测和 HTTP 开销。
- 纯汉字日语可能被检测成 `zh`，这是语种检测规则层的既有限制。
- 一期推理仍是 CPU + int8。本接口不提供设备切换参数。
