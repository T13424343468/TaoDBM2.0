# navicat-walk.ps1 — 竞品对照走查 v15：真库对照·类名状态机（v14 实锤：试用弹窗已被关掉——关断后枚举只见 TWhatsNew+TNavicatMainForm；
# 窗口类名全部到手：TRegistrationSubForm=试用弹窗、TWhatsNew=改善向导、TNavicatMainForm=主窗体、TSplashForm=启动屏）
# v15 策略：① 修 v14 矩形正则 bug（rect=(L,T)-(R,B) 误写为逗号分隔致 dlgHwnd=0）；② 全程以窗口类名做状态机：
#    等 TRegistrationSubForm→按序 ESC→TAB+ENTER→X(776,175)→试用(717,515) 关断并逐招类名验证；
#    等 TWhatsNew→不共享(746,667)→共享(887,667)→ESC 逐招关断；③ 关新功能弹窗(696,187)；
# ④ 连接菜单扫描后点第 7 行（SQLite 猜测位，v12 点第 6 行疑为 PostgreSQL）；⑤ 表单盲填+保存+树双击沿用；⑥ 每步枚举窗口落日志。
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

$sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public struct Rect15 { public int L; public int T; public int R; public int B; }
public class Win15 {
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect15 r);
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
}
'@
Add-Type -TypeDefinition $sig
[Win15]::ShowWindow([Win15]::GetConsoleWindow(), 6) | Out-Null

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative15 {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
  public static void DblClick(int x, int y) { Click(x, y); System.Threading.Thread.Sleep(80); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Get-NavWindows {
  param([uint32]$ProcId)
  $list = New-Object System.Collections.ArrayList
  $cb = [Win15+EnumProc]{
    param($h, $l)
    $wp = [uint32]0
    [Win15]::GetWindowThreadProcessId($h, [ref]$wp) | Out-Null
    if ($wp -eq $ProcId -and [Win15]::IsWindowVisible($h)) {
      $t = New-Object System.Text.StringBuilder 512
      [Win15]::GetWindowText($h, $t, 512) | Out-Null
      $c = New-Object System.Text.StringBuilder 512
      [Win15]::GetClassName($h, $c, 512) | Out-Null
      $r = New-Object Rect15
      [Win15]::GetWindowRect($h, [ref]$r) | Out-Null
      [void]$list.Add("hwnd=$h class='$($c.ToString())' title='$($t.ToString())' rect=($($r.L),$($r.T))-($($r.R),$($r.B))")
    }
    return $true
  }
  [Win15]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
  return $list
}

function Test-WindowClass {
  param([string]$ClassPattern)
  $wins = Get-NavWindows -ProcId $script:navPid
  foreach ($w in $wins) { if ($w -like "class='$ClassPattern'*") { return $true } }
  return $false
}

function Wait-WindowClass {
  param([string]$ClassPattern, [int]$TimeoutSec = 60)
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $wins = Get-NavWindows -ProcId $script:navPid
    foreach ($w in $wins) { if ($w -like "class='$ClassPattern'*") { return $w } }
    Start-Sleep -Seconds 2
  }
  return $null
}

function Focus-WindowClass {
  param([string]$ClassPattern)
  $wins = Get-NavWindows -ProcId $script:navPid
  foreach ($w in $wins) {
    if ($w -like "class='$ClassPattern'*" -and $w -match 'hwnd=(-?\d+)') {
      $h = [IntPtr][int64]$Matches[1]
      try { [Win15]::SetForegroundWindow($h) | Out-Null } catch {}
      Start-Sleep -Milliseconds 600
      return $true
    }
  }
  return $false
}

Log "v15 start exe=$ExePath"

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
$navProc = Get-Process -Name "navicat" | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$script:navPid = [uint32]$navProc.Id

# 1) 等试用弹窗（TRegistrationSubForm）出现并落窗口清单
$trial = Wait-WindowClass -ClassPattern 'TRegistrationSubForm*' -TimeoutSec 90
if ($trial) { Log "trial window: $trial" } else { Log "WARN trial window not seen; dump windows" }
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win: $w" }

# 2) 关断试用弹窗：ESC→TAB+ENTER→X(776,175)→试用(717,515)，每招后类名验证
$trialGone = -not (Test-WindowClass -ClassPattern 'TRegistrationSubForm*')
$mNames = @('esc', 'tab-enter', 'x-click', 'trial-click')
for ($m = 0; $m -lt 4 -and -not $trialGone; $m++) {
  Focus-WindowClass -ClassPattern 'TRegistrationSubForm*' | Out-Null
  switch ($m) {
    0 { [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Log "trial method $($m+1) ESC" }
    1 { [System.Windows.Forms.SendKeys]::SendWait("{TAB}"); Start-Sleep -Milliseconds 500; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Log "trial method $($m+1) TAB+ENTER" }
    2 { [MouseNative15]::Click(776, 175); Log "trial method $($m+1) X(776,175)" }
    3 { [MouseNative15]::Click(717, 515); Log "trial method $($m+1) trial(717,515)" }
  }
  Start-Sleep -Seconds 6
  $trialGone = -not (Test-WindowClass -ClassPattern 'TRegistrationSubForm*')
  Shot ("shot-n12-trial-" + $mNames[$m] + ".png")
  Log ("trial dismissed=" + $trialGone + " by " + $mNames[$m])
}
if (-not $trialGone) { Log "WARN trial dialog still up after 4 methods" }
Start-Sleep -Seconds 8

# 3) 关改善向导（TWhatsNew）：不共享(746,667)→共享(887,667)→ESC，每招后类名验证
$wizSeen = Wait-WindowClass -ClassPattern 'TWhatsNew*' -TimeoutSec 30
if ($wizSeen) {
  Log "wizard window: $wizSeen"
  $wizGone = $false
  $wNames = @('no-share', 'share', 'esc')
  for ($m = 0; $m -lt 3 -and -not $wizGone; $m++) {
    Focus-WindowClass -ClassPattern 'TWhatsNew*' | Out-Null
    switch ($m) {
      0 { [MouseNative15]::Click(746, 667); Log "wizard method $($m+1) no-share(746,667)" }
      1 { [MouseNative15]::Click(887, 667); Log "wizard method $($m+1) share(887,667)" }
      2 { [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Log "wizard method $($m+1) ESC" }
    }
    Start-Sleep -Seconds 6
    $wizGone = -not (Test-WindowClass -ClassPattern 'TWhatsNew*')
    Shot ("shot-n13-wizard-" + $wNames[$m] + ".png")
    Log ("wizard dismissed=" + $wizGone + " by " + $wNames[$m])
  }
  if (-not $wizGone) { Log "WARN wizard still up after 3 methods" }
} else { Log "no wizard window; continue" }
Start-Sleep -Seconds 5

# 4) 关新功能弹窗（v10 定谳坐标 696,187）+ 主界面截图
Focus-WindowClass -ClassPattern 'TNavicatMainForm*' | Out-Null
[MouseNative15]::Click(696, 187)
Start-Sleep -Seconds 3
Shot "shot-n02-main.png"
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win-main: $w" }

# 5) 连接下拉 → 扫描菜单行 → 点第 7 行（SQLite 猜测位）
[MouseNative15]::Click(48, 75)
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
if ($rows.Count -ge 7) {
  [MouseNative15]::Click(100, $rows[6])
  Log "clicked menu row 7 at y=$($rows[6])"
} elseif ($rows.Count -gt 0) {
  [MouseNative15]::Click(100, $rows[$rows.Count - 1])
  Log "clicked last menu row at y=$($rows[$rows.Count - 1])"
} else {
  Log "WARN no menu rows detected; fallback click y=270"
  [MouseNative15]::Click(100, 270)
}
Start-Sleep -Seconds 5
Shot "shot-n04-conn-dialog.png"
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win-conn: $w" }

# 6) SQLite 连接表单：连接名 → 数据库文件路径 → 保存
[MouseNative15]::Click(450, 180)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("sample-sqlite")
Start-Sleep -Seconds 1
[MouseNative15]::Click(450, 265)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("C:\taodb-sample\sample.db")
Start-Sleep -Seconds 1
Shot "shot-n05-filled.png"
[MouseNative15]::Click(640, 660)
Start-Sleep -Seconds 5
Shot "shot-n06-saved.png"

# 7) 树：双击连接节点 → main → users
[MouseNative15]::DblClick(100, 140)
Start-Sleep -Seconds 6
Shot "shot-n07-tree.png"
[MouseNative15]::DblClick(120, 170)
Start-Sleep -Seconds 4
Shot "shot-n08-db.png"
[MouseNative15]::DblClick(250, 220)
Start-Sleep -Seconds 8
Shot "shot-n09-data.png"

# 8) 落 PremiumSoft 设置目录清单（注册表清单 v14 已到手：Registration18XCS）
try {
  $dirs = Get-ChildItem "$env:APPDATA\PremiumSoft" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 60
  foreach ($d in $dirs) { Log "settings: $($d.FullName.Replace($env:APPDATA,'~APPDATA'))" }
} catch { Log "settings dump failed: $_" }

Log "v15 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
