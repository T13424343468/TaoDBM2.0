﻿# navicat-walk.ps1 — 竞品对照走查 v6：走完首启向导进真正主界面
# v5 实锤：点「试用」(717,515) 成功进主界面，但停在首启向导第一页「界面布景主题」（浅色/深色，右下「下一步」）。
# 本轮：点「试用」→ 连点「下一步/完成」(869,669) 走完向导（v5 帧定谳坐标，1024x768）→ 真正主界面停留 → Ctrl+N 新建连接表单 → 收场。全程录屏由 workflow 侧 ffmpeg 承担。
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative6 {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Focus-Navicat {
  $p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
    $t = Add-Type -MemberDefinition $sig -Name FG6 -Namespace W6 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Write-Host "[WALK] v6 start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动，等首启弹窗（v4 实锤约 30-50s 内出「试用提醒」）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 50
Focus-Navicat | Out-Null

# 2) 点「试用」进主界面（v4 定谳坐标 717,515；1024x768）
[MouseNative6]::Click(717, 515)
Start-Sleep -Seconds 20
$p = Focus-Navicat
if ($p) { Write-Host "[WALK] after trial click window='$($p.MainWindowTitle)'" }

# 3) 首启向导：连点「下一步/完成」(869,669)（v5 帧定谳：640 宽下 (543,418) x1.6）。
#    向导页数未知，点 4 次覆盖主题→后续页→完成；每点一次报窗口标题，末页按钮变「完成」同位。
for ($i = 1; $i -le 4; $i++) {
  [MouseNative6]::Click(869, 669)
  Start-Sleep -Seconds 6
  $p = Focus-Navicat
  Write-Host "[WALK] wizard click $i window='$($p.MainWindowTitle)'"
}

# 4) 真正主界面停留 12 秒（入镜：左侧连接区/顶部工具栏）
Start-Sleep -Seconds 12

# 5) 新建连接（Ctrl+N），停 15 秒展示连接表单
[System.Windows.Forms.SendKeys]::SendWait("^n")
Start-Sleep -Seconds 15

# 6) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 5
Write-Host "[WALK] v6 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
