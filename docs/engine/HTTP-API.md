# 本机 HTTP API

`python -m suiyi_engine serve` 在本机回环地址上提供翻译服务。复制翻译和框选翻译的客户端都调用这一套接口。框选翻译用 `POST /ocr_translate`：上传 PNG，一次往返拿到识别结果和译文（OCR 部分见 [OCR 核心](OCR核心.md)）。翻译本身由 [翻译核心](翻译核心.md) 完成；`source: "auto"` 先走 [语种检测](语种检测.md)，再把具体语种交给翻译器。

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
| `--port` | 环境变量 `SUIYI_PORT`，否则 `18780` | 命令行优先于环境变量。端口上已有监听者时非零退出（见下文「端口占用判断」） |
| `--models-dir` | `SUIYI_MODELS_DIR`，否则仓库根 `models/` | 传给 `Translator` |
| `--preload` | 不预热 | 逗号分隔的语向，如 `zh-en,en-zh`。缺模型时非零退出，不会开始监听 |
| `--max-text-chars` | `SUIYI_MAX_TEXT_CHARS`，否则 `10000` | 单条文本的字符上限 |
| `--max-image-bytes` | `SUIYI_MAX_IMAGE_BYTES`，否则 `8388608`（8 MiB） | OCR 请求体的字节上限 |
| `--preload-ocr` | 关闭 | 开始监听前加载并预热 OCR 模型（启动日志「OCR 已预热 N ms」），之后 `/health` 的 `ocr_loaded` 为 `true`。**与 `--preload` 不同，失败不退出**：OCR 依赖未装或模型缺失/损坏时只在 stderr 打一行警告（含缺失的 OCR 模型 id 和下载命令），服务照常启动，文本翻译不受影响；`/health` 的 `ocr_error` 带上原因，OCR 接口返回 503。客户端设置 `engine.preloadOcr`（#58）为 `true` 时追加这个参数，可以安全地默认开启 |
| `--glossary` / `--no-glossary` | 环境变量 `SUIYI_GLOSSARY`，否则开启 | 术语保护的默认开关（#83，见 [术语保护](术语保护.md)）。环境变量接受 `1/0`、`true/false`（也接受 `on/off`，不区分大小写），认不出的值在 stderr 告警并按开启处理。`/translate` 可以用 `glossary` 字段单次覆盖 |
| `--user-glossary` | 环境变量 `SUIYI_USER_GLOSSARY`，否则 `<设置目录>/glossary.tsv` | 用户术语表路径，文件可以不存在。设置目录与客户端 `settings.json` 相同：`SUIYI_CONFIG_DIR`，否则 Windows `%APPDATA%\suiyi`，其他系统 `$XDG_CONFIG_HOME/suiyi`（默认 `~/.config/suiyi`） |
| `--dev` | 关闭 | 才挂载 `/docs` 与 `/openapi.json` |
| `--intra-threads` | 环境变量 `SUIYI_INTRA_THREADS`，否则 `min(4, CPU 数)`（拿不到 CPU 数时 2） | 单个模型内部的计算线程。命令行优先于环境变量；环境变量不是 ≥ 1 的整数时，在开始监听前非零退出。#87 起默认上限从 2 改为 4 |
| `--beam-size` | `2` | 束搜索宽度。不传则用翻译核心的默认 |
| `--max-batch-size` | `32` | 一次请求里按句批量解码的上限。不传则用翻译核心的默认 |
| `--model-idle-unload` | 环境变量 `SUIYI_MODEL_IDLE_UNLOAD`，否则 `600` | 翻译模型连续这么多秒没被用到就卸载（#92），下次用到时自动重新加载。`0` 表示不卸载。命令行优先于环境变量；不是 ≥ 0 的整数时在开始监听前非零退出。只卸载翻译模型，不卸载 OCR 和语种检测 |

OCR 依赖（`engine[ocr]`）没装、或 `<models_dir>/ocr/` 缺模型时（无论是否加 `--preload-ocr`），服务照常启动，翻译接口不受影响，只有 `/ocr`、`/ocr_translate` 返回 503 `ocr_unavailable`。补齐模型后下一次请求就能用，不用重启。

**客户端请用环境变量 `SUIYI_GLOSSARY` / `SUIYI_USER_GLOSSARY` 传术语表设置**：老版引擎会忽略不认识的环境变量，但遇到不认识的命令行参数会启动失败。

进程起来后，标准输出有这几行：监听 URL、模型目录、可用语向数量，实际使用的 `intra_threads`、`beam_size`、`max_batch_size`，「语种检测已预热 N ms」，模型空闲卸载设置（「模型空闲卸载 600 秒」或「模型空闲卸载 关闭」，#92），以及术语表状态（「术语保护开启：内置 N 条，用户 M 条（路径）」）。清单推荐的模型没装、正在用同方向的旧模型时（例如只装了旧的 `opus-mt-en-zh`），stderr 多一行告警和补装命令，服务照常启动。可用语向只统计已经安装、现在就能翻译的方向（含英文中转）。这三个解码参数必须是大于等于 1 的整数，否则在开始监听前以非零状态退出。

### 端口占用判断

服务先绑定监听套接字，再预热语种检测，然后把同一个套接字交给 uvicorn 开始监听。绑定失败就打印「端口 … 已被占用」并以状态 1 退出，不预热也不监听。

套接字选项按平台区分（#46）：

| 平台 | 选项 | 原因 |
|------|------|------|
| Linux / macOS | `SO_REUSEADDR` | 服务被强杀后，调用方连接池里的 keep-alive 连接会让服务端一侧停在 FIN-WAIT / TIME_WAIT（Linux 最长约 60 秒）。不设这个选项时绑定报 `EADDRINUSE`，服务无法立即重启。这些平台上它不允许和正在监听的套接字共用同一地址，所以真实占用仍然报错 |
| Windows | 不设 `SO_REUSEADDR`，也不设 `SO_EXCLUSIVEADDRUSE`，用默认选项 | Windows 上的 `SO_REUSEADDR` 允许抢占别人正在用的端口。监听套接字如果设 `SO_EXCLUSIVEADDRUSE`，它接受过的连接在完全结束前会挡住下一次独占绑定，同样无法立即重启。默认绑定不受残留连接影响；同一地址上已有监听者时仍报 `WSAEADDRINUSE` |

已知差异：同一用户下，别人监听 `0.0.0.0`、随译绑定 `127.0.0.1` 时，Windows 与 macOS 允许两者共存（CI 实测 Windows 上用 `SO_EXCLUSIVEADDRUSE` 探测也发现不了）。这与修复前相同。客户端用 `/health` 判断端口上是不是随译

两个实例同时启动时，在 POSIX 上可能都绑定成功，但后调用 `listen` 的那个会失败，打印「无法在 … 启动服务」并以状态 1 退出。

修复前后，Linux 上「带 keep-alive 连接强杀服务，然后立即重启」各测 5 次：

| | 重启结果 |
|---|---|
| 修复前 | 5 次都失败：`端口 … 已被占用或无法在 127.0.0.1 上监听：[Errno 98] Address already in use`（残留连接状态 `FIN-WAIT-1`） |
| 修复后 | 5 次都成功，716–778 ms 后 `/health` 就绪 |

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
- 服务端忽略不认识的请求字段，不因此返回 `invalid_request`。新的可选项加在请求体里，旧客户端不用改；新客户端对老引擎发新字段也不会出错（例如 #83 的 `glossary`，老引擎直接忽略）。
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
  "uptime_s": 12.3,
  "ocr_loaded": false,
  "model_idle_unload_s": 600,
  "glossary_enabled": true,
  "glossary_builtin_entries": 574,
  "glossary_user_path": "C:\\Users\\me\\AppData\\Roaming\\suiyi\\glossary.tsv",
  "glossary_user_entries": 6,
  "glossary_error": null,
  "glossary_warnings": ["第 12 行：缺少目标词（源词和目标词之间要用 Tab 分隔）"],
  "ocr_error": {
    "message": "缺少 OCR 模型：PP-OCRv6_det_small、ch_ppocr_mobile_v2.0_cls_mobile、PP-OCRv6_rec_small（目录 /path/to/models/ocr）。请执行 python scripts/download_ocr_models.py download 下载 OCR 模型",
    "reason": "models_missing",
    "missing_models": ["PP-OCRv6_det_small", "ch_ppocr_mobile_v2.0_cls_mobile", "PP-OCRv6_rec_small"]
  }
}
```

| 字段 | 含义 |
|------|------|
| `status` | 固定 `"ok"` |
| `version` | 引擎包版本 |
| `models_dir` | 本次进程使用的模型目录 |
| `loaded_models` | 已经加载进内存的模型 id，字典序。`--preload` 成功后这里能看到它们。#92 起模型空闲超过 `model_idle_unload_s` 秒会被卸载，这个列表会变短（可能变成 `[]`）；下次翻译会重新加载 |
| `model_idle_unload_s` | 翻译模型空闲卸载的秒数，`0` 表示不卸载。#92 新增 |
| `uptime_s` | 自开始监听起的秒数，保留 1 位小数 |
| `ocr_loaded` | OCR 模型是否已加载进内存（`--preload-ocr` 成功或第一次 OCR 请求成功之后为 `true`）。#53 新增 |
| `glossary_enabled` | 服务端默认是否开启术语保护（`--glossary` / `SUIYI_GLOSSARY` 的结果；内置术语表加载失败时为 `false`）。不反映单次请求的 `glossary` 覆盖。#83 新增 |
| `glossary_builtin_entries` | 内置术语表条数，按方向展开（一条 zh↔en 术语算 2 条）。#83 新增 |
| `glossary_user_path` | 实际使用的用户术语表路径，文件可以不存在；没有配置时为 `null`。#83 新增 |
| `glossary_user_entries` | 当前生效的用户条目数，按方向展开；文件不存在或文件级错误时为 0。#83 新增 |
| `glossary_error` | 文件级错误原因（不是 UTF-8、读不了、超过 1 MiB 或 5000 条；内置表加载失败）。没有错误时为 `null`。出错时只用内置表，翻译照常。#83 新增 |
| `glossary_warnings` | 用户术语表的行级问题，最多 20 条，形如 `"第 12 行：缺少目标词…"`；这些行被跳过，其余照常生效。#83 新增 |
| `ocr_error` | 最近一次加载 OCR 失败的原因，形状同 503 `ocr_unavailable` 的 `details` 再加 `message`：`reason`、`missing_models`、`message`。没有失败或还没尝试加载时为 `null`。加了 `--preload-ocr` 时启动就会尝试，所以缺模型能在启动后立刻从这里看到；不加时要等第一次 OCR 请求。加载成功后清空。#53 新增 |

翻译在线程池里执行，并且进程内同时只跑一路翻译。OCR 也在线程池里执行，有自己的一把锁，与翻译互不阻塞。`/health` 两把锁都不进，长文本翻译或长 OCR 时它仍应在 200 毫秒内返回。

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
      "models": ["opus-mt-ja-en", "opus-mt-eng-zho-tc-big-2022-05-14"]
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

MVP 必测六个方向是 `zh↔en`、`zh↔ja`、`en↔ja`。它们是否出现，取决于对应模型（`ja→zh` 则是 `opus-mt-ja-en` 与 `opus-mt-eng-zho-tc-big-2022-05-14`；只装了旧的 `opus-mt-en-zh` 时用它顶替第二跳）是否已经放进模型目录。

## `POST /translate`

`Content-Type: application/json`。`text` 与 `texts` 必须有且只有一个。`source` 可以是 `"auto"` 或 ISO 639-1（`zh-CN` 会收成 `zh`）。`target` 不能是 `"auto"`。

可选字段 `glossary`（#83）：`true` / `false` 只影响这一次请求是否做术语保护（`text` 和 `texts` 都支持）；省略或 `null` 时用服务端默认。必须是 JSON 布尔值，`"yes"`、`1`、对象等返回 422 `invalid_request`。术语保护目前只作用于 zh↔en 直连，其他语向忽略这个字段。其他多出来的字段会被忽略。

单条文本超过 `--max-text-chars`（默认 10000 个 Unicode 字符，按 Python `len`）返回 413。批量时每一条单独计，任一条超限则整次请求失败，不返回部分译文。

`source` 为 `auto` 时，对每一条文本调用语种检测。检测结果就是响应里的 `source`，`detected` 为 `true`。检测结果与 `target` 相同则原样返回，`route` 为空数组。检测结果为 `und` 时返回 `detect_failed`。显式指定语种时 `detected` 为 `false`。

`elapsed_ms` 是翻译器报告的墙钟毫秒，保留 1 位小数，包含该方向第一次加载模型的时间，不包含 HTTP 解析和语种检测。

`serve` 在开始监听之前加载语种检测的统计模型（启动日志打印「语种检测已预热 N ms」）。#92 起用精简缓存（见 [语种检测](语种检测.md#92-精简缓存)），有缓存时约 10 ms；安装后第一次启动要在子进程里生成缓存，约 0.5 s，所以第一次 `auto` 请求的检测也在 1 ms 内，`elapsed_ms` 之外不再藏着这段加载时间（#40）。预热失败只在 stderr 告警、不阻止启动，检测会在第一次需要统计模型时再加载。

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

批量响应只有 `results`，每一项与单条对象相同。英文中转时 `route` 长度为 2，例如 `["opus-mt-ja-en", "opus-mt-eng-zho-tc-big-2022-05-14"]`。响应格式不因术语保护而变化。

## `POST /glossary/reload`

立即重读用户术语表（#83），返回与 `/health` 相同的 `glossary_*` 字段。平时不需要调用：每次 `/translate` 前都会检查文件的修改时间和大小（最多每秒一次），改了就自动重读。客户端在用户保存或关闭术语表编辑器后调一次，可以马上拿到行级警告。请求体为空；术语表永远不会让这个接口报错，错误写在 `glossary_error` / `glossary_warnings` 里。

```bash
curl -sS -X POST http://127.0.0.1:18780/glossary/reload
```

## `POST /ocr_translate`

框选翻译：请求体是**原始 PNG 字节**（不用 multipart，也不用 base64），一次往返返回识别结果与按段落对应的译文（#53）。

| 查询参数 | 必填 | 说明 |
|----------|------|------|
| `target` | 是 | 目标语种，不能是 `auto` |
| `source` | 否，默认 `auto` | 原文语种或 `auto` |
| `fallback_target` | 否 | 次目标：原文语种等于 `target` 时改译为它（客户端的主/次目标规则）。与 `target` 相同或为空时忽略 |
| `glossary` | 否 | 本次是否做术语保护（#87），含义同 `/translate` 的 `glossary` 字段。写法是 `?glossary=true` 或 `?glossary=false`；不传时按服务配置（`--glossary` / `SUIYI_GLOSSARY`）。框架也接受 `1/0`、`yes/no`、`on/off`，其他值返回 422 `invalid_request`（`details.errors[].loc` 为 `["query", "glossary"]`），不做 OCR。只作用于 zh↔en 直连。老版引擎会忽略这个参数（已在 main 3a7aebf 上实测：带 `glossary=false` 仍返回 200，按默认开启翻译） |

处理顺序与对应错误：

1. 查询参数不合法（缺 `target`、`target=auto`、语种代码非法、`glossary` 不是布尔值）→ 422 `invalid_request`，不读请求体。
2. 字节上限：先看 `Content-Length`，超过 `--max-image-bytes` 直接 413，**不读请求体**；没有 `Content-Length`（分块上传）时边读边计数，超限立即停止读取 → 413 `image_too_large`。
3. 按文件头魔数判断是不是 PNG，**不看 `Content-Type`**（`application/octet-stream` 也行）。不是 PNG 或请求体为空 → 415 `unsupported_media_type`。
4. 从 IHDR 读宽高（不解码像素）。头部不完整或尺寸为 0 → 422 `invalid_image`；宽 × 高超过 16,777,216（4096 × 4096）→ 413 `image_too_large`。只限总像素，不限单边，细长截图可以超过 4096。
5. OCR 不可用（依赖未装、模型缺失或损坏）→ 503 `ocr_unavailable`。
6. 解码失败（数据损坏、截断）→ 422 `invalid_image`。
7. 识别，合并成段落，再逐段翻译。翻译侧错误与 `/translate` 相同：`unsupported_pair`、`text_too_long`、`detect_failed`，`details.index` 是段落序号。任一段失败则整次请求失败。

语种与次目标（`source=auto` 时）：

- 每个段落各自做语种检测，结果就是该段的 `source`，`detected: true`。
- 检测不出的段落（`und`，例如只有数字、时间、符号）用整张图的语种。整张图的语种先对全文检测，检测不出再取各段结果里最多的；全部检测不出时 422 `detect_failed`。
- 次目标**按整张图决定**：整张图的语种等于 `target` 且给了 `fallback_target` 时，所有段落都译成 `fallback_target`。这样浮窗里的译文语种一致。显式给 `source` 时，用它与 `target` 比较。
- 段落语种与实际目标相同时原样返回，`route` 为空数组（与 `/translate` 一致）。

识别为空（没有段落）时返回 200：`paragraphs: []`、`text: ""`、`translation.results: []`。低置信度的行保留在 `lines` 里（`low_confidence: true`），但不进段落和译文。

#### bash

```bash
curl -sS 'http://127.0.0.1:18780/ocr_translate?source=auto&target=en&fallback_target=zh' \
  -H 'Content-Type: image/png' --data-binary @shot.png

# 这一次关闭术语保护
curl -sS 'http://127.0.0.1:18780/ocr_translate?target=zh&glossary=false' \
  -H 'Content-Type: image/png' --data-binary @shot.png
```

#### Windows PowerShell

```powershell
Invoke-RestMethod 'http://127.0.0.1:18780/ocr_translate?source=auto&target=en&fallback_target=zh' `
  -Method Post -InFile .\shot.png -ContentType image/png
```

```json
{
  "lines": [
    {"text": "本地翻译", "box": [[10.0, 10.0], [200.0, 10.0], [200.0, 44.0], [10.0, 44.0]], "score": 0.9962, "low_confidence": false},
    {"text": "随译把模型放在本机，复制、翻译、显示都在同", "box": [[10.0, 60.0], [600.0, 60.0], [600.0, 84.0], [10.0, 84.0]], "score": 0.9981, "low_confidence": false},
    {"text": "一台机器上完成。", "box": [[10.0, 90.0], [250.0, 90.0], [250.0, 114.0], [10.0, 114.0]], "score": 0.9975, "low_confidence": false}
  ],
  "paragraphs": [
    {"text": "本地翻译", "box": [10.0, 10.0, 200.0, 44.0], "line_indices": [0], "vertical": false},
    {"text": "随译把模型放在本机，复制、翻译、显示都在同一台机器上完成。", "box": [10.0, 60.0, 600.0, 114.0], "line_indices": [1, 2], "vertical": false}
  ],
  "text": "本地翻译\n随译把模型放在本机，复制、翻译、显示都在同一台机器上完成。",
  "image": {"width": 640, "height": 200},
  "translation": {
    "results": [
      {"text": "Local translation", "source": "zh", "detected": true, "target": "en", "route": ["opus-mt-zh-en"], "elapsed_ms": 61.3},
      {"text": "...", "source": "zh", "detected": true, "target": "en", "route": ["opus-mt-zh-en"], "elapsed_ms": 240.8}
    ]
  },
  "elapsed_ms": {"ocr": 180.4, "translate": 305.2, "total": 486.1}
}
```

| 字段 | 含义 |
|------|------|
| `lines[]` | 识别出的文本行：`text`、`box`（四点框 `[[x, y] × 4]`，原图像素，左上、右上、右下、左下）、`score`（0–1）、`low_confidence`。竖排时一个 `line` 是一列 |
| `paragraphs[]` | 合并后的段落，顺序就是阅读顺序（竖排从右到左）：`text`、`box`（外接矩形 `[x0, y0, x1, y1]`）、`line_indices`（指向 `lines`）、`vertical` |
| `text` | 段落以 `\n` 连接 |
| `image` | 服务端解码出的 `width`、`height` |
| `translation.results[]` | 与 `/translate` 批量结果同构，和 `paragraphs` 按顺序一一对应 |
| `elapsed_ms` | 对象：`ocr`（从请求开始到识别完成，含读请求体、首次加载 OCR 模型）、`translate`（检测 + 翻译，含排队等翻译锁）、`total` |

服务端日志只记录尺寸、字节数、行数、段数、目标语种和耗时，不记录图片和识别文本。

## `POST /ocr`

只识别不翻译。请求体、字节/像素上限、415/422/503 与 `/ocr_translate` 相同。查询参数 `lang`：`auto`（默认）、`zh`、`en`、`ja`，其他值 422 `invalid_request`。当前中英日共用一个识别模型，`lang` 只做校验，不改变识别结果。

响应是 `/ocr_translate` 去掉 `translation` 的部分，`elapsed_ms` 是**数字**（总耗时毫秒），不是对象。

#### bash

```bash
curl -sS 'http://127.0.0.1:18780/ocr?lang=auto' -H 'Content-Type: image/png' --data-binary @shot.png
```

#### Windows PowerShell

```powershell
Invoke-RestMethod 'http://127.0.0.1:18780/ocr?lang=auto' -Method Post -InFile .\shot.png -ContentType image/png
```

## 错误

失败时 HTTP 状态不是 200，正文仍然是 JSON：

```json
{
  "error": {
    "code": "unsupported_pair",
    "message": "不支持的语向 en→zh，未下载模型：opus-mt-eng-zho-tc-big-2022-05-14",
    "details": {
      "source": "en",
      "target": "zh",
      "missing_models": ["opus-mt-eng-zho-tc-big-2022-05-14"]
    }
  }
}
```

| `error.code` | HTTP | 何时 | `details` |
|--------------|------|------|-----------|
| `unsupported_pair` | 422 | 语向没有可加载的模型 | `source`、`target`、`missing_models`（缺失的模型 id，可能为空）。批量时另有 `index` |
| `invalid_request` | 422 | 缺字段（含 OCR 接口缺 `target`）、`text`/`texts` 冲突、语种代码非法、`target` 为 `auto`、`lang` 不在允许范围、JSON 无法解析 | 字段错误时有 `field`；JSON 校验失败时有 `errors`（`loc`、`msg`、`type`） |
| `text_too_long` | 413 | 某条文本超过字符上限 | `limit`、`length`。批量时另有 `index` |
| `detect_failed` | 422 | `source` 为 `auto` 但检测结果不是可用语种（含 `und`） | `index`、`detected` |
| `image_too_large` | 413 | OCR 请求体超过字节上限，或 PNG 宽 × 高超过像素上限 | `kind`（`bytes` / `pixels`）、`limit`、`actual`；像素超限时另有 `width`、`height` |
| `unsupported_media_type` | 415 | OCR 请求体不是 PNG（按魔数判断）或为空 | `{}` |
| `invalid_image` | 422 | PNG 头部不完整、尺寸为 0、或无法解码 | `{}` |
| `ocr_unavailable` | 503 | OCR 依赖未安装、模型清单或模型文件缺失/损坏 | `reason`（`dependency_missing` / `models_missing` / `models_invalid` / `manifest_unavailable`）、`missing_models`（缺失的 OCR 模型 id，可能为空）。`message` 里有安装或下载提示 |
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
- 术语保护只作用于 zh↔en 直连（#83）。`/translate` 用 JSON 字段 `glossary`，`/ocr_translate` 用同名 query 参数（#87）。
- 批量条数没有单独上限；每一条仍受字符上限约束。同时只执行一路翻译，多出来的请求在线程池里排队。`/health` 不排队。
- `internal_error` 不把异常文本返回给客户端。服务端日志里有栈。
- `elapsed_ms` 不含语种检测和 HTTP 开销。
- 纯汉字日语可能被检测成 `zh`，这是语种检测规则层的既有限制。
- 一期推理仍是 CPU + int8。本接口不提供设备切换参数。
- OCR 只接受 PNG；不支持 JPEG/BMP/base64 JSON，不支持一次多张图，不流式返回。
- OCR 同时只跑一张图，多出来的请求排队（onnxruntime 内部已多线程）。
- `ocr_unavailable` 的 `missing_models` 是 OCR 模型 id（如 `PP-OCRv6_det_small`），与 `unsupported_pair` 的翻译模型 id 不是一套。
