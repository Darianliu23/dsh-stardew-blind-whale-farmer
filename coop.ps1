# 联机双开：你玩的窗口（主机）用平常的 Mods，AI 的窗口用单独的 Mods-AI 文件夹和单独的 MCP 端口。
# 需要先运行 build.ps1。AI 窗口的 config.json 第一次从你的 Mods 里复制，之后只改端口，其他设置不动。
param(
    [string]$GamePath = "",
    [int]$Port = 18081,
    # 只开 AI 窗口（你的窗口已经开着，或者只想测试 AI 窗口）
    [switch]$AiOnly
)
$ErrorActionPreference = "Stop"
if (-not $GamePath) { . (Join-Path $PSScriptRoot "common.ps1") }
$smapi = Join-Path $GamePath "StardewModdingAPI.exe"
if (-not (Test-Path $smapi)) {
    Write-Host "没有在 $GamePath 找到 StardewModdingAPI.exe，请先安装 SMAPI（见 README）。" -ForegroundColor Yellow
    exit 1
}
$running = @(Get-Process StardewModdingAPI -ErrorAction SilentlyContinue)
if ($running.Count -ge 2) {
    Write-Host "已经开着 $($running.Count) 个游戏窗口了，先关掉再运行。" -ForegroundColor Yellow
    exit 1
}

# AI 窗口的模组：BlindWhaleFarmer 用最新编译的，WhaleFarmer（如果装了）照抄
$src = Join-Path $PSScriptRoot "release\BlindWhaleFarmer"
$aiMods = Join-Path $GamePath "Mods-AI"
$dst = Join-Path $aiMods "BlindWhaleFarmer"
if (-not (Test-Path "$src\BlindWhaleFarmer.dll")) {
    Write-Host "还没编译，先运行 build.ps1。" -ForegroundColor Yellow
    exit 1
}
New-Item -ItemType Directory -Force "$dst\guide" | Out-Null
Copy-Item "$src\BlindWhaleFarmer.dll", "$src\manifest.json" $dst -Force
Copy-Item "$src\guide\*" "$dst\guide\" -Force
$whale = Join-Path $GamePath "Mods\WhaleFarmer"
if (Test-Path $whale) { Copy-Item $whale $aiMods -Recurse -Force }

$config = Join-Path $dst "config.json"
if (-not (Test-Path $config)) {
    $hostConfig = Join-Path $GamePath "Mods\BlindWhaleFarmer\config.json"
    Copy-Item $(if (Test-Path $hostConfig) { $hostConfig } else { "$src\config.json" }) $config
}
$text = [IO.File]::ReadAllText($config)
if ($text -notmatch '"McpPort"\s*:\s*\d+') {
    Write-Host "$config 里没有 McpPort，没法设置端口。" -ForegroundColor Yellow
    exit 1
}
[IO.File]::WriteAllText($config, ($text -replace '("McpPort"\s*:\s*)\d+', "`${1}$Port"))

if (-not $AiOnly -and $running.Count -eq 0) {
    Start-Process $smapi -WorkingDirectory $GamePath
    # 等第一个窗口把 SMAPI 日志占住，第二个才会写到 player-2 那份
    Start-Sleep -Seconds 5
}
Start-Process $smapi -ArgumentList '--mods-path "Mods-AI"' -WorkingDirectory $GamePath

Write-Host "AI 窗口的 MCP 地址：http://127.0.0.1:$Port/mcp" -ForegroundColor Green
Write-Host @"
接下来：
1. 你的窗口：合作 → 主持，新建或载入联机农场（要有空小屋）。
2. AI 的窗口：先点一下让它在前台（没进联机前失去焦点会暂停），合作 → 加入 → 输入 IP → 127.0.0.1，选空的农场工人。
3. DSH：插件设置里把端口改成 $Port，重启 DSH。之后单人玩记得改回去。
"@
