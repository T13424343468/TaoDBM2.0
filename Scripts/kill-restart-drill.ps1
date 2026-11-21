# R2 进程级 kill/重启演练（盲区补齐 2026-11-21 老板令）
# 两阶段：seed 进程落状态后驻留 → 外部强杀模拟崩溃 → verify 新进程回读对账恢复。
# 用法：pwsh -NoProfile -File Scripts/kill-restart-drill.ps1 [-BinDir <dir containing TaoDBM.LogicTest.dll>] [-WorkDir <dir>]
# 退出码：0 = 演练全过；非 0 = 恢复对账有红或流程失败。
param(
    [string]$BinDir = "",
    [string]$WorkDir = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not $BinDir) {
    $cand = Get-ChildItem -Recurse -Filter TaoDBM.LogicTest.dll -Path Tests/TaoDBM.LogicTest/bin -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'Release' } | Select-Object -First 1
    if (-not $cand) { Write-Host "::error::TaoDBM.LogicTest.dll not found; build first"; exit 1 }
    $BinDir = $cand.DirectoryName
}
$dll = Join-Path $BinDir "TaoDBM.LogicTest.dll"
if (-not (Test-Path $dll)) { Write-Host "::error::missing $dll"; exit 1 }

if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("taodbm-kill-drill-" + [guid]::NewGuid().ToString("N")) }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$registry = Join-Path $WorkDir "sqlhandoff.registry"
Write-Host "[DRILL] workdir=$WorkDir"

function Start-Drill([string]$Phase) {
    # Windows：Start-Process -Environment（pwsh 7.4+）；其余：env 包装。绝不能不带环境变量起进程，
    # 否则会落进常规全量测试路径（约 5.5 分钟），演练语义全失。
    if ($IsWindows) {
        return Start-Process -FilePath "dotnet" -ArgumentList @($dll, $WorkDir) -PassThru -NoNewWindow `
            -Environment @{ TAODBM_DRILL = $Phase }
    }
    return Start-Process -FilePath "env" -ArgumentList @("TAODBM_DRILL=$Phase", "dotnet", $dll, $WorkDir) -PassThru -NoNewWindow
}

# ---- 阶段一：seed（落状态后驻留，等强杀）----
$seed = Start-Drill "seed"
$waited = 0
while (-not (Test-Path $registry) -and $waited -lt 60) { Start-Sleep -Seconds 1; $waited++ }
if (-not (Test-Path $registry)) {
    Write-Host "::error::seed did not register within 60s"
    try { Stop-Process -Id $seed.Id -Force -ErrorAction SilentlyContinue } catch { }
    exit 1
}
Write-Host "[DRILL] seed registered after ${waited}s, killing pid=$($seed.Id)"
Stop-Process -Id $seed.Id -Force   # 强杀 = 模拟崩溃，无任何清理机会
Start-Sleep -Seconds 2

# ---- 阶段二：verify（新进程回读对账）----
$verify = Start-Drill "verify"
Wait-Process -Id $verify.Id -ErrorAction SilentlyContinue
$code = $verify.ExitCode
Write-Host "[DRILL] verify exit=$code"
if ($code -eq 0) { Write-Host "[DRILL] kill/restart recovery PASS" } else { Write-Host "::error::kill/restart recovery FAILED" }
exit $code
