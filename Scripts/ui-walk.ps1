# ui-walk.ps1 — 走页冒烟（v3.12 视觉出图强制 + 界面自动化覆盖率首账）
# 流程：真启动 -> 主界面截图 -> UIA 遍历可点入口（覆盖率账）-> 逐页点开逐张截图 -> 留档退出
# 闸口：主窗口未起或主界面截图失败 = 红；各页点开为软步（结果如实记档，点不开记覆盖缺口，不拦整场）
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$src = 'using System; using System.Runtime.InteropServices; public class WinNative { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); }'
Add-Type -TypeDefinition $src

$shots = New-Object System.Collections.Generic.List[string]
$walkLog = New-Object System.Collections.Generic.List[string]
function Log([string]$m) { $walkLog.Add($m); Write-Host "[WALK] $m" }

function Save-Shot([string]$name) {
  $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap($vs.Width, $vs.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $path = Join-Path $PWD $name
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  $shots.Add($name)
  Log "shot $name $((Get-Item $path).Length) bytes, screen $([Math]::Round($vs.Width))x$([Math]::Round($vs.Height))"
}

# 1. 真启动
$dir = Split-Path $ExePath -Parent
$proc = Start-Process $ExePath -WorkingDirectory $dir -PassThru
$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Milliseconds 500
  $proc.Refresh()
  if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) {
  if ($proc.HasExited) { Write-Host "::error::app exited early code=$($proc.ExitCode)" } else { Write-Host "::error::no main window after 30s" }
  exit 1
}
[WinNative]::ShowWindow($handle, 3) | Out-Null
[WinNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Seconds 8
$proc.Refresh()
if ($proc.HasExited) { Write-Host "::error::app exited before walk code=$($proc.ExitCode)"; exit 1 }

# 2. 主界面截图（硬闸）
Save-Shot "shot-01-start.png"

# 3. UIA 覆盖率首账：主窗口内所有具名可交互控件
try {
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
  $cond = [System.Windows.Automation.Condition]::TrueCondition
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $cov = New-Object System.Collections.Generic.List[string]
  $named = 0
  foreach ($el in $all) {
    $nm = $el.Current.Name
    if (-not [string]::IsNullOrWhiteSpace($nm)) {
      $named++
      if ($cov.Count -lt 200) { $cov.Add("$($el.Current.ControlType.ProgrammaticName) | $($el.Current.IsOffscreen) | $nm") }
    }
  }
  "main-window named-interactive-elements: $named (visible-list capped 200)" | Set-Content ui-coverage.txt -Encoding utf8
  $cov | Add-Content ui-coverage.txt -Encoding utf8
  Log "coverage: $named named elements in main window"
} catch { Log "coverage enumeration failed: $($_.Exception.Message)"; "coverage enumeration failed: $($_.Exception.Message)" | Set-Content ui-coverage.txt -Encoding utf8 }

# 4. 逐页点开（软步：每个动作 try/catch，如实记档）
function Find-Button([System.Windows.Automation.AutomationElement]$scope, [string]$name) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
  return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function Invoke-Button($btn) {
  $inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $inv.Invoke()
}

function Close-ExtraWindows($mainHwnd) {
  # 关掉主窗口以外的弹窗（按窗口模式），失败再补 ESC
  try {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $wins) {
      if ($w.Current.NativeWindowHandle -ne 0 -and $w.Current.NativeWindowHandle -ne $mainHwnd -and $w.Current.Name -notmatch 'Program Manager|Windows') {
        try { ($w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() } catch { }
      }
    }
  } catch { }
  Start-Sleep -Milliseconds 800
  [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
  Start-Sleep -Milliseconds 500
}

$pageSeq = @(
  @{ name = 'connect';  btn = '新建连接'; shot = 'shot-02-connect.png' },
  @{ name = 'theme';    btn = '主题';     shot = 'shot-03-theme.png' },
  @{ name = 'about';    btn = '关于';     shot = 'shot-04-about.png' }
)
try { $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($handle) } catch { $mainRoot = $null }
foreach ($pg in $pageSeq) {
  try {
    if (-not $mainRoot) { throw 'main root unavailable' }
    $btn = Find-Button $mainRoot $pg.btn
    if (-not $btn) { Log "page $($pg.name): button '$($pg.btn)' NOT reachable"; continue }
    Invoke-Button $btn
    Start-Sleep -Seconds 3
    Save-Shot $pg.shot
    Close-ExtraWindows $handle
    Log "page $($pg.name): opened via '$($pg.btn)', shot taken, closed"
  } catch {
    Log "page $($pg.name): FAILED $($_.Exception.Message)"
    Close-ExtraWindows $handle
  }
  $proc.Refresh()
  if ($proc.HasExited) { Write-Host "::error::app exited during walk at page $($pg.name) code=$($proc.ExitCode)"; break }
}

# 5. 收尾：退出码只由硬闸决定（主窗口+主界面截图），软步结果进档
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
"walk finished; shots=$($shots.Count -join ','); soft-page-results: " + ($walkLog -join ' ;; ') | Set-Content ui-walk-report.txt -Encoding utf8
Get-Content ui-walk-report.txt | ForEach-Object { Write-Host "[REPORT] $_" }
exit 0
