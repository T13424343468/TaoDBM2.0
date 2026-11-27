# ui-walk.ps1 — v3.13 走页冒烟·真库真用真对比（清单驱动）
# 流程：真启动 -> 主界面截图（硬闸）-> UIA 覆盖率首账 -> 加载走查清单逐项真操作
#       SQLite：真连接、真浏览表、真执行 SQL、结果与预期清单逐项比对、逐页截图留证
# 断言：critical 步骤失败或查询结果与预期不符 => exit 1（红场）；非关键步骤记档不拦场
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [string]$DbFile = "",
  [string]$Expected = "",
  [string]$PlanPath = "",
  [string]$SkipIds = "",
  [string]$HostName = "localhost",
  [string]$DbUser = "",
  [string]$DbPass = "",
  [string]$DbName = "",
  [switch]$ShotAll   # ux-sweep: every step saves a screenshot when set (off by default, unchanged behavior)
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$src = 'using System; using System.Runtime.InteropServices; public class WinNative { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder buf, int max); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowTextW(IntPtr hWnd, string text); [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hWnd, int id); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd); [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId); [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags); [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo); [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId(); }'
Add-Type -TypeDefinition $src
# ---------- 3b. 真鼠标点击（恢复自 b7fefa823，合并解决方向误删） ----------
# Tapped/DoubleTapped/ContextFlyout 只认真鼠标事件，UIA Invoke 打不进（双击表/排序/右键菜单均此路）
$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative {
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public MOUSEINPUT mi; }
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  static void One(uint down, uint up) {
    var a = new INPUT { type = 0 }; a.mi.dwFlags = down;
    var b = new INPUT { type = 0 }; b.mi.dwFlags = up;
    SendInput(2, new INPUT[] { a, b }, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Click(int x, int y, bool right, bool dbl) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(150);
    if (right) { One(RIGHTDOWN, RIGHTUP); return; }
    One(LEFTDOWN, LEFTUP);
    if (dbl) { System.Threading.Thread.Sleep(120); One(LEFTDOWN, LEFTUP); }
  }
  public static void Move(int x, int y) { SetCursorPos(x, y); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Click-ElMouse([System.Windows.Automation.AutomationElement]$el, [string]$mode) {
  $r = $el.Current.BoundingRectangle
  $tabRect = $null
  if ($r.IsEmpty) {
    # 发现79：TabView 关闭钮仅悬停页签时渲染，UIA 树里有节点但 BoundingRectangle 为空（云343 S66a/S66e 十段同因定谇）——先悬停页签祖先中心让关闭钮现形，再重读矩形
    try {
      $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
      $anc = $walker.GetParent($el)
      $hop = 0
      while ($null -ne $anc -and $hop -lt 6) {
        if ($anc.Current.ControlType.ProgrammaticName -eq 'ControlType.TabItem') {
          $tr = $anc.Current.BoundingRectangle
          if (-not $tr.IsEmpty) {
            $tabRect = $tr
            [MouseNative]::Move([int]($tr.X + $tr.Width / 2), [int]($tr.Y + $tr.Height / 2))
            Start-Sleep -Milliseconds 800
            $r = $el.Current.BoundingRectangle
            # 云346 追修：悬停后对原元素回读仍空（UIA 缓存不刷新，346 mysql-b 实锤）——对悬停页签重新 FindAll Button，取非空矩形者替换点击目标
            if ($r.IsEmpty) {
              try {
                $btns2 = $anc.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
                foreach ($b2 in $btns2) {
                  $br2 = $b2.Current.BoundingRectangle
                  if (-not $br2.IsEmpty -and $br2.Width -gt 1 -and $br2.Height -gt 1) { $el = $b2; $r = $br2; break }
                }
              } catch { }
            }
          }
          break
        }
        $anc = $walker.GetParent($anc)
        $hop++
      }
    } catch { }
  }
  if ($r.IsEmpty) {
    # 云345 追修：悬停后 UIA 仍回读空矩形（截图实锤关闭钮可见而 UIA 不刷新）——按页签矩形右缘几何点击兜底（TabView 关闭钮贴页签右缘，中心约在右缘内 14px）
    if ($null -ne $tabRect -and -not $tabRect.IsEmpty) {
      $x = [int]($tabRect.Right - 14)
      $y = [int]($tabRect.Y + $tabRect.Height / 2)
      [MouseNative]::Click($x, $y, ($mode -eq 'right'), ($mode -eq 'double'))
      return
    }
    # 云401 取证（mysql 三作业 S66a 关闭钮与页签矩形全空）：枚举全部页签名与矩形入账，定谝溢出未渲染 vs 页签不在树
    try {
      $tabsAll = $mainRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)))
      $tinfo = @()
      foreach ($t in $tabsAll) { try { $tr2 = $t.Current.BoundingRectangle; $tinfo += ("{0}[{1}x{2}@{3},{4}]" -f $t.Current.Name, [int]$tr2.Width, [int]$tr2.Height, [int]$tr2.X, [int]$tr2.Y) } catch { } }
      Push-Evidence ("empty-rect-tabs " + ($tinfo -join ' | '))
    } catch { }
    throw "mouse target has empty rect"
  }
  $x = [int]($r.X + $r.Width / 2)
  $y = [int]($r.Y + $r.Height / 2)
  [MouseNative]::Click($x, $y, ($mode -eq 'right'), ($mode -eq 'double'))
}
function Click-TreeItemText([System.Windows.Automation.AutomationElement]$el, [string]$mode) {
  # 子级树节点右键定谳（2026-11-24 二轮）：UIA TreeItem BoundingRectangle 是整行宽度，行中心点落在文本右侧空白＝点在树背景上，菜单不开；
  # 连接节点文本长、行中心恰在文本上才侥幸成功。改为优先点节点内 Text 子元素中心，兜底点左缘+60px；先 ScrollIntoView 防虚拟化偏移。
  try {
    ($el.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView()
    Start-Sleep -Milliseconds 400
  } catch { }
  $r = $el.Current.BoundingRectangle
  if ($r.IsEmpty) { throw "mouse target has empty rect" }
  $tx = $null
  try {
    $texts = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) {
      $tr = $t.Current.BoundingRectangle
      if (-not $tr.IsEmpty -and $tr.Width -gt 1) { $tx = $tr; break }
    }
  } catch { }
  if ($null -ne $tx) {
    $x = [int]($tx.X + $tx.Width / 2)
    $y = [int]($tx.Y + $tx.Height / 2)
  } else {
    $x = [int]($r.X + [Math]::Min($r.Width / 2, 60))
    $y = [int]($r.Y + $r.Height / 2)
  }
  [MouseNative]::Click($x, $y, ($mode -eq 'right'), ($mode -eq 'double'))
}

function Get-AppLogTail([int]$n) {
  foreach ($lg in @('D:\TaoDBMData\TaoDBM\logs\taodbm.log', (Join-Path $env:LOCALAPPDATA 'TaoDBM\logs\taodbm.log'))) {
    if (Test-Path $lg) { return (Get-Content $lg -Tail $n) }
  }
  return @()
}


$shots = New-Object System.Collections.Generic.List[string]
$walkLog = New-Object System.Collections.Generic.List[string]
$funcCov = New-Object System.Collections.Generic.List[string]
$hardFail = $false
function Log([string]$m) { $walkLog.Add($m); Write-Host "[WALK] $m"; try { $walkLog | Set-Content ui-walk-log.txt -Encoding utf8 } catch { } }

function Force-AppForeground([string]$why) {
  # #322 定谳：裸 SetForegroundWindow 被前台锁静默拒绝（截图实锤宿主终端整窗盖住应用，键鼠全落终端）——AttachThreadInput 强制夺回并复核
  for ($att = 1; $att -le 2; $att++) {
    try {
      $fgHwnd = [WinNative]::GetForegroundWindow()
      if ($fgHwnd -eq $handle) { return $true }
      $fgProcId = [uint32]0
      $tidFg = [WinNative]::GetWindowThreadProcessId($fgHwnd, [ref]$fgProcId)
      $tidCur = [WinNative]::GetCurrentThreadId()
      $attached = $false
      if ($fgHwnd -ne [IntPtr]::Zero -and $tidFg -ne $tidCur) { $attached = [WinNative]::AttachThreadInput($tidCur, $tidFg, $true) }
      try {
        [WinNative]::ShowWindow($handle, 9) | Out-Null
        [WinNative]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 0, 0, 0x0043) | Out-Null
        [WinNative]::SetForegroundWindow($handle) | Out-Null
      } finally {
        if ($attached) { [WinNative]::AttachThreadInput($tidCur, $tidFg, $false) | Out-Null }
      }
      Start-Sleep -Milliseconds 250
      if ([WinNative]::GetForegroundWindow() -eq $handle) { return $true }
    } catch { }
    # 第二发前敲一下 ALT（模拟用户输入解除前台锁限制）
    try { [WinNative]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [WinNative]::keybd_event(0x12, 0, 2, [IntPtr]::Zero) } catch { }
    Start-Sleep -Milliseconds 150
  }
  Log "foreground force failed ($why)"
  Push-Evidence "fg-force-failed $why"
  return $false
}

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
Force-AppForeground 'startup' | Out-Null
Start-Sleep -Seconds 8
$proc.Refresh()
if ($proc.HasExited) { Write-Host "::error::app exited before walk code=$($proc.ExitCode)"; exit 1 }

# 云机器死亡时钟实锤（run 82/83/93/94 四轮六杀，job 起点 1.5-3.5 分钟随机回收，与步骤无关）：
# 静置探针缩到 10 秒仅保留最小时间相关数据点，把走查窗口让给 S12-S19 定谳
Log "probe quiet-watch start (10s, no UIA)"
Push-Evidence "quiet-watch start"
for ($qw = 1; $qw -le 2; $qw++) {
  Start-Sleep -Seconds 5
  $proc.Refresh()
  if ($proc.HasExited) { Write-Host "::error::app exited during quiet-watch q$qw code=$($proc.ExitCode)"; exit 1 }
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
  # 发现78：nameAny 此前未实现，find 静默退化成「任意第一个 Button」（S66a 点到无关按钮，未保存确认框永不出现）——多候选名依次精确→包含
  if ($find.nameAny) {
    foreach ($n in $find.nameAny) {
      $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, [string]$n)
      $el = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
      if ($null -ne $el) { return $el }
    }
    try {
      $allEls = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($n in $find.nameAny) {
        foreach ($e in $allEls) {
          if ($e.Current.Name -like ("*" + $n + "*")) { return $e }
        }
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

function Open-TreeContextFlyout([System.Windows.Automation.AutomationElement]$treeItem, [string]$needle, [string]$label) {
  # 右键弹层三段式（2026-11-24 二轮定谳）：直接右键 → 左键选中再右键 → Shift+F10；每段失败记录屏上窗口名，弹层开没开当场留证
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    if ($attempt -eq 1) {
      Click-TreeItemText $treeItem 'right'
    } elseif ($attempt -eq 2) {
      Log "ctx retry $label : select-then-rightclick"
      try { Invoke-El $treeItem | Out-Null } catch { }
      Start-Sleep -Milliseconds 600
      Click-TreeItemText $treeItem 'right'
    } else {
      Log "ctx retry $label : shift-f10"
      try { Invoke-El $treeItem | Out-Null } catch { }
      Start-Sleep -Milliseconds 600
      [System.Windows.Forms.SendKeys]::SendWait("+{F10}")
    }
    Start-Sleep -Seconds 1
    $mi = Find-ElWait @{ name = $needle } 4 $mainRoot
    if ($null -eq $mi) { $mi = Find-ElWait @{ name = $needle } 3 $null }
    if ($null -ne $mi) { return $mi }
    try {
      $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
      $names = @(); foreach ($w in $wins) { $names += ("[{0}]" -f $w.Current.Name) }
      Log ("ctx miss $label attempt $attempt windows-on-screen: " + ($names -join ' '))
      Push-Evidence ("ctx-miss $label attempt $attempt windows: " + ($names -join ' '))
    } catch { }
    try { [System.Windows.Forms.SendKeys]::SendWait("{ESC}") } catch { }
    Start-Sleep -Milliseconds 500
  }
  return $null
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
  # #250 定谳: 桌面级 UIA FindAll 是进程级 COM 崩溃点(#201 同类), seg4b/c2b x2 软页步同点死亡(#222/#248/#249/#250 四轮);
  # 热点路径零 UIA: 纯 user32 FindWindowExW 顶层游走 + WM_CLOSE 关外来窗口, 切记跳过自身进程窗口
  try {
    $hScan = [IntPtr]::Zero
    do {
      $hScan = [WinNative]::FindWindowExW([IntPtr]::Zero, $hScan, $null, $null)
      if ($hScan -ne [IntPtr]::Zero -and $hScan -ne $mainHwnd -and [WinNative]::IsWindowVisible($hScan)) {
        $cls = New-Object System.Text.StringBuilder 256
        [void][WinNative]::GetClassNameW($hScan, $cls, 256)
        $cn = $cls.ToString()
        $wpid = [uint32]0
        [void][WinNative]::GetWindowThreadProcessId($hScan, [ref]$wpid)
        if ($cn -notmatch 'Progman|WorkerW|Shell_TrayWnd|CoreWindow|ApplicationFrameWindow' -and $wpid -ne $PID) {
          $res = [IntPtr]::Zero
          [void][WinNative]::SendMessageTimeoutW($hScan, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 1000, [ref]$res)
        }
      }
    } while ($hScan -ne [IntPtr]::Zero)
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
function Read-EditorTextSafe([System.Windows.Automation.AutomationElement]$el, [System.Windows.Automation.AutomationElement]$root) {
  # #244 追修：并行高负载下 ValuePattern 回读偶发拿空——多路回读＋重试，全路拿不到返回 $null（读不回≠读到错）
  foreach ($round in 1..3) {
    try { $v = ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value; if ($v) { return $v } } catch { }
    try { $tp = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern); $v = $tp.DocumentRange.GetText(-1); if ($v) { return $v } } catch { }
    try { $n = $el.Current.Name; if ($n) { return $n } } catch { }
    if ($null -ne $root) {
      try {
        $el2 = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'work-editor')))
        if ($null -ne $el2) {
          try { $v = ($el2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value; if ($v) { return $v } } catch { }
        }
      } catch { }
    }
    Start-Sleep -Milliseconds 500
  }
  return $null
}
function Open-FreshQueryTab([System.Windows.Automation.AutomationElement]$root) {
  # #245 追修：第二查询步起 ^a 清空竞态实锤（DEL 吃旧句首字母→粘贴叠罗汉）——编辑器有内容时直接开新查询页签，绕开清空按键
  try {
    $btn = Find-ElWait @{ automationId = 'BtnLogo' } 6 $root
    if ($null -eq $btn) { return $null }
    $null = Invoke-El $btn
    $item = Find-ElWait @{ name = '新建查询' } 6 $root
    if ($null -eq $item) { $item = Find-ElWait @{ name = '新建查询' } 4 $null }
    if ($null -eq $item) { return $null }
    $null = Invoke-El $item
    Start-Sleep -Seconds 3
    $ed2 = Find-ElWait @{ automationId = 'work-editor' } 15 $root
    # #246 定谳：Invoke-El 布尔返回值未吸收→函数输出成 Object[]，下游单元素参数类型转换崩（S12/N12 全红段同型）；数组取末个（最新页签）
    if ($ed2 -is [System.Array]) { $ed2 = $ed2[-1] }
    return $ed2
  } catch { return $null }
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
    git add ev-shot-*.png 2>$null
    git -c user.email=ci@taodbm.local -c user.name=TaoDBM-CI commit -m "evi $tag" 2>$null | Out-Null
    # #154 定谁：矩阵作业共用 ui-smoke-evidence 时 --force 互相覆盖，MSSQL 现场被并跑作业顶掉（取证通道相撞）
    # 修法：WALK_EVIDENCE_BRANCH 按作业注入独立分支；未注入时保留旧通道兜底
    $evBranch = if ($env:WALK_EVIDENCE_BRANCH) { $env:WALK_EVIDENCE_BRANCH } else { 'ui-smoke-evidence' }
    git push origin HEAD:$evBranch --force 2>$null | Out-Null
    Write-Host ("[EV] push -> {0}" -f $evBranch)
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
  '__HOST__'         = $HostName
  '__USER__'         = $DbUser
  '__PASS__'         = $DbPass
  '__DBNAME__'       = $(if ([string]::IsNullOrEmpty($DbName)) { 'main' } else { $DbName })
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
  # 跳段走查（2026-11-19）：已定谳段可跳过，让尾部步骤在机器死亡窗口（走查起点 ~2.2 分钟）内完赛
  if ($SkipIds -split ',' -contains $id) {
    Log "step $id skipped (SkipIds)"
    Cov $id $feature 'skip'
    continue
  }
  # 步进进度注解：近期 #79/#80 机器回收死在走查中途且零红，公开注解只剩启动告警——每步起点留里程碑，
  # 注解上限约 10 条/步，恰好覆盖 S01-S10 关键窗（存盘 S05/树 S06 均在内），机器死前也能对出走到哪步；
  # 同时带应用内存读数并断言存活，把 S08 一带的静默死亡变成可读账
  Assert-AppAlive "step $id"
  Force-AppForeground $id | Out-Null
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
        if ($null -eq $el) {
          # 云401 取证（mssql 四作业 S05 保存钮 8 秒不现）：枚举主窗全部按钮名入账，定谝「对话框没开 vs 按钮不在树 vs 名字变了」
          try {
            $btns = $mainRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
            $bnames = @()
            foreach ($b in $btns) { try { $bn = $b.Current.Name; if (-not [string]::IsNullOrWhiteSpace($bn)) { $bnames += $bn } } catch { } }
            Push-Evidence ("click-fail-buttons " + (($bnames | Select-Object -First 40) -join ' | '))
          } catch { }
          try {
            $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
            $wnames = @()
            foreach ($w in $wins) { try { $wnames += $w.Current.Name } catch { } }
            Push-Evidence ("click-fail-windows " + (($wnames | Select-Object -First 10) -join ' | '))
          } catch { }
          throw "target not found"
        }
        if (-not (Invoke-El $el)) { throw "invoke failed" }
        if ($s.shot) { Start-Sleep -Seconds 2; Save-Shot (Tpl $s.shot) }
      }
      'setcombo' {
        # 云345 追修（mssql c2b 实锤：setcombo 显示 pass 而引擎仍 MySQL）——选择后回读选中项校验，不一致重试，3 次仍不一致才红
        $sc = New-DlgScope
        $el = Find-ElWait $s.find 8 $sc
        if ($null -eq $el) { throw "combo not found" }
        $want = (Tpl $s.value)
        $ok = $false
        $everRead = $false
        foreach ($attempt in 1..3) {
          if (-not (Select-ComboItem $el $want)) { Log "$id setcombo attempt $attempt select failed"; Start-Sleep -Milliseconds 800; continue }
          Start-Sleep -Milliseconds 1200
          $got = $null
          try { $sel = ($el.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection(); if ($sel.Length -gt 0) { $got = $sel[0].Current.Name } } catch { }
          if ($null -eq $got) { try { $got = ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { } }
          if ($null -eq $got) { continue }
          $everRead = $true
          if ($got -eq $want) { $ok = $true; break }
          Log "$id setcombo retry $attempt (readback got=$got want=$want)"
        }
        # 读得回但不一致＝真红；全程读不回＝脚本不可校验，留账接受不硬红
        if (-not $ok -and $everRead) { throw "combo set verify failed after retries (want=$want)" }
        if (-not $everRead) { Log "$id setcombo unreadable, accepted without verify" }
        Start-Sleep -Milliseconds 800
      }
      'setvalue' {
        # 云345 追修（mysql c2b 实锤：SetValue 竞态静默丢值，连接落库成空用户）——置文后回读校验，不一致换剪贴板粘贴法重试；读不回（密码框类）双法各置一次留账不硬红
        $sc = New-DlgScope
        $el = Find-ElWait $s.find 8 $sc
        if ($null -eq $el) { throw "field not found" }
        $want = (Tpl $s.value)
        $readable = $true
        foreach ($attempt in 1..3) {
          if (-not (Set-TextValue $el $want)) { Log "$id setvalue attempt $attempt Set-TextValue failed" }
          Start-Sleep -Milliseconds 600
          $got = $null
          try { $got = ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { }
          if ($null -eq $got) {
            # 首轮读不回＝字段不可回读（密码框类）：换剪贴板法再置一次，留账后接受（无法校验≠置失败）
            if ($readable) {
              $readable = $false
              Log "$id setvalue field unreadable, clipboard-paste fallback once"
              try {
                Set-Clipboard -Value $want
                $el.SetFocus()
                Start-Sleep -Milliseconds 300
                [System.Windows.Forms.SendKeys]::SendWait("^a")
                Start-Sleep -Milliseconds 150
                [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
                Start-Sleep -Milliseconds 150
                [System.Windows.Forms.SendKeys]::SendWait("^v")
                Start-Sleep -Milliseconds 600
              } catch { }
            }
            break
          }
          if ($got -eq $want) { break }
          Log "$id setvalue retry $attempt (readback got=$got want=$want)"
          try {
            Set-Clipboard -Value $want
            $el.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait("^a")
            Start-Sleep -Milliseconds 150
            [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
            Start-Sleep -Milliseconds 150
            [System.Windows.Forms.SendKeys]::SendWait("^v")
            Start-Sleep -Milliseconds 600
          } catch { }
        }
        # 可回读字段终审：3 轮后仍不一致＝真红；读不回（密码框类）不硬红
        if ($readable) {
          $fin = $null
          try { $fin = ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { }
          if ($null -ne $fin -and $fin -ne $want) { throw "set value verify failed (got=$fin want=$want)" }
        }
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
            # #153 取证先行（MSSQL S09 三轮同模式失败）：树全文 dump（限 4000 字符，不再 300 截断）
            # ＋ 应用日志装载/连接相关行入证据，定谳「装库没载入 vs 目录查询没返回 vs 读树口径」
            $snap = ($txt -replace '\r?\n', ' | ')
            if ($snap.Length -gt 4000) { $snap = $snap.Substring(0, 4000) }
            Push-Evidence "treetxt $snap"
            foreach ($lg in @('D:\TaoDBMData\TaoDBM\logs\taodbm.log', (Join-Path $env:LOCALAPPDATA 'TaoDBM\logs\taodbm.log'))) {
              if (Test-Path $lg) {
                $errLines = @(Get-Content $lg -Tail 200 | Where-Object { $_ -match '连接|装载|展开|错误|error|Exception|catalog|tree' })
                if ($errLines.Count -gt 0) { Push-Evidence ("applog-conn " + (($errLines | Select-Object -Last 20) -join ' || ')) }
                break
              }
            }
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
          # 3c05bf6 轮定谳：单元素 Expand() 打上后状态仍 Collapsed——UIA 展开对库子节点静默无效（连接根节点 S07 同款却成功）。
          # 有界自愈序列，每步复读状态入账：
          # A) Expand() 重试一发；B) Select＋SetFocus＋{RIGHT} 键盘；C) 再 {RIGHT}（焦点已在节点上时展开）
          try {
            ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
            Start-Sleep -Milliseconds 1500
            $dbState = ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState
          } catch { $dbState = 'expand-threw' }
          Push-Evidence "kb-state-afterA $needle $dbState"
          if ($dbState -ne 'Expanded') {
            try {
              try { ($dbItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() } catch { }
              try { $dbItem.SetFocus() } catch { }
              Start-Sleep -Milliseconds 400
              [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
              Start-Sleep -Milliseconds 1500
              try { $dbState = ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState } catch { $dbState = 'kbB-threw' }
            } catch { $dbState = 'kbB-threw' }
            Push-Evidence "kb-state-afterB $needle $dbState"
          }
          if ($dbState -ne 'Expanded') {
            try {
              [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
              Start-Sleep -Milliseconds 1500
              try { $dbState = ($dbItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState } catch { $dbState = 'kbC-threw' }
            } catch { $dbState = 'kbC-threw' }
            Push-Evidence "kb-state-afterC $needle $dbState"
          }
          # 终态复核：不论状态账如何，重新全名扫一遍看表名是否已落地
          try {
            $all2 = $mainRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            $names2 = @()
            foreach ($e2 in $all2) {
              try { $n2 = $e2.Current.Name; if (-not [string]::IsNullOrWhiteSpace($n2)) { $names2 += $n2 } } catch { }
            }
            Push-Evidence ("kb-tree-items-after " + (($names2 | Select-Object -First 24) -join ' | '))
          } catch { }
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
        # #88 S11 连三轮崩且 applog 零打点＝死点在步内未细分辨：找按钮/Invoke/找菜单项逐段留证（崩前最后一条即凶手段位）
        Push-Evidence "menu-find-start"
        $btn = Find-ElWait @{ automationId = $s.buttonAutomationId } 6 $mainRoot
        if ($null -eq $btn) { $btn = Find-ElWait @{ controlType = 'Button'; name = $s.buttonAutomationId } 3 $mainRoot }
        if ($null -eq $btn) { throw "menu button not found: $($s.buttonAutomationId)" }
        Push-Evidence "menu-button-found"
        Invoke-El $btn
        Push-Evidence "menu-button-invoked"
        # 菜单项先从主窗根找（弹层挂在应用窗 UIA 树内），桌面全根全扫是压舱雷，找不到再降级全根
        $item = Find-ElWait @{ name = (Tpl $s.itemName) } 6 $mainRoot
        if ($null -eq $item) { $item = Find-ElWait @{ name = (Tpl $s.itemName) } 4 $null }
        if ($null -eq $item) { throw "menu item not found: $($s.itemName)" }
        Push-Evidence "menu-item-found"
        Invoke-El $item
        Push-Evidence "menu-item-invoked"
        Start-Sleep -Seconds 3
        # #320 到达复核：计划声明 arrivalLog 时轮询 applog，未到达则回前台重开菜单补点一发（只补一次）
        if ($s.arrivalLog) {
          $needle = Tpl ([string]$s.arrivalLog)
          $arrived = $false
          for ($ai = 0; $ai -lt 5; $ai++) {
            if (((Get-AppLogTail 60) -join "`n").Contains($needle)) { $arrived = $true; break }
            Start-Sleep -Seconds 1
          }
          if (-not $arrived) {
            Log "$id menu arrival missing, retry once with foreground"
            Push-Evidence "$id menu-arrival-retry"
            Force-AppForeground $id | Out-Null
            Start-Sleep -Milliseconds 400
            Invoke-El $btn
            Start-Sleep -Milliseconds 600
            $item2 = Find-ElWait @{ name = (Tpl $s.itemName) } 6 $mainRoot
            if ($null -eq $item2) { $item2 = Find-ElWait @{ name = (Tpl $s.itemName) } 4 $null }
            if ($null -ne $item2) { Invoke-El $item2; Push-Evidence "$id menu-item-reinvoked" }
            Start-Sleep -Seconds 3
          }
        }
      }
      'query' {
        $ed = Find-ElWait @{ automationId = 'work-editor' } 15 $mainRoot
        if ($null -eq $ed) { throw "work-editor not found" }
        # #245 追修：编辑器已有内容（第二查询步起）先开新查询页签，杜绝 ^a/DEL 清空竞态叠罗汉
        $curContent = Read-EditorTextSafe $ed $mainRoot
        if ($null -ne $curContent -and $curContent.Trim().Length -gt 0) {
          Push-Evidence "$id editor-dirty, fresh tab"
          $fresh = Open-FreshQueryTab $mainRoot
          if ($null -ne $fresh) { $ed = $fresh }
        }
        try { $ed.SetFocus() } catch { }
        Start-Sleep -Milliseconds 400
        # S12/N12 定谳修复（2026-11-22）：逐字 SendKeys 打字不可靠（#241 两步同型红 expected 5000 not found in ledger）——
        # 改 UIA ValuePattern 整段设值＋回读校验，失败降级剪贴板整段粘贴，两轮重试后仍不一致才红，杜绝把错账当产品红
        $sqlText = Tpl $s.sql
        $setOk = $false
        foreach ($attempt in 1..3) {
          Set-TextValue $ed $sqlText
          Start-Sleep -Milliseconds 800
          $got = Read-EditorTextSafe $ed $mainRoot
          if ($got -eq $sqlText) { $setOk = $true; break }
          if ($null -eq $got) {
            # 读不回：脚本不可验证，不硬判产品红，交账本对账裁决；仍补剪贴板整段粘贴提高设值成功率
            Log "$id editor-verify retry $attempt unreadable (defer to ledger)"
            $fresh = Open-FreshQueryTab $mainRoot
            if ($null -ne $fresh) { $ed = $fresh }
            try {
              Set-Clipboard -Value $sqlText
              $ed.SetFocus()
              Start-Sleep -Milliseconds 300
              [System.Windows.Forms.SendKeys]::SendWait("^a")
              Start-Sleep -Milliseconds 150
              [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
              Start-Sleep -Milliseconds 150
              [System.Windows.Forms.SendKeys]::SendWait("^v")
            } catch { }
            Start-Sleep -Milliseconds 600
            continue
          }
          Log "$id editor-verify retry $attempt got=$got"
          # 读到错内容＝编辑器脏了，开新空页签再设值，不再用 ^a/DEL 清空（#245 叠罗汉实锤）
          $fresh = Open-FreshQueryTab $mainRoot
          if ($null -ne $fresh) { $ed = $fresh }
          try {
            Set-Clipboard -Value $sqlText
            $ed.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait("^a")
            Start-Sleep -Milliseconds 150
            [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
            Start-Sleep -Milliseconds 150
            [System.Windows.Forms.SendKeys]::SendWait("^v")
          } catch { }
          Start-Sleep -Milliseconds 600
        }
        if (-not $setOk) {
          $got = Read-EditorTextSafe $ed $mainRoot
          # 读不回＝脚本不可验证，交账本裁决；读到错内容才是硬红
          if ($null -ne $got -and $got -ne $sqlText) { throw "editor verify failed: sql mismatch after retries (got=$got)" }
          if ($null -eq $got) { Log "$id editor-verify final unreadable, deferring to ledger check" }
        }
        Start-Sleep -Milliseconds 300
        $run = Find-ElWait @{ automationId = 'work-run' } 6 $mainRoot
        if ($null -eq $run) { throw "work-run not found" }
        Invoke-El $run
        # 段位锚点：机器死在点击执行前后一眼可辨
        Push-Evidence "$id run-invoked"
        # 账本对账制（run 94/95/96 八连死实锤：机器集中死于点击执行后的 UIA 读结果轮询；产品执行实测仅 1ms 且截图健在）：
        # 彻底放弃点击后 UIA 轮询——睡 3 秒、截图、直接读应用日志 [执行账] 行对账，零 UIA
        $expects = @()
        foreach ($v in $s.expectContains) { $expects += (Tpl $v) }
        Start-Sleep -Seconds 3
        try { Save-Shot "ev-shot-result-$id.png" } catch { }
        Push-Evidence "$id result-shot"
        $ledger = $null
        foreach ($lg in @('D:\TaoDBMData\TaoDBM\logs\taodbm.log', (Join-Path $env:LOCALAPPDATA 'TaoDBM\logs\taodbm.log'))) {
          if (Test-Path $lg) {
            $ledger = (Get-Content $lg -Tail 40 | Where-Object { $_ -match '\[执行账\]' } | Select-Object -Last 1)
            break
          }
        }
        if (-not $ledger) { throw "execution ledger missing (no [执行账] line in applog)" }
        Log "$id ledger: $ledger"
        if ($ledger -match 'error=') { throw "execution error: $ledger" }
        $allHit = $true
        foreach ($want in $expects) { if (-not $ledger.Contains($want)) { $allHit = $false } }
        if (-not $allHit) { throw "query result mismatch: expected $(($expects -join ',')) not found in ledger" }
        Push-Evidence "$id ledger-hit"
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }
      'mouse-click' {
        # 真鼠标点击（left/right/double）：Tapped/DoubleTapped/ContextFlyout 只认实体鼠标事件
        $el = Find-ElWait $s.find 10 $mainRoot
        if ($null -eq $el) { throw "mouse target not found" }
        $mode = 'left'
        if ($s.button) { $mode = $s.button }
        Click-ElMouse $el $mode
        Start-Sleep -Seconds 2
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }
      'stop-query' {
        # #01 停止按钮实效断言：长流式查询执行中点停止 → [执行账] cancelled=1（置文走 Set-TextValue，#245 禁 ^a/DEL 叠层）
        $ed = Find-ElWait @{ automationId = 'work-editor' } 15 $mainRoot
        if ($null -eq $ed) { throw "work-editor not found" }
        try { $ed.SetFocus() } catch { }
        Start-Sleep -Milliseconds 400
        if (-not (Set-TextValue $ed (Tpl $s.sql))) { throw "editor set value failed" }
        Start-Sleep -Milliseconds 600
        $run = Find-ElWait @{ automationId = 'work-run' } 6 $mainRoot
        if ($null -eq $run) { throw "work-run not found" }
        Invoke-El $run
        Push-Evidence "$id run-invoked-before-stop"
        Start-Sleep -Seconds 2
        $stop = Find-ElWait @{ automationId = 'work-stop' } 6 $mainRoot
        if ($null -eq $stop) { throw "work-stop not found" }
        Invoke-El $stop
        Push-Evidence "$id stop-invoked"
        $deadline = (Get-Date).AddSeconds(20)
        $hit = $false
        do {
          $tail = (Get-AppLogTail 60) -join "`n"
          if ($tail.Contains('[执行账] cancelled=1')) { $hit = $true; break }
          Start-Sleep -Seconds 1
        } while ((Get-Date) -lt $deadline)
        if (-not $hit) { throw "stop not effective: no [执行账] cancelled=1 in applog" }
        Push-Evidence "$id cancelled-hit"
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }

      'export-csv' {
        # S17 四连死拆步版 v2（2026-11-20 夜批①）：UIA SetFocus 对该保存对话框必现 1.2s 循环失败（聚焦手段无效，11-19 定谳）。
        # 改走 Win32 句柄直取：GetClassName 过滤 #32770/Picker 类对话框 → FindWindowExW/GetDlgItem 定位文件名编辑框
        # → SetWindowTextW 写路径 + IDOK(=1) BM_CLICK 确认，全程零焦点依赖；句柄摸不到再回退键盘导航；
        # 仍不行由 allowMiss 弃步记 miss 续链（100s 守卫不变）
        $sw17 = [System.Diagnostics.Stopwatch]::StartNew()
        Push-Evidence "S17a start"
        $btn = Find-ElWait @{ automationId = 'work-csv' } 6 $mainRoot
        Push-Evidence "S17a btn-found=$($null -ne $btn)"
        if ($null -eq $btn) { throw "work-csv not found" }
        if ($sw17.Elapsed.TotalSeconds -gt 100) { throw "S17 guard exceeded before invoke" }
        Invoke-El $btn
        Push-Evidence "S17b invoked"
        Start-Sleep -Seconds 2
        if ($sw17.Elapsed.TotalSeconds -gt 100) { throw "S17 guard exceeded before dialog scan" }
        $picked = $false
        $script:s17logged = @()
        try {
          Push-Evidence "S17c dialog-scan-start"
          # run 202 定谳：UIA 桌面 FindAll 扫描是多轮走查暴毙统一死亡点（COM 崩溃无红账）；
          # 改纯 user32 EnumWindows 枚举顶层可见窗口，热点路径零 UIA
          # run 204: EnumWindows scriptblock delegate threw inside callback (S17c scan-err);
          # iterate top-level dialogs via FindWindowExW class walk instead - zero callback, zero UIA
          $dlgHwnds = New-Object System.Collections.Generic.List[IntPtr]
          $hScan = [IntPtr]::Zero
          do {
            $hScan = [WinNative]::FindWindowExW([IntPtr]::Zero, $hScan, '#32770', $null)
            if ($hScan -ne [IntPtr]::Zero -and [WinNative]::IsWindowVisible($hScan)) { $dlgHwnds.Add($hScan) | Out-Null }
          } while ($hScan -ne [IntPtr]::Zero)
          Push-Evidence "S17c dlg-count=$($dlgHwnds.Count)"
          foreach ($h in $dlgHwnds) {
            if ($sw17.Elapsed.TotalSeconds -gt 100) { Push-Evidence "S17c guard-break"; break }
            if ($h -eq $handle -or $h -eq [IntPtr]::Zero) { continue }
            try {
              $cn = New-Object System.Text.StringBuilder 256
              [WinNative]::GetClassNameW($h, $cn, 256) | Out-Null
              $cls = $cn.ToString()
              if ($script:s17logged -notcontains $cls) { $script:s17logged += $cls; Push-Evidence "S17c class=$cls" }
              if ($cls -notmatch '32770|Picker') { continue }
              # 句柄直取：经典 #32770 文件名编辑框直挂 Edit；Explorer 风格走 ComboBox 内 Edit
              $edit = [WinNative]::FindWindowExW($h, [IntPtr]::Zero, 'Edit', $null)
              if ($edit -eq [IntPtr]::Zero) {
                $cb = [WinNative]::FindWindowExW($h, [IntPtr]::Zero, 'ComboBox', $null)
                if ($cb -ne [IntPtr]::Zero) { $edit = [WinNative]::FindWindowExW($cb, [IntPtr]::Zero, 'Edit', $null) }
              }
              Push-Evidence "S17d edit-found=$($edit -ne [IntPtr]::Zero)"
              if ($edit -ne [IntPtr]::Zero) {
                $csvPath = Join-Path $PWD "ui-export-orders.csv"
                if (-not [WinNative]::SetWindowTextW($edit, $csvPath)) { Push-Evidence "S17d settext-fail" }
                else {
                  $ok = [WinNative]::GetDlgItem($h, 1)
                  Push-Evidence "S17d idok-found=$($ok -ne [IntPtr]::Zero)"
                  if ($ok -ne [IntPtr]::Zero) {
                    [WinNative]::SendMessageW($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                    Push-Evidence "S17d bmclick-sent"
                    $picked = $true
                    break
                  }
                }
              }
              # 键盘导航兜底（原路保留）：前置窗口后路径键入；仍失败则落 allowMiss
              try {
                Push-Evidence "S17d kbd-nav-attempt"
                Force-AppForeground 'S17d' | Out-Null
                Start-Sleep -Milliseconds 400
                [System.Windows.Forms.SendKeys]::SendWait("^a")
                Push-Evidence "S17d ctrl-a-sent"
                Start-Sleep -Milliseconds 150
                [System.Windows.Forms.SendKeys]::SendWait((Escape-SendKeys (Join-Path $PWD "ui-export-orders.csv")))
                Push-Evidence "S17d path-sent"
                Start-Sleep -Milliseconds 300
                [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
                Push-Evidence "S17d enter-sent"
                $picked = $true
                break
              } catch { Push-Evidence "S17d kbd-nav-err" }
            } catch { Push-Evidence "S17d iter-err" }
          }
        } catch { Push-Evidence ("S17c scan-err " + $_.Exception.GetType().Name + ": " + (($_.Exception.Message -replace '\s+','_')) + "@" + $_.InvocationInfo.ScriptLineNumber) }
        Push-Evidence "S17d-resolved picked=$picked"
        Assert-AppAlive "S17c-after-scan"
        if (-not $picked) { throw "save picker not handled" }
        $csvPath = Join-Path $PWD "ui-export-orders.csv"
        $fileOk = $false
        for ($i = 0; $i -lt 6; $i++) {
          if (Test-Path $csvPath) { $fileOk = $true; break }
          Start-Sleep -Seconds 1
        }
        Push-Evidence "S17e file=$fileOk"
        if (-not $fileOk) { Log "S17 export file missing after 6s: $csvPath" }
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
      }
      'soft-page' {
        if ($s.page -eq 'noop') {
          # ux-sweep: screenshot-only step (main window current state)
          Start-Sleep -Seconds 1
        } elseif ($s.page -eq 'theme') {
          # 2026-11-22 老板令: 切主题触发全屏重绘, 免费云机虚拟显示扛不住即被回收(#247/#249 三段同点定谳);
          # 摘除切换动作, 仅验主题入口可达+截图, 外观由逐屏对稿线兜底
          $btn = Find-ElWait @{ automationId = 'BtnTheme' } 6 $mainRoot
          if ($null -eq $btn) { throw "theme button not reachable" }
          Start-Sleep -Seconds 1
        } elseif ($s.page -eq 'about') {
          $btn = Find-ElWait @{ automationId = 'BtnAbout' } 6 $mainRoot
          if ($null -eq $btn) { throw "about button not reachable" }
          Invoke-El $btn
          Start-Sleep -Seconds 2
        }
        if ($s.shot) { Save-Shot (Tpl $s.shot) }
        Push-Evidence "$id pre-close"
        Close-ExtraWindows $handle
        Push-Evidence "SP closed"
      }
      'set-text' {
        # 评分补步通用置文：按 find 选择器（automationId/name）定位对话框输入框，SetValue＋回读校验（editor-set 同口径，3 轮重试）
        $target = $null
        foreach ($attempt in 1..3) {
          if ($s.find.automationId) { $target = Find-ElWait @{ automationId = (Tpl $s.find.automationId) } 6 $mainRoot }
          elseif ($s.find.name) { $target = Find-ElWait @{ controlType = 'Edit'; name = (Tpl $s.find.name) } 6 $mainRoot }
          if ($null -ne $target) { break }
          Start-Sleep -Seconds 1
        }
        if ($null -eq $target) { throw "set-text target not found: $($s.find.automationId)$($s.find.name)" }
        $want = Tpl ([string]$s.value)
        $done = $false
        foreach ($attempt in 1..3) {
          if (Set-TextValue $target $want) {
            try {
              $vp = $target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
              if ($vp.Current.Value -eq $want) { $done = $true; break }
            } catch { }
          }
          Start-Sleep -Milliseconds 500
        }
        if (-not $done) { throw "set-text readback mismatch: $($s.find.automationId)" }
        Push-Evidence "$id set-text ok"
      }
      'editor-set' {
        # 编辑器整段置文＋回读校验（#312：S63 轮执行账落 ELECT *＝首字符被吃，SetValue 竞态/回退路径静默失败）
        $ed = Find-ElWait @{ automationId = 'work-editor' } 15 $mainRoot
        if ($null -eq $ed) { throw "work-editor not found" }
        # #313：脏编辑器上 SetValue 稳定产生回读不一致（#312 定谳的 SetValue 竞态在非空内容下复现，a/b 段跨库同型）——置文前有内容先开新查询页签（#245/query 同口径）
        $cur = Read-EditorTextSafe $ed $mainRoot
        if ($s.sameTab) {
          # 发现72：sameTab 步（S63e 横幅收起链路）必须留在原页签——横幅在原页签上，新开页签成功执行后「[错误横幅] 收起」永不落账；清空后同空编辑器口径 SetValue（S63a 已验证可靠）
          Push-Evidence "$id same-tab clear"
          try { $ed.SetFocus() } catch { }
          Start-Sleep -Milliseconds 300
          [System.Windows.Forms.SendKeys]::SendWait("^a")
          Start-Sleep -Milliseconds 150
          [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
          Start-Sleep -Milliseconds 300
        } elseif ($null -ne $cur -and $cur.Trim().Length -gt 0) {
          Push-Evidence "$id editor-dirty, fresh tab"
          $fresh = Open-FreshQueryTab $mainRoot
          if ($null -ne $fresh) { $ed = $fresh }
        }
        $want = (Tpl $s.sql)
        $done = $false
        foreach ($attempt in 1..3) {
          try { $ed.SetFocus() } catch { }
          Start-Sleep -Milliseconds 400
          # 发现79：Set-TextValue 失败（高负载下 SetFocus/SendKeys 抛异）不再即抛终止，落到回读校验+剪贴板重试尾部，3 次后交给收场裁决
          if (-not (Set-TextValue $ed $want)) { Log "$id editor-set attempt $attempt Set-TextValue failed, fall into retry" }
          Start-Sleep -Milliseconds 600
          $got = Read-EditorTextSafe $ed $mainRoot
          if ($null -ne $got -and $got.Trim() -eq $want.Trim()) { $done = $true; break }
          Log "$id editor-set retry $attempt (readback mismatch got=$got)"
          # 读不回＝脚本不可验证，交账本裁决；读到错＝编辑器脏，开新页签剪贴板整段粘贴再试（query 同口径）；sameTab 步不开新页签，原位重试
          $fresh = $null
          if (-not $s.sameTab) { $fresh = Open-FreshQueryTab $mainRoot }
          if ($null -ne $fresh) { $ed = $fresh }
          # 云345 追修（mssql c2b 实锤：重试尾 ^a 未选中时 DEL 逐轮吃掉首字符，叠成 ELECT...）——先 ValuePattern 清空并回读确空，再整段粘贴
          try { ($ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('') } catch { }
          Start-Sleep -Milliseconds 400
          $curClr = Read-EditorTextSafe $ed $mainRoot
          if ($null -ne $curClr -and $curClr.Trim().Length -gt 0) {
            try {
              $ed.SetFocus()
              Start-Sleep -Milliseconds 300
              [System.Windows.Forms.SendKeys]::SendWait("^a")
              Start-Sleep -Milliseconds 150
              [System.Windows.Forms.SendKeys]::SendWait("{DEL}")
              Start-Sleep -Milliseconds 300
            } catch { }
          }
          try {
            Set-Clipboard -Value $want
            $ed.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait("^v")
          } catch { }
          Start-Sleep -Milliseconds 600
        }
        if (-not $done) {
          $got = Read-EditorTextSafe $ed $mainRoot
          # 读不回＝脚本不可验证，交账本裁决不硬红；读到错内容才是硬红
          if ($null -ne $got -and $got.Trim() -ne $want.Trim()) { throw "editor set verify failed after retries (readback mismatch got=$got)" }
          if ($null -eq $got) { Log "$id editor-set final unreadable, deferring to ledger check" }
        }
        Start-Sleep -Milliseconds 400
        Push-Evidence "$id editor-set done"
      }
      'press-keys' {
        # 编辑器按键＋焦点护栏（#312：F6 发出后 45s 才执行＝焦点不在编辑器，按键落空）；不在编辑器则真鼠标点入再发
        $el = Find-ElWait @{ automationId = 'work-editor' } 8 $mainRoot
        if ($null -eq $el) {
          # #321 定谳：查询页签丢失（欢迎页实锤）——先补开新页签恢复，再继续按键
          Log "$id work-editor missing, open fresh query tab to recover"
          Push-Evidence "$id editor-recover-fresh-tab"
          $fresh = Open-FreshQueryTab $mainRoot
          if ($null -ne $fresh) { $el = $fresh } else { throw "work-editor not found and fresh tab failed" }
        }
        # #320 定谳加固：长走查中云机前台漂移，SendKeys 落空（红步屏面正常而 applog 零打点）——发键前强制回前台
        Force-AppForeground $id | Out-Null; Start-Sleep -Milliseconds 250
        try { $el.SetFocus() } catch { }
        Start-Sleep -Milliseconds 300
        $focusedOk = $false
        try { $fe = [System.Windows.Automation.AutomationElement]::FocusedElement; if ($null -ne $fe -and $fe.AutomationId -eq 'work-editor') { $focusedOk = $true } } catch { }
        if (-not $focusedOk) {
          Log "$id keys focus-guard: focus not on editor, mouse-click into editor"
          try { Click-ElMouse $el 'left' } catch { try { $el.SetFocus() } catch { } }
          Start-Sleep -Milliseconds 400
          # #313：点击后复核焦点，仍不在编辑器则 SetFocus 兜底并留证（a 段 S57 护栏点击后 F6 落空无执行账实锤）
          try {
            $fe2 = [System.Windows.Automation.AutomationElement]::FocusedElement
            if ($null -eq $fe2 -or $fe2.AutomationId -ne 'work-editor') {
              $el.SetFocus()
              Push-Evidence "$id focus-guard fallback SetFocus"
            }
          } catch { Push-Evidence "$id focus-guard fallback failed" }
        }
        [System.Windows.Forms.SendKeys]::SendWait((Tpl $s.keys))
        Start-Sleep -Seconds 1
        # #320 到达复核：计划声明 arrivalLog 时轮询 applog，未到达则回前台＋回焦点补发一发（只补一次，杜绝静默落空）
        if ($s.arrivalLog) {
          $needle = Tpl ([string]$s.arrivalLog)
          $arrived = $false
          for ($ai = 0; $ai -lt 6; $ai++) {
            if (((Get-AppLogTail 60) -join "`n").Contains($needle)) { $arrived = $true; break }
            Start-Sleep -Seconds 1
          }
          if (-not $arrived) {
            Log "$id keys arrival missing, retry once with foreground+focus"
            Push-Evidence "$id keys-arrival-retry"
            Force-AppForeground $id | Out-Null
            Start-Sleep -Milliseconds 300
            try { $el.SetFocus() } catch { }
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait((Tpl $s.keys))
            Start-Sleep -Seconds 1
          }
          # #321 仍零到达：改走确定性通道——UIA 直点执行按钮（计划声明 fallbackInvokeId），键入降级如实留痕
          if ((-not $arrived) -and $s.fallbackInvokeId) {
            $fb = Find-ElWait @{ automationId = (Tpl ([string]$s.fallbackInvokeId)) } 6 $mainRoot
            if ($null -ne $fb) {
              Log "$id keys still not arrived, fallback invoke $($s.fallbackInvokeId)"
              Push-Evidence "$id keys-fallback-invoke"
              Invoke-El $fb
              Start-Sleep -Seconds 1
            }
          }
        }
        Push-Evidence "$id keys-sent $($s.keys)"
      }
      'set-window-size' {
        # 主窗分辨率三档走查：Win32 MoveWindow 直调（计划内非关键步，失败软 miss）
        $w = [int]$s.width; $h = [int]$s.height
        $hw = [IntPtr]::Zero
        try { $script:proc.Refresh(); $hw = $script:proc.MainWindowHandle } catch { }
        if ($hw -eq [IntPtr]::Zero) { $hw = $handle }
        if ($hw -eq [IntPtr]::Zero) { throw "main window handle not found" }
        if (-not [WinNative]::MoveWindow($hw, 0, 0, $w, $h, $true)) { throw "MoveWindow failed ${w}x$h" }
        Start-Sleep -Seconds 2
        Push-Evidence "$id winsize ${w}x$h"
      }
      'applog-contains' {
        # 通用实效断言：轮询应用日志尾部等锚点行（零 UIA 读结果，与 query 对账制同路数）
        $needles = @($s.value | ForEach-Object { Tpl ([string]$_) })
        $nots = @()
        if ($s.notContains) { $nots = @($s.notContains | ForEach-Object { Tpl ([string]$_) }) }
        $timeoutSec = 15
        if ($s.timeout) { $timeoutSec = [int]$s.timeout }
        $deadline = (Get-Date).AddSeconds($timeoutSec)
        $allFound = $false
        do {
          $tail = (Get-AppLogTail 200) -join "`n"
          $allFound = $true
          foreach ($v in $needles) { if (-not $tail.Contains($v)) { $allFound = $false; break } }
          if ($allFound) {
            foreach ($v in $nots) { if ($tail.Contains($v)) { throw "applog negative anchor hit: $v" } }
            break
          }
          Start-Sleep -Seconds 1
        } while ((Get-Date) -lt $deadline)
        if (-not $allFound) {
          $got = ($tail -replace "\r?\n", ' | ')
          if ($got.Length -gt 300) { $got = $got.Substring($got.Length - 300) }
          throw "applog anchor missing: $(($needles -join ' && ')) got-tail=[$got]"
        }
        Push-Evidence "$id applog-hit $(($needles -join ' && '))"
      }
      'window-contains' {
        # 主窗可见文本断言（弹层标题/步骤条头），支持 notContains 反断言
        $needle = Tpl $s.value
        $nots = @()
        if ($s.notContains) { $nots = @($s.notContains | ForEach-Object { Tpl ([string]$_) }) }
        $txt = ''
        for ($i = 0; $i -lt 10; $i++) {
          $txt = Get-ScopeText $mainRoot
          if ($txt.Contains($needle)) { break }
          Start-Sleep -Seconds 1
        }
        if (-not $txt.Contains($needle)) {
          $got = ($txt -replace "\s+", ' ')
          if ($got.Length -gt 200) { $got = $got.Substring($got.Length - 200) }
          throw "window text missing: $needle got-tail=[$got]"
        }
        foreach ($v in $nots) { if ($txt.Contains($v)) { throw "window negative anchor hit: $v" } }
        Push-Evidence "$id window-hit $needle"
      }
      'menu-item' {
        # 已开弹层中点菜单项（右键菜单/下拉菜单通用），UIA Invoke 可达
        $item = Find-ElWait @{ name = (Tpl $s.itemName) } 6 $mainRoot
        if ($null -eq $item) { $item = Find-ElWait @{ name = (Tpl $s.itemName) } 4 $null }
        if ($null -eq $item) { throw "menu item not found: $($s.itemName)" }
        Invoke-El $item
        Start-Sleep -Seconds 2
        if ($s.shot) { Save-Shot (Tpl $s.shot) }   # ㉗ 菜单项点击留证（与 click 动作同口径）
      }
      'context-tour' {
        # 全页面逐页留证（2026-11-24 老板令）：右键树节点 → 展开二级分组 → 逐项打开弹窗截图 → ESC/关闭按钮收场。
        # 单项失败只记账不中断（红绿全留痕），每项独立 try/catch；截图名 tour-{tourPrefix}-{序号}.png
        $treeItem = $null
        $needleItem = Tpl ([string]$s.prefix)
        $allTree = $mainRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($e in $allTree) {
          try { if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::TreeItem -and $e.Current.Name -like "*$needleItem*") { $treeItem = $e; break } } catch { }
        }
        if ($null -eq $treeItem) { throw "tour tree item not found: $needleItem" }
        $group = [string]$s.group
        $idx = 0
        $results = @()
        foreach ($name in @($s.items | ForEach-Object { Tpl ([string]$_) })) {
          $idx++
          $shotName = "{0}-{1:d2}.png" -f $s.tourPrefix, $idx
          try {
            $sub = Open-TreeContextFlyout $treeItem $group "$($s.tourPrefix)-$idx"
            if ($null -ne $sub) {
              $expanded = $false
              try {
                $ec = $sub.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                $ec.Expand(); $expanded = $true
              } catch { }
              if (-not $expanded) { Invoke-El $sub | Out-Null }
              Start-Sleep -Milliseconds 900
            }
            # 分组缺失兜底：菜单项可能平铺在一级（无「库工具」分组），直接找项
            $mi = Find-ElWait @{ name = $name } 6 $mainRoot
            if ($null -eq $mi) { $mi = Find-ElWait @{ name = $name } 4 $null }
            if ($null -eq $mi) { throw "tour item not found: $name (group=$group opened=$($null -ne $sub))" }
            Invoke-El $mi
            Start-Sleep -Seconds 3
            Save-Shot $shotName
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
            Start-Sleep -Milliseconds 700
            # ESC 不达兜底：点常见关闭/取消按钮（45 个弹窗无键盘收场口径的现场对策）
            $closeBtn = Find-ElWait @{ controlType = 'Button'; name = '关闭' } 2 $mainRoot
            if ($null -eq $closeBtn) { $closeBtn = Find-ElWait @{ controlType = 'Button'; name = '取消' } 2 $mainRoot }
            if ($null -ne $closeBtn) { try { Invoke-El $closeBtn } catch { } }
            Start-Sleep -Milliseconds 500
            Close-ExtraWindows $handle
            Assert-AppAlive "tour $name"
            $results += "OK $name"
            Push-Evidence "tour-ok $name $shotName"
          } catch {
            try { Save-Shot ("{0}-{1:d2}-fail.png" -f $s.tourPrefix, $idx) } catch { }
            try { [System.Windows.Forms.SendKeys]::SendWait("{ESC}") } catch { }
            Start-Sleep -Milliseconds 500
            Close-ExtraWindows $handle
            $results += "RED $name : $($_.Exception.Message)"
            Push-Evidence "tour-red $name $($_.Exception.Message)"
          }
        }
        Log ("tour done: " + ($results -join ' ;; '))
        Push-Evidence ("tour-summary " + ($results -join ' ;; '))
      }
      'tree-context' {
        # 树节点右键 → 弹出菜单点项（连接右键菜单：任务中心/智能工具/看板管理/部署向导…）
        # 2026-11-24 二轮定谳：首轮 35 个 tree-context 全 miss——二级子菜单项未展开不在 UIA 树；
        # 且 U078 一级项也 miss＝右键弹层本身可能没开。本版补：弹层探测＋选中后重试＋Shift+F10 兜底＋现场窗口取证。
        $item = Find-ElWait @{ controlType = 'TreeItem'; name = (Tpl $s.prefix) } 10 $mainRoot
        if ($null -eq $item) { throw "tree item not found: $(Tpl $s.prefix)" }
        $needle = Tpl ([string]$s.itemName)
        # 复拍轮3 修红（run 46019515974 定谳）：二级分组项（库工具/工具与向导 SubItem）不展开不在 UIA 树，
        # tree-context 直找 needle 全 miss（U079a/U088a/e/k 假阳性留证的根因）。补：可选 group 字段，
        # 先展开分组再找项；分组路径失败再退回原直找路径。
        $mi = $null
        if ($s.PSObject.Properties['group'] -and [string]$s.group) {
          $grp = Open-TreeContextFlyout $item (Tpl ([string]$s.group)) "$($s.prefix)-group"
          if ($null -ne $grp) {
            $expanded = $false
            try {
              $ec = $grp.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
              $ec.Expand(); $expanded = $true
            } catch { }
            if (-not $expanded) { Invoke-El $grp | Out-Null }
            Start-Sleep -Milliseconds 900
            $mi = Find-ElWait @{ name = $needle } 6 $mainRoot
            if ($null -eq $mi) { $mi = Find-ElWait @{ name = $needle } 4 $null }
          }
        }
        if ($null -eq $mi) { $mi = Open-TreeContextFlyout $item $needle $s.prefix }
        if ($null -eq $mi) { throw "context item not found: $($s.itemName)" }
        Invoke-El $mi
        Start-Sleep -Seconds 3
      }
      'press-esc' {
        # 弹层收场：ESC 关 ContentDialog/MenuFlyout，恢复主窗可操作
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Seconds 1
        Close-ExtraWindows $handle
        # 云345 追修（mysql c2b 实锤：另存 SQL 文件对话框 Esc 后仍开着，模态挡住后续确认框）——
        # 扫应用进程的 Win32 对话框窗（#32770/Picker），见即 WM_CLOSE
        # 云346 追修（346 十段同因，红场截图实锤另存框仍开）：另存对话框冷机首启慢，固定 3 轮扫完即退＝
        # 对话框还没出现就收工，弹出后无人再关。改为持续清扫窗：15 秒内每 0.8 秒扫一次，
        # 见框即关；连续 5 扫无框且已关过至少一个才收工，超时仍开则留红账注记
        $appPid = $script:proc.Id
        $scanDeadline = (Get-Date).AddSeconds(15)
        $closedCount = 0
        $quietScans = 0
        while ((Get-Date) -lt $scanDeadline) {
          $dlgHwnd = [IntPtr]::Zero
          try {
            $hScan = [IntPtr]::Zero
            do {
              $hScan = [WinNative]::FindWindowExW([IntPtr]::Zero, $hScan, $null, $null)
              if ($hScan -ne [IntPtr]::Zero -and [WinNative]::IsWindowVisible($hScan)) {
                $cls2 = New-Object System.Text.StringBuilder 256
                [void][WinNative]::GetClassNameW($hScan, $cls2, 256)
                $wpid2 = [uint32]0
                [void][WinNative]::GetWindowThreadProcessId($hScan, [ref]$wpid2)
                $cls2s = $cls2.ToString()
                if (($cls2s -eq '#32770' -or $cls2s -match 'Picker') -and $wpid2 -eq $appPid) { $dlgHwnd = $hScan; break }
              }
            } while ($hScan -ne [IntPtr]::Zero)
          } catch { }
          if ($dlgHwnd -eq [IntPtr]::Zero) {
            $quietScans++
            if ($closedCount -gt 0 -and $quietScans -ge 8) { break }
            if ($closedCount -eq 0 -and $quietScans -ge 8) { break }
            Start-Sleep -Milliseconds 800
            continue
          }
          $closedCount++
          $quietScans = 0
          Log "$id press-esc: app dialog still open (close $closedCount), send WM_CLOSE"
          Push-Evidence "$id esc-dialog-still-open"
          try { $res2 = [IntPtr]::Zero; [void][WinNative]::SendMessageTimeoutW($dlgHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 1000, [ref]$res2) } catch { }
          Start-Sleep -Milliseconds 900
        }
        # 云346 追修②：Win32 类名扫不中时的 UIA 兜底——桌面根找应用进程对话框窗，点其 Cancel/取消（真实用户动作）
        try {
          $desk = [System.Windows.Automation.AutomationElement]::RootElement
          $wins3 = $desk.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
          foreach ($w3 in $wins3) {
            try {
              $h3 = [IntPtr]$w3.Current.NativeWindowHandle
              if ($h3 -eq [IntPtr]::Zero) { continue }
              $wpid3 = [uint32]0
              [void][WinNative]::GetWindowThreadProcessId($h3, [ref]$wpid3)
              if ($wpid3 -ne $appPid) { continue }
              $cls3 = New-Object System.Text.StringBuilder 256
              [void][WinNative]::GetClassNameW($h3, $cls3, 256)
              if ($cls3.ToString() -notmatch '32770|Picker') { continue }
              $cancel3 = $null
              foreach ($nm in @('Cancel','取消')) {
                $cancel3 = $w3.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $nm)))
                if ($null -ne $cancel3) { break }
              }
              if ($null -ne $cancel3) {
                ($cancel3.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                $closedCount++
                Log "$id press-esc: UIA fallback clicked Cancel on app dialog"
                Push-Evidence "$id esc-dialog-uia-cancel"
                Start-Sleep -Milliseconds 900
              }
            } catch { }
          }
        } catch { }
        if ($closedCount -gt 0) { Log "$id press-esc: closed $closedCount app dialog(s) in persistence window" }
      }
      default { throw "unknown action: $action" }
    }
    if ($ShotAll) { Start-Sleep -Seconds 1; Save-Shot "sweep-$id.png" }
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
    # 红账当场落证据分支：不等下一步 step-start 捎带（机器死在步间会丢红）
    if ($outcome -eq 'fail') {
      # 失败屏面截图随红账上分支：对照屏面与 UIA 读账，定谳"应用没显示"还是"显示没读到"
      try { Save-Shot "ev-shot-fail-$id.png" } catch { }
      Push-Evidence "red $id"
    }
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
    # 死亡现场落公开分支：crash.log/事件日志已入 walklog，随最后一次推送固化（否则登录侧与 redaction 后什么都拿不到）
    Push-Evidence "app-exited $id"
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
