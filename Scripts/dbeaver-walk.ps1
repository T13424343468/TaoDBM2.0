# dbeaver-walk.ps1 v7 — 竞品对照走查（DBeaver CE）：录屏＋先选单选项再走完首启向导
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative7 {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Focus-DBeaver {
  $p = Get-Process -Name "dbeaver" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
    $t = Add-Type -MemberDefinition $sig -Name FG7 -Namespace W7 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Write-Host "[WALK] v7 start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动（闪屏＋首启向导全程入镜）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 55

# 2) 第 1 页点 Next（645,476）进第 2 页
Focus-DBeaver | Out-Null
[MouseNative7]::Click(645, 476)
Start-Sleep -Seconds 5

# 3) 第 2 页 Data collection：先点「Do not share」单选项（243,201），Next 才会亮
Focus-DBeaver | Out-Null
[MouseNative7]::Click(243, 201)
Start-Sleep -Seconds 2

# 4) 逐页点 Next＋回车兜底，共 4 轮（覆盖后续可能的多页）
for ($i = 1; $i -le 4; $i++) {
  Focus-DBeaver | Out-Null
  [MouseNative7]::Click(645, 476)
  Start-Sleep -Seconds 1
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
  Start-Sleep -Seconds 4
}

# 5) 末页点 Apply（737,476）＋回车兜底，等 12 秒进主界面
Focus-DBeaver | Out-Null
[MouseNative7]::Click(737, 476)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 12

# 6) 主界面停留 10 秒（入镜）
Start-Sleep -Seconds 10

# 7) 新建连接向导 Ctrl+Shift+N，停 12 秒展示连接表单
Focus-DBeaver | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("^+n")
Start-Sleep -Seconds 12

# 8) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 4

Write-Host "[WALK] v7 done"
Get-Process -Name "dbeaver" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
