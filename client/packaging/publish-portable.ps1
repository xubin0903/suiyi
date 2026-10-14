# 在仓库根执行：发布 Windows 便携目录骨架（可不含引擎 exe / 模型）。
[CmdletBinding()]
param(
    [string] $OutputDir = ".\artifacts\suiyi-portable",
    [switch] $SelfContained
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Out = $OutputDir
if (-not [System.IO.Path]::IsPathRooted($Out)) {
    $Out = Join-Path $Root $Out
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$publishArgs = @(
    "publish", (Join-Path $Root "client\src\Suiyi.App\Suiyi.App.csproj"),
    "-c", "Release", "-r", "win-x64", "-o", $Out
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
} else {
    $publishArgs += @("--self-contained", "false")
}
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path (Join-Path $Out "engine"), (Join-Path $Out "models") | Out-Null
Copy-Item (Join-Path $Root "client\packaging\engine\README.txt") (Join-Path $Out "engine\README.txt") -Force
Copy-Item (Join-Path $Root "client\packaging\models\README.txt") (Join-Path $Out "models\README.txt") -Force
Copy-Item (Join-Path $Root "client\packaging\settings.portable.example.json") (Join-Path $Out "settings.portable.example.json") -Force

Write-Host "便携骨架已输出到: $Out"
Write-Host "下一步（可选）：用 bundle-engine.ps1 拼入引擎 onedir，或由 CI assemble 自动完成（见 docs/client/M4-便携包.md）"
