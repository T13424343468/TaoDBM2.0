# navicat-walk.ps1 — 竞品对照走查 v3：录屏模式＋真鼠标点击（首启登录框尝试跳过）
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative3 {
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
    $t = Add-Type -MemberDefinition $sig -Name FG3 -Namespace W3 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Write-Host "[WALK] v3 start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动（闪屏＋登录框全程入镜）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 50

# 2) 登录框右上角 ✕ 尝试跳过（坐标按上轮录屏实拍 1024x768）
Focus-Navicat | Out-Null
[MouseNative3]::Click(877, 155)
Start-Sleep -Seconds 10

# 3) 若弹确认框，回车兜底；再 ESC 收干净
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 5
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 5

# 4) 主界面停留 8 秒（入镜）
Start-Sleep -Seconds 8

# 5) 新建连接（Ctrl+N），停 12 秒展示连接表单
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("^n")
Start-Sleep -Seconds 12

# 6) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 4

Write-Host "[WALK] v3 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
