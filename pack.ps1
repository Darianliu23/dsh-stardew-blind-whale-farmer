# 打发布用的压缩包 dist\BlindWhaleFarmer.zip（需要先运行 build.ps1）
# 不放 config.json：玩家更新时解压覆盖也不会清掉自己填的 API Key，第一次启动时 SMAPI 会自动生成默认设置。
# 也不放 readme 之类的说明文件（Mod 网站要求说明写在页面简介里）。guide\*.md 是 AI 运行时读的攻略，要带上。
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$src = Join-Path $PSScriptRoot "release\BlindWhaleFarmer"
$dist = Join-Path $PSScriptRoot "dist"
$zip = Join-Path $dist "BlindWhaleFarmer.zip"
New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path $zip) { Remove-Item $zip }

$files = @("$src\BlindWhaleFarmer.dll", "$src\manifest.json") + (Get-ChildItem "$src\guide\*.md" | ForEach-Object { $_.FullName })
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create, [Text.Encoding]::UTF8)
try {
    foreach ($f in $files) {
        $rel = $f.Substring($src.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f, "BlindWhaleFarmer/$rel") | Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "已打包 $zip（$((Get-Item $zip).Length) 字节，$($files.Count) 个文件）" -ForegroundColor Green
