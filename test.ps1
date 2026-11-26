# 编译模组并运行离线测试（C# HarnessTest + DSH 插件 JS 测试，不需要启动游戏）
. (Join-Path $PSScriptRoot "common.ps1")
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Dotnet run --project "$PSScriptRoot\tests\HarnessTest" @MsbuildPaths
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Host "找不到 node（PATH 中未安装 Node.js），无法运行 DSH 插件 JS 测试。" -ForegroundColor Red
    exit 1
}
$jsTests = Get-ChildItem -Path (Join-Path $PSScriptRoot "tests\DshPlugin") -Filter "*.test.mjs" -File | Sort-Object Name | ForEach-Object { $_.FullName }
if (-not $jsTests -or $jsTests.Count -eq 0) {
    Write-Host "找不到 tests\DshPlugin\*.test.mjs 测试文件。" -ForegroundColor Red
    exit 1
}
Write-Host "`n运行 DSH 插件 JS 测试（$($jsTests.Count) 个文件）..." -ForegroundColor Cyan
& $node.Source --test @jsTests
exit $LASTEXITCODE
