# ui-walk.ps1 — v3.13 走页冒烟·真库真用真对比（清单驱动）
# 流程：真启动 -> 主界面截图（硬闸）-> UIA 覆盖率首账 -> 加载走查清单逐项真操作
#       SQLite：真连接、真浏览表、真执行 SQL、结果与预期清单逐项比对、逐页截图留证
# 断言：critical 步骤失败或查询结果与预期不符 => exit 1（红场）；非关键步骤记档不拦场
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [string]$DbFile = "",
  [string]$Expected = "",
  [string]$PlanPath = ""
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$src = 'using System; using System.Runtime.InteropServices; public class WinNative { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); }'
Add-Type -TypeDefinition $src

$shots = New-Object System.Collections.Generic.List[string]
$walkLog = New-Object System.Collections.Generic.List[string]
$funcCov = New-Object System.Collections.Generic.List[string]
$hardFail = $false
function Log([string]$m) { $walkLog.Add($m); Write-Host "[WALK] $m" }

function Save-Shot([string]$name) {
  try {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($vs.Width, $vs.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $path = Join-Path $PWD $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    $shots.Add($name)
    Log "shot $name $((Get-Item $path).Length) bytes"
  } catch { Log "shot $name FAILED $($_.Exception.Message)" }
}

# ---------- 1. 真启动（硬闸） ----------
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

Save-Shot "shot-01-start.png"

# ---------- 2. 主窗口控件覆盖率首账 ----------
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

# ---------- 3. UIA 助手 ----------
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
function Find-El([System.Windows.Automation.AutomationElement]$scope, $find) {
  if ($null -eq $scope) { return $null }
  $cons = New-Object System.Collections.Generic.List[System.Windows.Automation.Condition]
  if ($find.automationId) {
    $cons.Add((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $find.automationId)))
  }
  if ($find.controlType) {
    $ct = $null
    try { $ct = [System.Windows.Automation.ControlType]::$($find.controlType) } catch { }
    if ($ct) { $cons.Add((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct))) }
  }
  if ($find.name) {
    $cons.Add((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $find.name)))
  }
  if ($cons.Count -eq 0) { return $null }
  $and = $null
  if ($cons.Count -eq 1) { $and = $cons[0] } else { $and = New-Object System.Windows.Automation.AndCondition($cons.ToArray()) }
  $el = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
  if ($null -ne $el) { return $el }
  # 精确名未命中 -> 按包含关系兜底（枚举过滤）
  if ($find.name) {
    try {
      $allEls = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($e in $allEls) {
        if ($e.Current.Name -like ("*" + $find.name + "*")) { return $e }
      }
    } catch { }
  }
  return $null
}
function Find-ElWait($find, [int]$seconds, $scope) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    $sc = $scope
    if ($null -eq $sc) { $sc = $desktop }
    $el = Find-El $sc $find
    if ($null -ne $el) { return $el }
    Start-Sleep -Milliseconds 500
  }
  return $null
}
function Get-ScopeText([System.Windows.Automation.AutomationElement]$scope) {
  $sb = New-Object System.Text.StringBuilder
  try {
    $allEls = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $allEls) {
      $nm = $e.Current.Name
      if (-not [string]::IsNullOrWhiteSpace($nm)) { [void]$sb.Append($nm); [void]$sb.Append(" | ") }
    }
  } catch { }
  return $sb.ToString()
}
function Invoke-El([System.Windows.Automation.AutomationElement]$el) {
  try { ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true } catch { }
  try { ($el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); return $true } catch { }
  try { $el.SetFocus(); return $true } catch { }
  return $false
}
function Close-ExtraWindows($mainHwnd) {
  try {
    $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
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
function Escape-SendKeys([string]$s) {
  # 单遍逐字符映射：先转义花括号再映射其余特殊符，避免二次转义
  $sb = New-Object System.Text.StringBuilder
  foreach ($c in $s.ToCharArray()) {
    switch ($c) {
      '{' { [void]$sb.Append('{{}') }
      '}' { [void]$sb.Append('{}}') }
      '+' { [void]$sb.Append('{+}') }
      '^' { [void]$sb.Append('{^}') }
      '%' { [void]$sb.Append('{%}') }
      '~' { [void]$sb.Append('{~}') }
      '(' { [void]$sb.Append('{(}') }
      ')' { [void]$sb.Append('{)}') }
      '[' { [void]$sb.Append('{[}') }
      ']' { [void]$sb.Append('{]}') }
      default { [void]$sb.Append($c) }
    }
  }
  return $sb.ToString()
}
function Set-TextValue([System.Windows.Automation.AutomationElement]$el, [string]$value) {
  $ok = $false
  try {
    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($value)
    $ok = $true
  } catch { }
  if (-not $ok) {
    try {
      $el.SetFocus()
      Start-Sleep -Milliseconds 300
      [System.Windows.Forms.SendKeys]::SendWait("^a")
      Start-Sleep -Milliseconds 150
      [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
      Start-Sleep -Milliseconds 150
      [System.Windows.Forms.SendKeys]::SendWait((Escape-SendKeys $value))
      $ok = $true
    } catch { }
  }
  return $ok
}
function Select-ComboItem([System.Windows.Automation.AutomationElement]$combo, [string]$itemName) {
  try { ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() } catch { }
  Start-Sleep -Milliseconds 1200
  $item = Find-ElWait @{ controlType = 'ListItem'; name = $itemName } 4 $null
  if ($null -eq $item) { return $false }
  try { ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); return $true } catch { }
  try { Invoke-El $item; return $true } catch { }
  return $false
}
function Expand-TreeItem([string]$prefix, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $allEls = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($e in $allEls) {
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem' -and $e.Current.Name -like ("*" + $prefix + "*")) {
          try { ($e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() } catch { }
          return $e
        }
      }
    } catch { }
    Start-Sleep -Milliseconds 600
  }
  return $null
}
function Select-TreeItem([string]$prefix, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $allEls = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($e in $allEls) {
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem' -and $e.Current.Name -like ("*" + $prefix + "*")) {
          try { ($e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() } catch { try { $e.SetFocus() } catch { } }
          return $e
        }
      }
    } catch { }
    Start-Sleep -Milliseconds 600
  }
  return $null
}
function New-DlgScope {
  try {
    $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    foreach ($w in $wins) {
      if ($w.Current.Name -match '新建连接|编辑连接') { return $w }
    }
  } catch { }
  return $desktop
}

# ---------- 4. 清单加载与模板替换 ----------
$mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
if ([string]::IsNullOrWhiteSpace($PlanPath) -or -not (Test-Path $PlanPath)) {
  Log "plan missing; walk ends after coverage account"
  "walk finished (no plan); shots=$($shots -join ',')" | Set-Content ui-walk-report.txt -Encoding utf8
  Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  exit 0
}
$plan = Get-Content $PlanPath -Raw -Encoding utf8 | ConvertFrom-Json
$connName = 'ui-auto-sqlite'
$tbl1 = 'orders'; $tbl2 = 'products'
$ordC = '5000'; $sumA = '22722500'; $nullE = '92'; $name7 = 'prod-0007'; $nullN = '454'
$exp = $null
if (-not [string]::IsNullOrWhiteSpace($Expected) -and (Test-Path $Expected)) {
  $exp = Get-Content $Expected -Raw -Encoding utf8 | ConvertFrom-Json
}
if ($exp) {
  $connName = [string]$exp.conn_name
  $tbl1 = [string]$exp.tables[0]
  $tbl2 = [string]$exp.tables[1]
  $ordC = [string]$exp.orders_count
  $sumA = [string]$exp.sum_amount
  $nullE = [string]$exp.null_email_count
  $name7 = [string]$exp.product7_name
  $nullN = [string]$exp.null_note_count
}
$tpl = @{
  '__DBFILE__'       = $DbFile
  '__CONN_NAME__'    = $connName
  '__TBL1__'         = $tbl1
  '__TBL2__'         = $tbl2
  '__ORDERS_COUNT__' = $ordC
  '__SUM_AMOUNT__'   = $sumA
  '__NULLEMAILS__'   = $nullE
  '__NAME7__'        = $name7
  '__NULLNOTES__'    = $nullN
}
function Tpl([string]$s) {
  $t = $s
  foreach ($k in $tpl.Keys) { $t = $t.Replace($k, [string]$tpl[$k]) }
  return $t
}
function Cov([string]$id, [string]$feature, [string]$outcome) {
  $funcCov.Add("[$id] $feature => $outcome")
  Log "coverage-func $id $feature => $outcome"
}

# ---------- 5. 清单解释器 ----------
$failed = $false
foreach ($s in $plan.steps) {
  $id = $s.id; $feature = Tpl ([string]$s.feature); $action = $s.action
  $critical = ($s.critical -eq $true) -or ($action -eq 'query')
  $allowMiss = ($s.allowMiss -eq $true)
  $outcome = 'pass'
  try {
    switch ($action) {
      'click' {
        $sc = New-DlgScope
        $el = Find-ElWait $s.find 8 $sc
        if ($null -eq $el) { throw "target not found" }
        if (-not (Invoke-El $el)) { throw "invoke failed" }
        if ($s.shot) { Start-Sleep -Seconds 2; Save-Shot (Tpl $s.shot) }
      }
      'setcombo' {
        $sc = New-DlgScope
        $el = Find-ElWait $s.find 8 $sc
        if ($null -eq $el) { throw "combo not found" }
        if (-not (Select-ComboItem $el (Tpl $s.value))) { throw "combo item select failed" }
        Start-Sleep -Milliseconds 1500
      }
      'setvalue' {
        $sc = New-DlgScope
        $el = Find-ElWait $s.find 8 $sc
        if ($null -eq $el) { throw "field not found" }
        if (-not (Set-TextValue $el (Tpl $s.value))) { throw "set value failed" }
      }
      'tree-contains' {
        $treeEl = Find-ElWait @{ automationId = 'ConnTree' } 6 $mainRoot
        $sc = if ($treeEl) { $treeEl } else { $mainRoot }
        $txt = Get-ScopeText $sc
        if (-not $txt.Contains((Tpl $s.value))) { throw "tree text missing: $(Tpl $s.value)" }
      }
      'tree-contains-all' {
        $treeEl = Find-ElWait @{ automationId = 'ConnTree' } 6 $mainRoot
        $sc = if ($treeEl) { $treeEl } else { $mainRoot }
        $txt = Get-ScopeText $sc
        foreach ($v in $s.value) {
          if (-not $txt.Contains((Tpl $v))) { throw "tree text missing: $(Tpl $v)" }
        }
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }
      'expand' {
        $el = Expand-TreeItem (Tpl $s.prefix) 15
        if ($null -eq $el) { throw "tree item not found: $(Tpl $s.prefix)" }
        $sleepSec = 3
        if ($s.sleepAfter) { $sleepSec = [int]$s.sleepAfter }
        Start-Sleep -Seconds $sleepSec
      }
      'select' {
        $el = Select-TreeItem (Tpl $s.prefix) 10
        if ($null -eq $el) { throw "tree item not found: $(Tpl $s.prefix)" }
        Start-Sleep -Seconds 1
      }
      'menu' {
        $btn = Find-ElWait @{ automationId = $s.buttonAutomationId } 6 $mainRoot
        if ($null -eq $btn) { $btn = Find-ElWait @{ controlType = 'Button'; name = $s.buttonAutomationId } 3 $mainRoot }
        if ($null -eq $btn) { throw "menu button not found: $($s.buttonAutomationId)" }
        Invoke-El $btn
        $item = Find-ElWait @{ name = (Tpl $s.itemName) } 8 $null
        if ($null -eq $item) { throw "menu item not found: $($s.itemName)" }
        Invoke-El $item
        Start-Sleep -Seconds 3
      }
      'query' {
        $ed = Find-ElWait @{ automationId = 'work-editor' } 15 $mainRoot
        if ($null -eq $ed) { throw "work-editor not found" }
        try { $ed.SetFocus() } catch { }
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait((Escape-SendKeys (Tpl $s.sql)))
        Start-Sleep -Milliseconds 500
        $run = Find-ElWait @{ automationId = 'work-run' } 6 $mainRoot
        if ($null -eq $run) { throw "work-run not found" }
        Invoke-El $run
        $expects = @()
        foreach ($v in $s.expectContains) { $expects += (Tpl $v) }
        $pageNeed = ""
        if ($s.pageInfoContains) { $pageNeed = Tpl $s.pageInfoContains }
        $deadline = (Get-Date).AddSeconds(25)
        $okRun = $false
        while ((Get-Date) -lt $deadline) {
          Start-Sleep -Milliseconds 700
          $msg = ""
          $msgEl = Find-El @{ automationId = 'work-message' } $mainRoot
          if ($msgEl) {
            try { $msg = ($msgEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Value } catch { }
            if ([string]::IsNullOrEmpty($msg)) { $msg = $msgEl.Current.Name }
          }
          if ($msg -match '错误：') { throw "execution error: $msg" }
          $resTxt = ""
          $resEl = Find-El @{ automationId = 'work-results' } $mainRoot
          if ($resEl) { $resTxt = Get-ScopeText $resEl }
          $allHit = $true
          foreach ($want in $expects) { if (-not $resTxt.Contains($want)) { $allHit = $false } }
          if ($allHit) {
            if ($pageNeed.Length -gt 0) {
              $pgEl = Find-El @{ automationId = 'work-pageinfo' } $mainRoot
              $pgName = ""
              if ($pgEl) { $pgName = $pgEl.Current.Name }
              if (-not $pgName.Contains($pageNeed)) { throw "pageinfo mismatch: got '$pgName' want '$pageNeed'" }
            }
            $okRun = $true
            break
          }
        }
        if (-not $okRun) { throw "query result mismatch: expected $(($expects -join ',')) not found in results" }
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }
      'export-csv' {
        $btn = Find-ElWait @{ automationId = 'work-csv' } 6 $mainRoot
        if ($null -eq $btn) { throw "work-csv not found" }
        Invoke-El $btn
        Start-Sleep -Seconds 4
        $picked = $false
        try {
          $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
          foreach ($w in $wins) {
            if ($w.Current.NativeWindowHandle -ne $handle -and $w.Current.NativeWindowHandle -ne 0) {
              try {
                $w.SetFocus()
                Start-Sleep -Milliseconds 500
                [System.Windows.Forms.SendKeys]::SendWait("^a")
                Start-Sleep -Milliseconds 150
                [System.Windows.Forms.SendKeys]::SendWait((Escape-SendKeys (Join-Path $PWD "ui-export-orders.csv")))
                Start-Sleep -Milliseconds 300
                [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
                $picked = $true
                break
              } catch { }
            }
          }
        } catch { }
        if (-not $picked) { throw "save picker not handled" }
        Start-Sleep -Seconds 3
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }
      'soft-page' {
        if ($s.page -eq 'theme') {
          $btn = Find-ElWait @{ automationId = 'BtnTheme' } 6 $mainRoot
          if ($null -eq $btn) { throw "theme button not reachable" }
          Invoke-El $btn
          Start-Sleep -Seconds 2
          $item = Find-ElWait @{ name = '天青' } 6 $null
          if ($null -eq $item) { throw "theme item not reachable" }
          Invoke-El $item
          Start-Sleep -Seconds 2
        } elseif ($s.page -eq 'about') {
          $btn = Find-ElWait @{ automationId = 'BtnAbout' } 6 $mainRoot
          if ($null -eq $btn) { throw "about button not reachable" }
          Invoke-El $btn
          Start-Sleep -Seconds 2
        }
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
        Close-ExtraWindows $handle
      }
      default { throw "unknown action: $action" }
    }
  } catch {
    $outcome = 'fail'
    if ($allowMiss) { $outcome = 'miss' }
    Log "step $id $feature => $outcome : $($_.Exception.Message)"
    if ($outcome -eq 'fail' -and $critical) { $hardFail = $true; Write-Host "::error::step $id $feature failed: $($_.Exception.Message)" }
    Close-ExtraWindows $handle
  }
  Cov $id $feature $outcome
  $proc.Refresh()
  if ($proc.HasExited) {
    Write-Host "::error::app exited during walk at step $id exitcode=$($proc.ExitCode)"
    Log "APP EXITED at step $id exitcode=$($proc.ExitCode)"
    Save-Shot "shot-99-death-$id.png"
    try {
      $evts = Get-WinEvent -LogName Application -MaxEvents 80 -ErrorAction Stop | Where-Object {
        $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') -or $_.Message -match 'TaoDBM'
      } | Select-Object -First 8
      foreach ($e in $evts) {
        $m = $e.Message
        if ($m.Length -gt 500) { $m = $m.Substring(0, 500) }
        Log "EVT $($e.TimeCreated) [$($e.ProviderName)] $($m -replace "\r?\n", ' | ')"
      }
    } catch { Log "eventlog read failed: $($_.Exception.Message)" }
    $hardFail = $true; break
  }
}

# ---------- 6. 收档 ----------
"feature-level coverage account:" | Add-Content ui-coverage.txt -Encoding utf8
$funcCov | Add-Content ui-coverage.txt -Encoding utf8
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
$summary = "walk finished; shots=" + ($shots -join ',') + "; hardFail=$hardFail; coverage: " + ($funcCov -join ' ;; ')
$summary | Set-Content ui-walk-report.txt -Encoding utf8
Get-Content ui-walk-report.txt | ForEach-Object { Write-Host "[REPORT] $_" }
if ($env:GITHUB_STEP_SUMMARY) {
  try {
    "## ui-walk walk log (public evidence)" | Add-Content $env:GITHUB_STEP_SUMMARY
    '```' | Add-Content $env:GITHUB_STEP_SUMMARY
    $walkLog | ForEach-Object { $_ -replace '<', '&lt;' | Add-Content $env:GITHUB_STEP_SUMMARY }
    '```' | Add-Content $env:GITHUB_STEP_SUMMARY
    "## ui-walk feature coverage" | Add-Content $env:GITHUB_STEP_SUMMARY
    '```' | Add-Content $env:GITHUB_STEP_SUMMARY
    $funcCov | ForEach-Object { $_ -replace '<', '&lt;' | Add-Content $env:GITHUB_STEP_SUMMARY }
    '```' | Add-Content $env:GITHUB_STEP_SUMMARY
  } catch { }
}
if ($hardFail) { exit 1 }
exit 0
