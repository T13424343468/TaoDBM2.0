# dbeaver-walk.ps1 v9 — 竞品对照走查（DBeaver CE）：录屏＋UI Automation 按控件名驱动首启向导
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Focus-DBeaver {
  $p = Get-Process -Name "dbeaver" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
    $t = Add-Type -MemberDefinition $sig -Name FG9 -Namespace W9 -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

function Find-Wizard {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Product Configuration")
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Invoke-ButtonByName([string]$NamePattern) {
  $w = Find-Wizard
  if (-not $w) { Write-Host "[UIA] wizard not found"; return $false }
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $btns = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($b in $btns) {
    Write-Host "[UIA] button: $($b.Current.Name) enabled=$($b.Current.IsEnabled)"
    if ($b.Current.Name -like $NamePattern -and $b.Current.IsEnabled) {
      try { ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Write-Host "[UIA] invoked: $($b.Current.Name)"; return $true } catch { Write-Host "[UIA] invoke failed: $_" }
    }
  }
  return $false
}

function Select-RadioByName([string]$NamePattern) {
  $w = Find-Wizard
  if (-not $w) { Write-Host "[UIA] wizard not found"; return $false }
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)
  $rads = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($r in $rads) {
    Write-Host "[UIA] radio: $($r.Current.Name) enabled=$($r.Current.IsEnabled)"
    if ($r.Current.Name -like $NamePattern) {
      try { ($r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Write-Host "[UIA] selected: $($r.Current.Name)"; return $true } catch { Write-Host "[UIA] select failed: $_" }
    }
  }
  return $false
}

Write-Host "[WALK] v9 start exe=$ExePath"
Start-Sleep -Seconds 5

# 1) 启动（闪屏＋首启向导全程入镜）
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 55

# 2) 第 1 页：UIA 点 Next
[void](Invoke-ButtonByName "Next*")
Start-Sleep -Seconds 5

# 3) 第 2 页 Data collection：UIA 选中「Do not share」单选项
[void](Select-RadioByName "*Do not share*")
Start-Sleep -Seconds 2

# 4) 逐页 UIA 点 Next，共 4 轮（覆盖后续可能的多页）
for ($i = 1; $i -le 4; $i++) {
  if (-not (Invoke-ButtonByName "Next*")) { Write-Host "[UIA] Next round $i not invoked" }
  Start-Sleep -Seconds 4
}

# 5) 末页 UIA 点 Apply，等 12 秒进主界面
[void](Invoke-ButtonByName "Apply")
Start-Sleep -Seconds 12

# 6) 主界面停留 10 秒（入镜）
Start-Sleep -Seconds 10

# 7) 新建连接向导 Ctrl+Shift+N，停 12 秒展示连接表单
Focus-DBeaver | Out-Null
Focus-DBeaver | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("^+n")
Start-Sleep -Seconds 12

# 8) 收场
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 4

Write-Host "[WALK] v9 done"
Get-Process -Name "dbeaver" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
