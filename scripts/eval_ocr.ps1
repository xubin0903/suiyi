<#
.SYNOPSIS
  Windows 一条命令跑 OCR 评测（#54）。说明见 docs/engine/OCR评测.md。

.EXAMPLE
  # 第一次：建 venv、装依赖、下载模型，然后跑完整评测
  powershell -ExecutionPolicy Bypass -File scripts\eval_ocr.ps1 -Setup -Label "我的笔记本 i5-1135G7 16GB"

.EXAMPLE
  # 之后：直接跑（其余参数原样传给 scripts/eval_ocr.py）
  powershell -ExecutionPolicy Bypass -File scripts\eval_ocr.ps1 --quick
#>
param(
    [switch]$Setup,
    [string]$Venv = ".venv-ocr",
    [string]$Label = "",
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

# 中文输出与 JSON 一律 UTF-8，避免 GBK 控制台乱码或 UnicodeEncodeError
$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$py = Join-Path $Venv "Scripts\python.exe"
if ($Setup) {
    if (-not (Test-Path $py)) {
        Write-Host "创建虚拟环境 $Venv …"
        python -m venv $Venv
        if ($LASTEXITCODE -ne 0) { throw "python -m venv 失败：请先安装 Python 3.11+（python.org 安装版）" }
    }
    & $py -m pip install --upgrade pip
    & $py -m pip install -e "./engine[ocr,eval]"
    if ($LASTEXITCODE -ne 0) { throw "pip install 失败" }
    # 推荐组合（det small + rec small + cls）与对比用的 det tiny，写入 models\ocr\
    & $py scripts/download_ocr_models.py download
    if ($LASTEXITCODE -ne 0) { throw "下载推荐 OCR 模型失败" }
    & $py scripts/download_ocr_models.py download --model PP-OCRv6_det_tiny
    if ($LASTEXITCODE -ne 0) { throw "下载 PP-OCRv6_det_tiny 失败" }
}
if (-not (Test-Path $py)) {
    throw "找不到 $py：第一次请加 -Setup"
}
$argsList = @("scripts/eval_ocr.py")
if ($Label) { $argsList += @("--label", $Label) }
if ($Rest) { $argsList += $Rest }
& $py @argsList
exit $LASTEXITCODE
