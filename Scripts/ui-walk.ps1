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
function Log([string]$m) { $walkLog.Add($m); Write-Host "[WALK] $m"; try { $walkLog | Set-Content ui-walk-log.txt -Encoding utf8 } catch { } }

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

# 定时炸弹探针（#83 死亡现场判定：六次死亡恒定落在应用启动后 45-60 秒，与步骤可能只是相关）：
# 静置 45 秒不做任何 UIA 操作并分段留证——死在静置期＝应用/环境时间引爆，与 S08 无关；死回 S08＝动作因果
Log "probe quiet-watch start (45s, no UIA)"
Push-Evidence "quiet-watch start"
for ($qw = 1; $qw -le 9; $qw++) {
  Start-Sleep -Seconds 5
  $proc.Refresh()
  if ($proc.HasExited) { Write-Host "::error::app exited during quiet-watch q$qw code=$($proc.ExitCode)"; exit 1 }
  if ($qw % 3 -eq 0) { Push-Evidence "quiet-watch q$qw" }
}
Log "probe quiet-watch done"
Push-Evidence "quiet-watch done"

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
function Get-WalkDiagnostics {
  # 红场诊断（#77 教训：云端日志锁在登录侧、runner 还会在红后回收）：当场把屏上窗口与程序日志写成注解，注解在取消后仍公开可见
  $out = New-Object System.Collections.Generic.List[string]
  try {
    $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    $names = @()
    foreach ($w in $wins) { $names += ("[{0}]" -f $w.Current.Name) }
    $out.Add("windows-on-screen: " + ($names -join ' '))
  } catch { $out.Add("window enum failed: $($_.Exception.Message)") }
  foreach ($lg in @('D:\TaoDBMData\TaoDBM\logs\taodbm.log', (Join-Path $env:LOCALAPPDATA 'TaoDBM\logs\taodbm.log'))) {
    try {
      if (Test-Path $lg) {
        $out.Add("applog $lg tail:")
        $tail = (Get-Content $lg -Tail 25) -join ' | '
        if ($tail.Length -gt 2900) { $tail = $tail.Substring($tail.Length - 2900) }
        $out.Add("APPLOG: $tail")
        break
      }
    } catch { $out.Add("applog read failed: $($_.Exception.Message)") }
  }
  return $out
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
function Push-Evidence([string]$tag) {
  # 证据分步落公开分支：机器死亡时最后一次成功推送即死亡现场（#79-#82 四轮六杀全死在走查中途，登录侧什么都拿不到）
  try {
    if (-not (Test-Path .git)) { return }
    Copy-Item ui-walk-log.txt ev-walklog.txt -Force -ErrorAction SilentlyContinue
    foreach ($lg in @('D:\TaoDBMData\TaoDBM\logs\taodbm.log', (Join-Path $env:LOCALAPPDATA 'TaoDBM\logs\taodbm.log'))) {
      if (Test-Path $lg) { Get-Content $lg -Tail 60 | Set-Content ev-applog.txt -Encoding utf8; break }
    }
    $amem = 0
    try { $script:proc.Refresh(); $amem = [int]($script:proc.WorkingSet64 / 1MB) } catch { }
    "tag=$tag time=$(Get-Date -Format o) appMem=$amem MB" | Add-Content ev-meta.txt
    git add ev-walklog.txt ev-applog.txt ev-meta.txt 2>$null
    git -c user.email=ci@taodbm.local -c user.name=TaoDBM-CI commit -m "evi $tag" 2>$null | Out-Null
    git push origin HEAD:ui-smoke-evidence --force 2>$null | Out-Null
  } catch { }
}
function Assert-AppAlive([string]$where) {
  # 就地定谳：应用一死立刻红场注解带 applog/屏上现场，不等下一撞（#79-#81 机器死在走查中途且无应用侧取证）
  try {
    $script:proc.Refresh()
    if ($script:proc.HasExited) {
      Write-Host "::error::app exited at $where code=$($script:proc.ExitCode)"
      Log "APP EXITED at $where exitcode=$($script:proc.ExitCode)"
      foreach ($d in (Get-WalkDiagnostics)) { Write-Host ("::error::" + $d) }
      exit 1
    }
  } catch { }
}
function Expand-TreeItem([string]$prefix, [int]$seconds) {
  # 扫描限缩到主窗 scope：原实现在全桌面 Descendants 上 600ms 轮询重扫 15 秒，是 #79-#81 走查中途机器死亡的头号嫌疑
  $scope = $desktop
  try { if ($script:mainRoot) { $scope = $script:mainRoot } } catch { }
  $deadline = (Get-Date).AddSeconds($seconds)
  $polls = 0
  while ((Get-Date) -lt $deadline) {
    try {
      $allEls = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($e in $allEls) {
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem' -and $e.Current.Name -like ("*" + $prefix + "*")) {
          try { ($e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() } catch { }
          Push-Evidence "expand-ok $prefix"
          return $e
        }
      }
    } catch { }
    Assert-AppAlive "expand $prefix"
    $polls++
    if ($polls % 8 -eq 0) { Push-Evidence "poll $prefix n$polls" }
    Start-Sleep -Milliseconds 600
  }
  return $null
}
function Select-TreeItem([string]$prefix, [int]$seconds) {
  $scope = $desktop
  try { if ($script:mainRoot) { $scope = $script:mainRoot } } catch { }
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $allEls = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
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
  # 步进进度注解：近期 #79/#80 机器回收死在走查中途且零红，公开注解只剩启动告警——每步起点留里程碑，
  # 注解上限约 10 条/步，恰好覆盖 S01-S10 关键窗（存盘 S05/树 S06 均在内），机器死前也能对出走到哪步；
  # 同时带应用内存读数并断言存活，把 S08 一带的静默死亡变成可读账
  Assert-AppAlive "step $id"
  $appMem = 0
  try { $script:proc.Refresh(); $appMem = [int]($script:proc.WorkingSet64 / 1MB) } catch { }
  Write-Host ("::notice::WALK step {0} start ({1}; app={2}MB)" -f $id, $feature, $appMem)
  Push-Evidence "step-start $id"
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
        # 保存→RebuildRoots 异步重建，单次读树会竞速（#76 S06 误报嫌疑）：轮询 10 秒等节点出现
        $needle = Tpl $s.value
        $txt = ''
        for ($i = 0; $i -lt 10; $i++) {
          $treeEl = Find-ElWait @{ automationId = 'ConnTree' } 6 $mainRoot
          $sc = if ($treeEl) { $treeEl } else { $mainRoot }
          $txt = Get-ScopeText $sc
          if ($txt.Contains($needle)) { break }
          Start-Sleep -Seconds 1
        }
        if (-not $txt.Contains($needle)) { throw "tree text missing: $needle" }
      }
      'tree-contains-all' {
        $needles = @($s.value | ForEach-Object { Tpl ([string]$_) })
        $txt = ''
        for ($i = 0; $i -lt 10; $i++) {
          $treeEl = Find-ElWait @{ automationId = 'ConnTree' } 6 $mainRoot
          $sc = if ($treeEl) { $treeEl } else { $mainRoot }
          $txt = Get-ScopeText $sc
          $allFound = $true
          foreach ($v in $needles) { if (-not $txt.Contains($v)) { $allFound = $false; break } }
          if ($allFound) { break }
          Start-Sleep -Seconds 1
        }
        foreach ($v in $needles) {
          if (-not $txt.Contains($v)) {
            # 真红入账：把树文本快照推上证据分支，定谳「表名缺失＝子级未渲染还是读树口径」
            $snap = ($txt -replace '\r?\n', ' | ')
            if ($snap.Length -gt 300) { $snap = $snap.Substring(0, 300) }
            Push-Evidence "treetxt $snap"
            throw "tree text missing: $v"
          }
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
      'expand-keyboard' {
        # dd6edf3 轮定谳（treetxt 快照）：树文本一直读的是主窗回退（Find-ElWait ConnTree 从未命中），
        # 主窗 FindAll 实际读得到「📁 main」——库节点在且已渲染，只是没展开（表名 orders 缺）；
        # 旧 kb 扫描在 null 的 ConnTree scope 上被跳过才报 none，且嵌套项 ControlType 未必是 TreeItem。
        # 本轮：扫描回退主窗、不限 ControlType 按名找 main 节点；有 ExpandCollapse 就单元素展开（S07 同款已验证安全），
        # 没有就 Select＋SetFocus＋{RIGHT}；S09 仍按真实表清单终审
        $sel = Select-TreeItem (Tpl $s.selectPrefix) 10
        if ($null -eq $sel) { throw "tree item not found: $(Tpl $s.selectPrefix)" }
        try { $sel.SetFocus() } catch { }
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
        Start-Sleep -Milliseconds 800
        $needle = (Tpl $s.childPrefix) -replace '^[^\s]+\s+', ''
        $dbState = 'notfound'
        $dbItem = $null
        $itemNames = @()
        try {
          $scanRoot = $mainRoot
          $all = $scanRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
          foreach ($e in $all) {
            try {
              $nm = $e.Current.Name
              if (-not [string]::IsNullOrWhiteSpace($nm)) {
                $itemNames += $nm
                if ($dbItem -eq $null -and $nm -like ("*" + $needle + "*")) {
                  $dbItem = $e
                  try { $dbState = ($e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState } catch { $dbState = 'no-pattern' }
                }
              }
            } catch { }
          }
        } catch { }
        Push-Evidence ("kb-tree-items " + (($itemNames | Select-Object -First 20) -join ' | '))
        Push-Evidence "kb-state $needle $dbState"
        if ($dbItem -and $dbState -ne 'Expanded') {
          # 单元素补一发展开：ExpandCollapse 可用则直接 Expand（S07 同款）；否则 Select＋SetFocus＋{RIGHT}
          try {
            ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
            Start-Sleep -Seconds 1
            $dbState = ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState
          } catch {
            try {
              ($dbItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            } catch { try { $dbItem.SetFocus() } catch { } }
            Start-Sleep -Milliseconds 400
            [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
            Start-Sleep -Seconds 1
            try { $dbState = ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState } catch { $dbState = 'kb-fallback' }
          }
          Push-Evidence "kb-state-after $needle $dbState"
        }
        Assert-AppAlive "expand-keyboard $(Tpl $s.selectPrefix)"
        Push-Evidence ("expand-kb-ok {0} child={1} state={2}" -f (Tpl $s.selectPrefix), $needle, $dbState)
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
    if ($outcome -eq 'fail' -and $critical) {
      $hardFail = $true
      Write-Host "::error::step $id $feature failed: $($_.Exception.Message)"
      # 红场当场取证为注解：登录侧与 runner 回收都动不了它（#77 教训）
      foreach ($d in (Get-WalkDiagnostics)) { Write-Host ("::error::" + $d) }
    }
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
        if ($m.Length -gt 2500) { $m = $m.Substring(0, 2500) }
        Log "EVT $($e.TimeCreated) [$($e.ProviderName)] $($m -replace "\r?\n", ' | ')"
      }
    } catch { Log "eventlog read failed: $($_.Exception.Message)" }
    # 应用崩溃日志（crash.log 双根兜底）：未处理异常文本直接取证
    foreach ($clog in @('D:\TaoDBMData\TaoDBM\crash.log', (Join-Path $env:LOCALAPPDATA 'TaoDBM\crash.log'))) {
      try {
        if (Test-Path $clog) {
          Log "crashlog $clog (tail 40):"
          Get-Content $clog -Tail 40 | ForEach-Object { Log "CRASHLOG $_" }
        }
      } catch { Log "crashlog read failed: $($_.Exception.Message)" }
    }
    $hardFail = $true; break
  }
}

# ---------- 6. 收档 ----------
"feature-level coverage account:" | Add-Content ui-coverage.txt -Encoding utf8
$funcCov | Add-Content ui-coverage.txt -Encoding utf8
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
$walkLog | Set-Content ui-walk-log.txt -Encoding utf8
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
