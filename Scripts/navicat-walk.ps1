# navicat-walk.ps1 — 竞品对照走查 v4：Navicat Premium 试用版第一轮被动录屏
# 2026-11-25 老板令：Premium 有免费体验期不需要注册；第一轮只看不点——启动等 60s 看弹什么，
# 全程不点击不按键，把首启真实弹窗（试用期提示/登录框/更新框）原样录下来，确认后再定下一轮点哪。
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "[WALK] v4 passive start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动（闪屏＋首启弹窗全程入镜）
Start-Process -FilePath $ExePath

# 2) 被动观察 90 秒：不点击不按键，让所有首启弹窗自己弹出来（50s 处报一次进程态）
Start-Sleep -Seconds 50
$p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p) { Write-Host "[WALK] t+50s navicat running pid=$($p.Id) window=$($p.MainWindowTitle)" }
else { Write-Host "[WALK] t+50s navicat process not found" }
Start-Sleep -Seconds 40

# 3) 收场：报窗口标题供日志比对，不杀进程外的任何交互
$p2 = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p2) { Write-Host "[WALK] t+90s window='$($p2.MainWindowTitle)'" }
Write-Host "[WALK] v4 passive done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
