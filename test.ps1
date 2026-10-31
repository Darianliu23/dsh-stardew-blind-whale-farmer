# 编译模组并运行离线测试（不需要启动游戏）
. (Join-Path $PSScriptRoot "common.ps1")
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Dotnet run --project "$PSScriptRoot\tests\HarnessTest" @MsbuildPaths
exit $LASTEXITCODE
