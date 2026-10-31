# 把模组复制到星露谷的 Mods 文件夹（需要先安装 SMAPI，并先运行 build.ps1）
param(
    [string]$GamePath = ""
)
$ErrorActionPreference = "Stop"
if (-not $GamePath) { . (Join-Path $PSScriptRoot "common.ps1") }
if (-not (Test-Path "$GamePath\StardewModdingAPI.exe")) {
    Write-Host "没有在 $GamePath 找到 StardewModdingAPI.exe，请先安装 SMAPI（见 README）。" -ForegroundColor Yellow
    exit 1
}
$src = Join-Path $PSScriptRoot "release\BlindWhaleFarmer"
$dst = Join-Path $GamePath "Mods\BlindWhaleFarmer"
New-Item -ItemType Directory -Force "$dst\guide" | Out-Null
Copy-Item "$src\BlindWhaleFarmer.dll", "$src\manifest.json" $dst -Force
# 自带的攻略会随版本更新；想自己改攻略请复制成别的文件名，并在 config.json 的 GuideFile 里指过去
Copy-Item "$src\guide\*" "$dst\guide\" -Force
# 已有 config.json（里面有你的 API Key）就不覆盖
if (-not (Test-Path "$dst\config.json")) { Copy-Item "$src\config.json" $dst }
Write-Host "已安装到 $dst" -ForegroundColor Green
Write-Host "下一步：用 MCP 客户端连 http://127.0.0.1:18080/mcp，或者打开 $dst\config.json 填上 ApiKey 后在游戏里按 F8。"
