# navicat-walk.ps1 — 竞品对照走查 v9：单 BOM 修复（v6-v8 双 BOM 实锤 param 失效 ExePath 恒空）
# v6 教训：runner 控制台全程压在桌面上，Navicat 窗口一次都没到前台，整段视频被控制台挡住。
# 本轮：① 脚本开头最小化自身控制台；② 启动后轮询 navicat 进程与窗口标题并落 walk-log.txt（workflow 随视频推 shots 分支）；
# ③ 点「试用」(717,515) → 连点「下一步/完成」(869,669) 走完向导 → 真正主界面 → Ctrl+N 新建连接表单 → 收场。
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

function Log([string]$m) {
  $line = "[WALK] $m"
  Write-Host $line
  Add-Content -Path "walk-log.txt" -Value $line -Encoding utf8
}

# 0) 最小化自身控制台，避免挡住录屏（v6 教训）
$sig = @'
using System;
using System.Runtime.InteropServices;
public class Win9 {
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@
Add-Type -TypeDefinition $sig
[Win9]::ShowWindow([Win9]::GetConsoleWindow(), 6) | Out-Null  # 6 = SW_MINIMIZE
Log "console minimized"

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative9 {
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
    [Win9]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Log "v9 start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动，轮询进程与窗口（v4/v5 实锤约 30-50s 内出「试用提醒」）
Start-Process -FilePath $ExePath
$seen = $false
for ($i = 0; $i -lt 12; $i++) {
  Start-Sleep -Seconds 5
  $ps = Get-Process -Name "navicat" -ErrorAction SilentlyContinue
  if ($ps) {
    $seen = $true
    $titles = ($ps | ForEach-Object { "'$($_.MainWindowTitle)'" }) -join ','
    Log "t+$((($i+1)*5))s proc count=$(@($ps).Count) titles=$titles"
  }
}
if (-not $seen) { Log "ERROR navicat process never appeared"; exit 1 }
Focus-Navicat | Out-Null

# 2) 点「试用」进主界面（v4 定谳坐标 717,515；1024x768）
[MouseNative9]::Click(717, 515)
Start-Sleep -Seconds 20
$p = Focus-Navicat
Log "after trial click window='$($p.MainWindowTitle)'"

# 3) 首启向导：连点「下一步/完成」(869,669)（v5 帧定谳：640 宽下 (543,418) x1.6）。
#    向导页数未知，点 4 次覆盖主题→后续页→完成；每点一次报窗口标题，末页按钮变「完成」同位。
for ($i = 1; $i -le 4; $i++) {
  [MouseNative9]::Click(869, 669)
  Start-Sleep -Seconds 6
  $p = Focus-Navicat
  Log "wizard click $i window='$($p.MainWindowTitle)'"
}

# 4) 真正主界面停留 12 秒（入镜：左侧连接区/顶部工具栏）
Start-Sleep -Seconds 12

# 5) 新建连接（Ctrl+N），停 15 秒展示连接表单
[System.Windows.Forms.SendKeys]::SendWait("^n")
Start-Sleep -Seconds 15
$p = Focus-Navicat
Log "after ctrl+n window='$($p.MainWindowTitle)'"

# 6) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 5
Log "v9 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
