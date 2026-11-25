# navicat-walk.ps1 — 竞品对照走查 v5：Premium 试用版点「试用」进主界面
# 2026-11-25 v4 被动录屏实锤：首启弹「试用提醒 剩余 14 天」，按钮＝在线购买/注册/试用（老板指路免费体验期）。
# 本轮：启动→等弹窗→点「试用」(717,515)→主界面停留→Ctrl+N 新建连接表单展示→收场。全程录屏由 workflow 侧 ffmpeg 承担。
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative5 {
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
    $t = Add-Type -MemberDefinition $sig -Name FG5 -Namespace W5 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Write-Host "[WALK] v5 start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动，等首启弹窗（v4 实锤约 30-50s 内出「试用提醒」）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 50
Focus-Navicat | Out-Null

# 2) 点「试用」进主界面（v4 定谳坐标 717,515；1024x768）
[MouseNative5]::Click(717, 515)
Start-Sleep -Seconds 20
$p = Focus-Navicat
if ($p) { Write-Host "[WALK] after trial click window='$($p.MainWindowTitle)'" }

# 3) 主界面停留 10 秒（入镜：左侧连接区/顶部工具栏）
Start-Sleep -Seconds 10

# 4) 新建连接（Ctrl+N），停 15 秒展示连接表单
[System.Windows.Forms.SendKeys]::SendWait("^n")
Start-Sleep -Seconds 15

# 5) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 5
Write-Host "[WALK] v5 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
