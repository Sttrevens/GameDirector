#requires -Version 5.1
<#
.SYNOPSIS
  GameDirector Windows 首装预检（只读，不修改系统）。

.DESCRIPTION
  按产品的固定解析顺序核对 FFmpeg/ffprobe：环境变量覆盖 → PATH。
  验证工作台出片实际需要的编码器（libx264）与字幕滤镜（libass 的 subtitles）。
  同时提示 Unity 桥在 Windows 上的 urlacl 注意事项。
  退出码：0 = 全部通过；1 = 有缺失项（输出会给出修复动作）。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$failures = [System.Collections.Generic.List[string]]::new()

function Resolve-Tool([string]$name, [string]$envVar) {
    $override = [Environment]::GetEnvironmentVariable($envVar)
    if ($override) {
        if (Test-Path $override) { return @{ Path = $override; Source = $envVar } }
        Write-Host "[warn] $envVar 指向的文件不存在：$override（将继续尝试 PATH）"
    }
    $onPath = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($onPath) { return @{ Path = $onPath.Source; Source = 'PATH' } }
    return $null
}

Write-Host '== GameDirector Windows 预检 ==' -ForegroundColor Cyan

$ffmpeg = Resolve-Tool 'ffmpeg' 'GAMEDIRECTOR_FFMPEG'
if (-not $ffmpeg) {
    $failures.Add('未找到 ffmpeg。安装含 libx264+libass 的构建（例如 gyan.dev 的 release full），然后设置 GAMEDIRECTOR_FFMPEG 或加入 PATH。') | Out-Null
} else {
    Write-Host ("ffmpeg: {0}  ({1})" -f $ffmpeg.Path, $ffmpeg.Source)
    $encoders = & $ffmpeg.Path -hide_banner -encoders 2>$null
    if ($encoders -match '(?m)^\s*\S+\s+libx264\s') { Write-Host '  [ok] libx264 编码器可用' }
    else { $failures.Add('该 ffmpeg 没有 libx264 编码器，请换 full 构建。') | Out-Null }
    $filters = & $ffmpeg.Path -hide_banner -filters 2>$null
    if ($filters -match '(?m)^\s*\S+\s+subtitles\s') { Write-Host '  [ok] subtitles 滤镜（libass）可用' }
    else { $failures.Add('该 ffmpeg 没有 subtitles 滤镜（libass），中文字幕将无法合成；请换 full 构建。') | Out-Null }
}

$ffprobe = Resolve-Tool 'ffprobe' 'GAMEDIRECTOR_FFPROBE'
if (-not $ffprobe) {
    $failures.Add('未找到 ffprobe。它与 ffmpeg 同包提供，设置 GAMEDIRECTOR_FFPROBE 或加入 PATH。') | Out-Null
} else {
    & $ffprobe.Path -version *> $null
    if ($LASTEXITCODE -eq 0) { Write-Host ("ffprobe: {0}  ({1})" -f $ffprobe.Path, $ffprobe.Source) }
    else { $failures.Add("ffprobe 无法运行：$($ffprobe.Path)") | Out-Null }
}

# Unity 桥（Play Mode 内的 HttpListener）在 Windows 上使用 HTTP.SYS；
# 非管理员进程监听回环端口可能要求一次性 urlacl 授权。
$urlacl = netsh http show urlacl 2>$null | Select-String -SimpleMatch '127.0.0.1:39777'
if ($urlacl) { Write-Host '[ok] 已存在 39777 的 urlacl 授权' }
else {
    Write-Host '[提示] 未检测到 39777 的 urlacl 授权。若 Unity 进入 Play Mode 后桥启动报 Access is denied：'
    Write-Host '       以管理员运行一次：netsh http add urlacl url=http://127.0.0.1:39777/ user="%USERDOMAIN%\%USERNAME%"'
    Write-Host '       实测结束可用 netsh http delete urlacl 撤销。'
}

$unity = Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory -ErrorAction SilentlyContinue
if ($unity) { Write-Host ("Unity 版本：" + ($unity.Name -join ', ')) }
else { Write-Host '[warn] 未在默认位置发现 Unity Hub 编辑器安装。' }

if ($failures.Count -eq 0) {
    Write-Host "`n预检通过。接下来按 INSTALL.md 导入 tarball 候选包即可。" -ForegroundColor Green
    exit 0
}
Write-Host "`n待处理：" -ForegroundColor Yellow
$failures | ForEach-Object { Write-Host "  - $_" }
exit 1
