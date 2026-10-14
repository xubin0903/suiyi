# 将引擎 PyInstaller onedir 内容拼进便携根的 engine\（#132）。
# 用法：
#   .\client\packaging\bundle-engine.ps1 -PortableDir .\artifacts\suiyi-portable -EngineOnedir .\artifacts\suiyi-engine-win-x64\dist\suiyi-engine
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PortableDir,
    [Parameter(Mandatory = $true)][string] $EngineOnedir
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")

function Resolve-OutPath([string] $Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return (Join-Path $Root $Path)
}

$Portable = Resolve-OutPath $PortableDir
$EngineSrc = Resolve-OutPath $EngineOnedir
$Exe = Join-Path $EngineSrc "suiyi-engine.exe"
$Internal = Join-Path $EngineSrc "_internal"
if (-not (Test-Path $Exe)) { throw "未找到引擎入口: $Exe" }
if (-not (Test-Path $Internal)) { throw "未找到 onedir 依赖目录: $Internal" }

$EngineDst = Join-Path $Portable "engine"
New-Item -ItemType Directory -Force -Path $EngineDst | Out-Null
$ReadmeSrc = Join-Path $PSScriptRoot "engine\README.txt"
$ReadmeDst = Join-Path $EngineDst "README.txt"
if (-not (Test-Path $ReadmeDst) -and (Test-Path $ReadmeSrc)) {
    Copy-Item $ReadmeSrc $ReadmeDst -Force
}

Copy-Item -Path (Join-Path $EngineSrc "*") -Destination $EngineDst -Recurse -Force

if (-not (Test-Path (Join-Path $EngineDst "suiyi-engine.exe"))) {
    throw "拼包后缺少 suiyi-engine.exe"
}
if (-not (Test-Path (Join-Path $EngineDst "_internal"))) {
    throw "拼包后缺少 _internal"
}

Write-Host "已拼入引擎: $(Join-Path $EngineDst 'suiyi-engine.exe')"
