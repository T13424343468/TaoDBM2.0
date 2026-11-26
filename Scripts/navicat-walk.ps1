# navicat-walk.ps1 — 竞品对照走查 v14：真库对照·弹窗关断多招（v13 实锤：试用按钮 5 击+回车全无效，坐标经截图核实无误）
# v14 根因假设：Focus-Navicat 每轮把主窗口拉前台→弹窗失焦→点击沦为「激活点击」被吞（v10 未逐轮抢焦点故曾成功）。
# v14 策略：① EnumWindows 枚举 Navicat 全部可见窗口（hwnd/类名/标题/矩形）落日志，按几何（含点 512,300）锁定弹窗句柄；
# ② 对弹窗句柄直接 SetForegroundWindow 再操作；③ 关断招式按序：X(776,175)→试用(717,515)→PostMessage 消息级→ESC→TAB+ENTER→双击试用，
#    每招后截图+像素双验证+Responding 探针（防 UI 线程挂起白耗）；④ 关断后枚举窗口+ESC 处理可能的注册窗口；
# ⑤ 落 HKCU\Software\PremiumSoft 注册表清单（为预置跳过首启弹窗定位）；⑥ 向导/连接/树展开沿用 v12 定谳坐标。
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Log([string]$m) {
  $line = "[WALK] $m"
  Write-Host $line
  Add-Content -Path "walk-log.txt" -Value $line -Encoding utf8
}

function Shot([string]$name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $g.Dispose()
  $bmp.Save("$PWD\$name", [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Log "shot $name"
}

function Get-Pixel([int]$x, [int]$y) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $g.Dispose()
  $c = $bmp.GetPixel($x, $y)
  $bmp.Dispose()
  return $c
}

$sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public struct Rect14 { public int L; public int T; public int R; public int B; }
public class Win14 {
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect14 r);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
}
'@
Add-Type -TypeDefinition $sig
[Win14]::ShowWindow([Win14]::GetConsoleWindow(), 6) | Out-Null

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative14 {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
  public static void DblClick(int x, int y) { Click(x, y); System.Threading.Thread.Sleep(80); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Focus-Navicat {
  $p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    try { [Win14]::SetForegroundWindow($p.MainWindowHandle) | Out-Null } catch {}
    Start-Sleep -Milliseconds 600
  }
  return $p
}

function Get-NavWindows {
  param([uint32]$ProcId)
  $list = New-Object System.Collections.ArrayList
  $cb = [Win14+EnumProc]{
    param($h, $l)
    $wp = [uint32]0
    [Win14]::GetWindowThreadProcessId($h, [ref]$wp) | Out-Null
    if ($wp -eq $ProcId -and [Win14]::IsWindowVisible($h)) {
      $t = New-Object System.Text.StringBuilder 512
      [Win14]::GetWindowText($h, $t, 512) | Out-Null
      $c = New-Object System.Text.StringBuilder 512
      [Win14]::GetClassName($h, $c, 512) | Out-Null
      $r = New-Object Rect14
      [Win14]::GetWindowRect($h, [ref]$r) | Out-Null
      [void]$list.Add("hwnd=$h class='$($c.ToString())' title='$($t.ToString())' rect=($($r.L),$($r.T))-($($r.R),$($r.B))")
    }
    return $true
  }
  [Win14]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
  return $list
}

function PostClick {
  param([IntPtr]$h, [int]$sx, [int]$sy)
  if ($h -eq [IntPtr]::Zero) { Log "postclick skip: no hwnd"; return }
  $r = New-Object Rect14
  [Win14]::GetWindowRect($h, [ref]$r) | Out-Null
  $cx = $sx - $r.L
  $cy = $sy - $r.T
  $lp = [IntPtr]((($cy -shl 16) -bor ($cx -band 0xFFFF)))
  [Win14]::PostMessage($h, 0x0201, [IntPtr]1, $lp) | Out-Null
  Start-Sleep -Milliseconds 120
  [Win14]::PostMessage($h, 0x0202, [IntPtr]::Zero, $lp) | Out-Null
  Log "postclick client=($cx,$cy) hwnd=$h"
}

Log "v14 start exe=$ExePath"

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
$navProc = Focus-Navicat

# 1) 枚举全部可见窗口落日志，按几何锁定弹窗句柄（含点 512,300 且非主窗口）
Start-Sleep -Seconds 5
$navPid = [uint32]$navProc.Id
$wins = Get-NavWindows -ProcId $navPid
foreach ($w in $wins) { Log "win: $w" }
$dlgHwnd = [IntPtr]::Zero
foreach ($w in $wins) {
  if ($w -match 'hwnd=(-?\d+) class=') {
    $h = [IntPtr][int64]$Matches[1]
    if ($h -ne $navProc.MainWindowHandle -and $w -match 'rect=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)') {
      $l = [int]$Matches[2]; $t = [int]$Matches[3]; $r = [int]$Matches[4]; $b = [int]$Matches[5]
      if ($l -le 512 -and $r -gt 512 -and $t -le 300 -and $b -gt 300) { $dlgHwnd = $h; Log "dialog candidate: $w" }
    }
  }
}
Log "dialog hwnd=$dlgHwnd mainHwnd=$($navProc.MainWindowHandle)"

function Focus-Dialog {
  if ($script:dlgHwnd -ne [IntPtr]::Zero) {
    try { [Win14]::SetForegroundWindow($script:dlgHwnd) | Out-Null } catch {}
    Start-Sleep -Milliseconds 600
  } else { Focus-Navicat | Out-Null }
}

function Test-TrialGone {
  try {
    $c = Get-Pixel 512 120
    if ($c.R -gt 180 -and $c.G -gt 120 -and $c.B -lt 90) { return $false }
    $c2 = Get-Pixel 512 300
    if ($c2.R -gt 235 -and $c2.G -gt 235 -and $c2.B -gt 235) { return $false }
    return $true
  } catch { return $false }
}

# 2) 关断招式按序尝试，每招后截图+双像素验证+Responding 探针
$mNames = @('x-close', 'trial-click', 'postmsg', 'esc', 'tab-enter', 'dblclick')
$dismissed = $false
for ($m = 0; $m -lt 6 -and -not $dismissed; $m++) {
  Shot ("shot-n10-$($mNames[$m])-before.png")
  switch ($m) {
    0 { Focus-Dialog; [MouseNative14]::Click(776, 175); Log "method $($m+1) X(776,175)" }
    1 { Focus-Dialog; [MouseNative14]::Click(717, 515); Log "method $($m+1) trial(717,515) with dialog focused" }
    2 { Focus-Dialog; PostClick -h $dlgHwnd -sx 717 -sy 515; Log "method $($m+1) PostMessage click" }
    3 { Focus-Dialog; [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Log "method $($m+1) ESC" }
    4 { Focus-Dialog; [System.Windows.Forms.SendKeys]::SendWait("{TAB}"); Start-Sleep -Milliseconds 500; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Log "method $($m+1) TAB+ENTER" }
    5 { Focus-Dialog; [MouseNative14]::DblClick(717, 515); Log "method $($m+1) dblclick trial" }
  }
  Start-Sleep -Seconds 7
  try { $navProc.Refresh(); Log "responding=$($navProc.Responding)" } catch { Log "process probe failed" }
  Shot ("shot-n11-$($mNames[$m])-after.png")
  if (Test-TrialGone) { $dismissed = $true; Log "trial dismissed by method $($m+1) [$($mNames[$m])]" }
  else {
    if ($dlgHwnd -ne [IntPtr]::Zero -and -not [Win14]::IsWindowVisible($dlgHwnd)) { $dismissed = $true; Log "trial dismissed by method $($m+1) (hwnd gone)" }
  }
}
if (-not $dismissed) { Log "WARN all 6 methods failed; trial dialog possibly still up" }
Start-Sleep -Seconds 10

# 3) 关断后枚举窗口（捕获可能的注册窗口）+ ESC 兜底
$wins2 = Get-NavWindows -ProcId $navPid
foreach ($w in $wins2) { Log "win-after: $w" }
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 3
Shot "shot-n20-after-trial.png"

# 4) 向导 4 连击 + 关新功能弹窗（v10 定谳坐标）
for ($i = 1; $i -le 4; $i++) { [MouseNative14]::Click(869, 669); Start-Sleep -Seconds 6 }
$p = Focus-Navicat
Log "after wizard window='$($p.MainWindowTitle)'"
[MouseNative14]::Click(696, 187)
Start-Sleep -Seconds 3
Shot "shot-n02-main.png"

# 5) 连接下拉 → 扫描菜单行 → 点第 6 行（SQLite 猜测位）
[MouseNative14]::Click(48, 75)
Start-Sleep -Seconds 3
Shot "shot-n03-menu.png"
$rows = @()
$bmp = New-Object System.Drawing.Bitmap(1024, 768)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$g.Dispose()
$inBand = $false; $bandStart = 0
for ($y = 90; $y -lt 520; $y++) {
  $dark = 0
  for ($x = 25; $x -lt 220; $x += 2) {
    $c = $bmp.GetPixel($x, $y)
    if ($c.R -lt 120 -and $c.G -lt 120 -and $c.B -lt 120) { $dark++ }
  }
  if ($dark -ge 3 -and -not $inBand) { $inBand = $true; $bandStart = $y }
  elseif ($dark -lt 3 -and $inBand) { $inBand = $false; if ($y - $bandStart -ge 8) { $rows += [int](($bandStart + $y) / 2) } }
}
$bmp.Dispose()
Log "menu rows detected: $($rows -join ',')"
if ($rows.Count -ge 6) {
  [MouseNative14]::Click(100, $rows[5])
  Log "clicked menu row 6 at y=$($rows[5])"
} elseif ($rows.Count -gt 0) {
  [MouseNative14]::Click(100, $rows[$rows.Count - 1])
  Log "clicked last menu row at y=$($rows[$rows.Count - 1])"
} else {
  Log "WARN no menu rows detected; fallback click y=249"
  [MouseNative14]::Click(100, 249)
}
Start-Sleep -Seconds 5
Shot "shot-n04-conn-dialog.png"

# 6) SQLite 连接表单：连接名 → 数据库文件路径 → 保存
[MouseNative14]::Click(450, 180)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("sample-sqlite")
Start-Sleep -Seconds 1
[MouseNative14]::Click(450, 265)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("C:\taodb-sample\sample.db")
Start-Sleep -Seconds 1
Shot "shot-n05-filled.png"
[MouseNative14]::Click(640, 660)
Start-Sleep -Seconds 5
Shot "shot-n06-saved.png"

# 7) 树：双击连接节点 → main → users
[MouseNative14]::DblClick(100, 140)
Start-Sleep -Seconds 6
Shot "shot-n07-tree.png"
[MouseNative14]::DblClick(120, 170)
Start-Sleep -Seconds 4
Shot "shot-n08-db.png"
[MouseNative14]::DblClick(250, 220)
Start-Sleep -Seconds 8
Shot "shot-n09-data.png"

# 8) 落 PremiumSoft 设置目录清单 + 注册表清单（为预置跳过首启弹窗定位）
try {
  $dirs = Get-ChildItem "$env:APPDATA\PremiumSoft" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 80
  foreach ($d in $dirs) { Log "settings: $($d.FullName.Replace($env:APPDATA,'~APPDATA'))" }
} catch { Log "settings dump failed: $_" }
try {
  $regRoot = "HKCU:\Software\PremiumSoft"
  if (Test-Path $regRoot) {
    $keys = Get-ChildItem $regRoot -Recurse -ErrorAction SilentlyContinue | Select-Object -First 40
    foreach ($k in $keys) {
      Log "regkey: $($k.Name)"
      foreach ($v in ($k.GetValueNames() | Select-Object -First 12)) { Log "  regval: $v = $($k.GetValue($v))" }
    }
  } else { Log "regkey: HKCU PremiumSoft not found" }
} catch { Log "reg dump failed: $_" }

Log "v14 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
