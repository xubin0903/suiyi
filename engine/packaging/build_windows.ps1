#Requires -Version 5.1
<#
.SYNOPSIS
  在 Windows 上用 PyInstaller onedir 产出 suiyi-engine.exe（#130）。

.DESCRIPTION
  在仓库根目录执行：
    .\engine\packaging\build_windows.ps1
    .\engine\packaging\build_windows.ps1 -OutputDir D:\out\engine-dist

  产物目录结构：
    <OutputDir>\suiyi-engine\suiyi-engine.exe
    <OutputDir>\suiyi-engine\_internal\...

  拷贝到便携包时，把 onedir 目录内容放进便携根的 engine\，即：
    <便携根>\engine\suiyi-engine.exe
    <便携根>\engine\_internal\...

  不打入模型/OCR 权重；不打入 torch/transformers。
#>
param(
  [string]$OutputDir = "",
  [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $RepoRoot

if (-not $OutputDir) {
  $OutputDir = Join-Path $RepoRoot "artifacts\suiyi-engine-win-x64"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$Py = $null
if (Get-Command py -ErrorAction SilentlyContinue) {
  $Py = "py -3.11"
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
  $Py = "python"
} else {
  throw "找不到 Python 3.11（py -3.11 或 python）"
}

Write-Host "仓库根: $RepoRoot"
Write-Host "输出目录: $OutputDir"
Write-Host "Python: $Py"

if (-not $SkipInstall) {
  Invoke-Expression "$Py -m pip install --upgrade pip"
  # opencv-python-headless：避免 GUI opencv；rapidocr 用 --no-deps 再装其余
  Invoke-Expression "$Py -m pip install -e `"./engine[dev]`""
  Invoke-Expression "$Py -m pip install `"pyinstaller>=6.3,<7`" `"onnxruntime>=1.20`" `"opencv-python-headless>=4.8`""
  Invoke-Expression "$Py -m pip install --no-deps `"rapidocr>=3.9.2,<3.10`""
  # rapidocr 其余声明依赖（除 opencv-python）
  Invoke-Expression "$Py -m pip install `"Pillow`" `"pyclipper`" `"shapely`" `"numpy`" `"PyYAML`""
}

$Dist = Join-Path $OutputDir "dist"
$Work = Join-Path $OutputDir "build"
$Spec = Join-Path $RepoRoot "engine\packaging\suiyi-engine.spec"

Invoke-Expression "$Py -m PyInstaller --noconfirm --clean --distpath `"$Dist`" --workpath `"$Work`" `"$Spec`""

$Onedir = Join-Path $Dist "suiyi-engine"
$Exe = Join-Path $Onedir "suiyi-engine.exe"
if (-not (Test-Path $Exe)) {
  throw "未找到产物: $Exe"
}

Write-Host "冒烟: --version / serve --help"
& $Exe --version
& $Exe serve --help | Select-Object -First 5

# 拒绝产物里出现 torch / transformers 目录名
$Bad = Get-ChildItem -Recurse -Directory $Onedir | Where-Object {
  $_.Name -match '^(torch|transformers|huggingface_hub|paddle|tensorflow)($|_)'
}
if ($Bad) {
  throw ("产物含禁止依赖目录: " + ($Bad.FullName -join ", "))
}

Write-Host ""
Write-Host "完成。onedir 目录: $Onedir"
Write-Host "放入便携包: 复制该目录下全部内容到 <便携根>\engine\"
Write-Host "  例如: Copy-Item -Recurse $Onedir\* <便携根>\engine\"
