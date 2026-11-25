# navicat-walk.ps1 — 竞品对照走查 v2：录屏模式（2026-11-25 老板令：不要截屏，用录屏）
# 口径：全程动作在 ffmpeg 桌面录屏下进行，视频即证据；动作间留停顿保证可看
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms

function Focus-Navicat {
  $p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
    $t = Add-Type -MemberDefinition $sig -Name FG2 -Namespace W2 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Write-Host "[WALK] video mode start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动 Navicat（首启全程入镜）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 45

# 2) 首启弹窗兜底：回车（默认按钮多为同意/确定），留 8 秒看清弹了什么
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 8

# 3) 关掉残余弹窗，亮出主界面
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 5

# 4) 新建连接（Ctrl+N），停 12 秒展示整个连接表单
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("^n")
Start-Sleep -Seconds 12

# 5) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 4

Write-Host "[WALK] done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
