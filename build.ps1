# 重新编译模组并更新 release 文件夹（release\BlindWhaleFarmer 就是可以直接放进 Mods 的模组）
. (Join-Path $PSScriptRoot "common.ps1")
& $Dotnet build "$PSScriptRoot\BlindWhaleFarmer" -c Release @MsbuildPaths
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$out = "$PSScriptRoot\BlindWhaleFarmer\bin\Release\net6.0"
$rel = "$PSScriptRoot\release\BlindWhaleFarmer"
New-Item -ItemType Directory -Force "$rel\guide" | Out-Null
Copy-Item "$out\BlindWhaleFarmer.dll", "$out\manifest.json" $rel -Force
Copy-Item "$out\guide\*" "$rel\guide\" -Force
Write-Host "编译完成，release 已更新。" -ForegroundColor Green
