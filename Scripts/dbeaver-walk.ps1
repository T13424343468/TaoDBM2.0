# dbeaver-walk.ps1 — 竞品对照走查（DBeaver CE 免登录）：录屏模式
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

function Focus-DBeaver {
  $p = Get-Process -Name "dbeaver" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
    $t = Add-Type -MemberDefinition $sig -Name FG4 -Namespace W4 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Write-Host "[WALK] dbeaver start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动（首启向导全程入镜）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 50

# 2) 关掉可能的首启向导/提示
Focus-DBeaver | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 5

# 3) 主界面停留 10 秒
Start-Sleep -Seconds 10

# 4) 新建连接向导（Ctrl+Shift+N 不确定则菜单路径兜底：Alt 不展开，直接再 ESC）
Focus-DBeaver | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("^+n")
Start-Sleep -Seconds 10

# 5) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 4

Write-Host "[WALK] dbeaver done"
Get-Process -Name "dbeaver" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
