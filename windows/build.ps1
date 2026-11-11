# CasePilot for Windows: restore + build + test + publish 自包含单文件 exe (无需安装 .NET), 并打包 zip.
# 用法: ./build.ps1 [-Version v1.0.0] [-Rids win-x64,win-arm64]
param([string]$Configuration = "Release", [string]$Version = "dev", [string[]]$Rids = @("win-x64", "win-arm64"))
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet restore CasePilot.Windows.sln
dotnet build CasePilot.Windows.sln -c $Configuration --no-restore
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test CasePilot.Windows.sln -c $Configuration --no-build
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$dist = Join-Path $PSScriptRoot "dist"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
foreach ($rid in $Rids) {
    $out = Join-Path $dist $rid
    # 自包含 + 单文件 + 压缩: 运行时打进 exe, 用户下载后双击即可
    dotnet publish src/CasePilot.App -c $Configuration -r $rid --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    $zip = Join-Path $dist "CasePilot-$Version-$rid.zip"
    Compress-Archive -Path (Join-Path $out "CasePilot.exe") -DestinationPath $zip -Force
    Write-Host "Packaged $zip"
}
