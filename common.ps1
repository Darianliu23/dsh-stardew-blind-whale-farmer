# build.ps1 / install.ps1 / test.ps1 共用：读取本机设置，找 dotnet 和游戏目录
# 本机专用的路径写在 local.ps1（不提交到 git，参考 local.ps1.example）

$localSettings = Join-Path $PSScriptRoot "local.ps1"
if (Test-Path $localSettings) { . $localSettings }

# dotnet：优先 local.ps1 里的 DOTNET_EXE，否则用 PATH 里的 dotnet（需要 .NET 6 或更新的 SDK）
$Dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { "dotnet" }

function Find-GamePath {
    if ($env:STARDEW_PATH) { return $env:STARDEW_PATH }
    $candidates = @()
    # Steam：注册表里的安装位置 + libraryfolders.vdf 里登记的其他库（比如装在别的盘）
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if ($steam) {
        $candidates += Join-Path $steam "steamapps\common\Stardew Valley"
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $candidates += Join-Path ($m.Groups[1].Value -replace '\\\\', '\') "steamapps\common\Stardew Valley"
            }
        }
    }
    $candidates += "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
    $candidates += "C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley"
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "Stardew Valley.dll")) { return $c }
    }
    return $null
}

$GamePath = Find-GamePath
if (-not $GamePath) {
    Write-Host "找不到星露谷的安装目录。请在 local.ps1 里设置 `$env:STARDEW_PATH（参考 local.ps1.example）。" -ForegroundColor Yellow
    exit 1
}

# 传给 MSBuild 的路径参数（csproj 里的 GamePath / SmapiPath）
$MsbuildPaths = @("-p:GamePath=$GamePath")
if ($env:SMAPI_PATH) { $MsbuildPaths += "-p:SmapiPath=$env:SMAPI_PATH" }
