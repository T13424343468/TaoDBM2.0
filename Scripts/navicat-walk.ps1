# navicat-walk.ps1 — 竞品对照走查 v11：真库对照（2026-11-26 老板令：数据库工具不连真库，对照了个什么）
# v10 实锤：走查止步 Ctrl+N 连接表单，从未连过任何库。v11 目标：真 SQLite 文件库 → Navicat 建连接 →
# 展开对象树 → 打开表数据编辑器，全程录屏+分步截图+walk-log 取证。
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Log([string]$m) {
  $line = "[WALK] $m"
  Write-Host $line
  Add-Content -Path "walk-log.txt" -Value $line -Encoding utf8
}

function Shot([string]$name) {
  Add-Type -AssemblyName System.Drawing
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $g.Dispose()
  $bmp.Save("$PWD\$name", [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Log "shot $name"
}

$sig = @'
using System;
using System.Runtime.InteropServices;
public class Win11 {
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@
Add-Type -TypeDefinition $sig
[Win11]::ShowWindow([Win11]::GetConsoleWindow(), 6) | Out-Null

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative11 {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
  public static void DblClick(int x, int y) { Click(x, y); System.Threading.Thread.Sleep(80); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

$AE = [System.Windows.Automation.AutomationElement]
function Find-ByName($root, $pattern, [int]$maxMs = 4000) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $maxMs) {
    $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $els) {
      try { if ($e.Current.Name -like "*$pattern*") { return $e } } catch {}
    }
    Start-Sleep -Milliseconds 400
  }
  return $null
}
function Dump-Names($root, $tag) {
  try {
    $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $names = @()
    foreach ($e in $els) { $n = $e.Current.Name; if ($n) { $names += "$($e.Current.ControlType.ProgrammaticName):$n" } }
    Log "uia[$tag] count=$($names.Count) names=$((($names | Select-Object -First 60) -join ' | '))"
  } catch { Log "uia[$tag] dump failed: $_" }
}
function Focus-Navicat {
  $p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    [Win11]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Log "v11 start exe=$ExePath"

# 0) 真库：Python 内置 sqlite3 生成带脏数据的样例库
New-Item -ItemType Directory -Force C:\taodb-sample | Out-Null
$py = @'
import sqlite3, random
random.seed(42)
c = sqlite3.connect(r'C:\taodb-sample\sample.db')
cur = c.cursor()
cur.execute('CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT, email TEXT, age INTEGER, balance REAL)')
cur.execute('CREATE TABLE orders(id INTEGER PRIMARY KEY, user_id INTEGER, amount REAL, status TEXT)')
for i in range(1, 201):
    email = '' if i % 7 == 0 else 'user%d@taodb.dev' % i
    cur.execute('INSERT INTO users VALUES(?,?,?,?,?)', (i, 'user%03d' % i, email, random.randint(18, 70), round(random.uniform(0, 9999), 2)))
for i in range(1, 501):
    cur.execute('INSERT INTO orders VALUES(?,?,?,?)', (i, random.randint(1, 200), round(random.uniform(1, 500), 2), random.choice(['paid', 'pending', 'cancelled'])))
c.commit()
c.close()
'@
$tmpPy = "$PWD\gen-sample.py"
[System.IO.File]::WriteAllText($tmpPy, $py, (New-Object System.Text.UTF8Encoding($false)))
python $tmpPy
Log "db gen exit=$LASTEXITCODE size=$((Get-Item C:\taodb-sample\sample.db -ErrorAction SilentlyContinue).Length)"

Start-Sleep -Seconds 5
Start-Process -FilePath $ExePath
$seen = $false
for ($i = 0; $i -lt 12; $i++) {
  Start-Sleep -Seconds 5
  if (Get-Process -Name "navicat" -ErrorAction SilentlyContinue) { $seen = $true; break }
}
if (-not $seen) { Log "ERROR navicat process never appeared"; exit 1 }
Focus-Navicat | Out-Null

# 1) 试用 → 向导 → 关新功能弹窗（v10 定谳坐标）
[MouseNative11]::Click(717, 515)
Start-Sleep -Seconds 20
for ($i = 1; $i -le 4; $i++) { [MouseNative11]::Click(869, 669); Start-Sleep -Seconds 6 }
$p = Focus-Navicat
Log "after wizard window='$($p.MainWindowTitle)'"
[MouseNative11]::Click(696, 187)
Start-Sleep -Seconds 3
Shot "shot-n01-main.png"

# 2) 打开连接类型下拉（工具栏「连接」右侧箭头），UIA 点 SQLite
[MouseNative11]::Click(48, 75)
Start-Sleep -Seconds 3
Shot "shot-n02-menu.png"
$fg = $AE::FocusedElement
if (-not $fg) { $fg = $p }
Dump-Names $fg "menu"
$sqlite = Find-ByName $fg "SQLite" 6000
if ($sqlite) {
  Log "uia found SQLite element: $($sqlite.Current.ControlType.ProgrammaticName)"
  try {
    $cp = $sqlite.GetClickablePoint()
    [MouseNative11]::Click([int]$cp.X, [int]$cp.Y)
  } catch {
    try { $sqlite.SetFocus() } catch {}
    try { $ip = $sqlite.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $ip.Invoke() } catch { [MouseNative11]::Click(48, 75) }
  }
  Log "clicked SQLite menu item"
} else {
  Log "WARN SQLite menu item not found by UIA; fallback keyboard"
  [System.Windows.Forms.SendKeys]::SendWait("{DOWN}{DOWN}{DOWN}{DOWN}{DOWN}{DOWN}{ENTER}")
}
Start-Sleep -Seconds 5
Shot "shot-n03-conn-dialog.png"

# 3) 连接表单：连接名 + 数据库文件（浏览→路径→打开）
$dlg = $AE::FocusedElement
Dump-Names $dlg "conn-dialog"
$nameEl = Find-ByName $dlg "连接名" 3000
if ($nameEl) {
  try {
    $vp = $nameEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue("sample-sqlite"); Log "set conn name"
  } catch {
    $cp = $nameEl.GetClickablePoint(); [MouseNative11]::Click([int]$cp.X, [int]$cp.Y); [System.Windows.Forms.SendKeys]::SendWait("sample-sqlite")
  }
} else { Log "WARN conn-name element not found" }
$browse = Find-ByName $dlg "浏览" 3000
if (-not $browse) { $browse = Find-ByName $dlg "..." 2000 }
if ($browse) {
  try { $cp = $browse.GetClickablePoint(); [MouseNative11]::Click([int]$cp.X, [int]$cp.Y) } catch { try { $browse.SetFocus() } catch {} }
  Start-Sleep -Seconds 3
  [System.Windows.Forms.SendKeys]::SendWait("C:\taodb-sample\sample.db")
  Start-Sleep -Seconds 1
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
  Log "file dialog path sent"
} else { Log "WARN browse button not found" }
Start-Sleep -Seconds 2
Shot "shot-n04-filled.png"

# 4) 保存连接
$ok = Find-ByName $dlg "确定" 3000
if (-not $ok) { $ok = Find-ByName $dlg "保存" 3000 }
if (-not $ok) { $ok = Find-ByName $dlg "OK" 2000 }
if ($ok) {
  try { $cp = $ok.GetClickablePoint(); [MouseNative11]::Click([int]$cp.X, [int]$cp.Y) } catch { try { ($ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch {} }
  Log "clicked save/ok"
} else { Log "WARN ok button not found; fallback Ctrl+S"; [System.Windows.Forms.SendKeys]::SendWait("^s") }
Start-Sleep -Seconds 5
Shot "shot-n05-saved.png"

# 5) 展开连接 → main → 表 → 打开 users 数据编辑器
$main = Focus-Navicat
$rootEl = $AE::FromHandle($main.MainWindowHandle)
$connNode = Find-ByName $rootEl "sample-sqlite" 8000
if ($connNode) {
  try { $cp = $connNode.GetClickablePoint(); [MouseNative11]::DblClick([int]$cp.X, [int]$cp.Y) } catch { try { $connNode.SetFocus() } catch {}; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}") }
  Log "expanded connection node"
} else { Log "WARN connection node not found in tree" }
Start-Sleep -Seconds 6
Shot "shot-n06-tree.png"
$rootEl = $AE::FromHandle((Focus-Navicat).MainWindowHandle)
$mainNode = Find-ByName $rootEl "main" 5000
if ($mainNode) {
  try { $cp = $mainNode.GetClickablePoint(); [MouseNative11]::DblClick([int]$cp.X, [int]$cp.Y) } catch { try { $mainNode.SetFocus() } catch {}; [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}") }
  Log "expanded main db node"
}
Start-Sleep -Seconds 4
$rootEl = $AE::FromHandle((Focus-Navicat).MainWindowHandle)
$usersNode = Find-ByName $rootEl "users" 6000
if ($usersNode) {
  try { $cp = $usersNode.GetClickablePoint(); [MouseNative11]::DblClick([int]$cp.X, [int]$cp.Y) } catch { try { $usersNode.SetFocus() } catch {}; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}") }
  Log "opened users table data editor"
} else { Log "WARN users node not found" }
Start-Sleep -Seconds 8
Shot "shot-n07-data.png"
Start-Sleep -Seconds 4
Shot "shot-n08-data2.png"

Log "v11 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
