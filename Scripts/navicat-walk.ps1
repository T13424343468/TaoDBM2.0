# navicat-walk.ps1 — 竞品对照走查 v21：真库对照·类名状态机完版（v17 定谳真根因：类名匹配模式缺前导星号，-like 整串匹配时模式锚在 hwnd= 串首永不命中，Wait/Test/Focus 三函数全部失效，关断循环从未执行；v15 双通配诊断不完整；
# v14 截图定谳：ESC 一招关掉试用弹窗，TAB+ENTER 推进 WhatsNew 向导页，向导末页=改善 Navicat(不共享/共享)
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
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, 0, UIntPtr.Zero); }
  public static void DblClick(int x, int y) { Click(x, y); System.Threading.Thread.Sleep(80); mouse_event(LEFTDOWN, 0, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(LEFTUP, 0, 0, 0, 0, UIntPtr.Zero); }
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
  $ClassPattern = $ClassPattern.TrimEnd('*')
  $wins = Get-NavWindows -ProcId $script:navPid
  foreach ($w in $wins) { if ($w -like "*class='$ClassPattern'*") { return $true } }
  return $false
}

function Wait-WindowClass {
  param([string]$ClassPattern, [int]$TimeoutSec = 60)
  $ClassPattern = $ClassPattern.TrimEnd('*')
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $wins = Get-NavWindows -ProcId $script:navPid
    foreach ($w in $wins) { if ($w -like "*class='$ClassPattern'*") { return $w } }
    Start-Sleep -Seconds 2
  }
  return $null
}

function Focus-WindowClass {
  param([string]$ClassPattern)
  $ClassPattern = $ClassPattern.TrimEnd('*')
  $wins = Get-NavWindows -ProcId $script:navPid
  foreach ($w in $wins) {
    if ($w -like "*class='$ClassPattern'*" -and $w -match 'hwnd=(-?\d+)') {
      $h = [IntPtr][int64]$Matches[1]
      try { [Win15]::SetForegroundWindow($h) | Out-Null } catch {}
      Start-Sleep -Milliseconds 600
      return $true
    }
  }
  return $false
}

Log "v21 start exe=$ExePath"

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

# 3) 走完改善向导（TWhatsNew）：4×下一步(869,669)推进到末页→不共享(746,667)→共享(887,667)→ESC，每招后类名验证
$wizSeen = Wait-WindowClass -ClassPattern 'TWhatsNew*' -TimeoutSec 30
if ($wizSeen) {
  Log "wizard window: $wizSeen"
  for ($i = 1; $i -le 8; $i++) {
    if (-not (Test-WindowClass -ClassPattern 'TWhatsNew*')) { break }
    Focus-WindowClass -ClassPattern 'TWhatsNew*' | Out-Null
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Log "wizard tab-enter $i"
    Start-Sleep -Seconds 3
    Shot ("shot-n13-wizard-te" + $i + ".png")
  }
  $wizGone = -not (Test-WindowClass -ClassPattern 'TWhatsNew*')
  Log ("wizard dismissed=" + $wizGone + " by tab-enter-loop")
  for ($i = 1; $i -le 5 -and -not $wizGone; $i++) {
    Focus-WindowClass -ClassPattern 'TWhatsNew*' | Out-Null
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Log "wizard esc $i"
    Start-Sleep -Seconds 3
    $wizGone = -not (Test-WindowClass -ClassPattern 'TWhatsNew*')
    Shot ("shot-n14-wizard-esc" + $i + ".png")
    Log ("wizard dismissed=" + $wizGone + " by esc" + $i)
  }
  if (-not $wizGone) { Log "WARN wizard still up after keyboard methods" }
} else { Log "no wizard window; continue" }
Start-Sleep -Seconds 5

# 4) 关功能导览（TFeatureTour，v19 新现）键盘 ESC + 主界面截图
$tourGone = -not (Test-WindowClass -ClassPattern 'TFeatureTour*')
for ($i = 1; $i -le 3 -and -not $tourGone; $i++) {
  Focus-WindowClass -ClassPattern 'TFeatureTour*' | Out-Null
  [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
  Log "tour esc $i"
  Start-Sleep -Seconds 2
  $tourGone = -not (Test-WindowClass -ClassPattern 'TFeatureTour*')
  Log ("tour dismissed=" + $tourGone + " by esc" + $i)
}
Shot "shot-n02-main.png"
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win-main: $w" }

# 5) 键盘建连接（v19 定谳：鼠标注入全程落空、光标钉死屏幕中心；键盘全通）：
#    Alt+F 打开文件菜单 → DOWN 到第 2 项「新建连接...」→ ENTER
Focus-WindowClass -ClassPattern 'TNavicatMainForm*' | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("%f")
Start-Sleep -Seconds 2
Shot "shot-n03-filemenu.png"
[System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 3
Shot "shot-n04-conn-dialog.png"
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win-conn: $w" }

# 5.5) 类型选择：v20 截图定谳网格第 1 行 = MySQL(默认选中),PostgreSQL,SQL Server,Oracle,SQLite → RIGHT×4 到 SQLite → ENTER
for ($i = 1; $i -le 4; $i++) {
  [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
  Start-Sleep -Milliseconds 300
}
Shot "shot-n05-picker.png"
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 3
Shot "shot-n06-connform.png"
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win-form: $w" }

# 6) SQLite 连接表单（键盘）：连接名 → TAB×2（v21 实证 TAB×1 落在类型单选钮，再 TAB 才到路径字段）→ 数据库文件路径 → ENTER 保存
[System.Windows.Forms.SendKeys]::SendWait("sample-sqlite")
Start-Sleep -Milliseconds 500
Shot "shot-n06a-name.png"
[System.Windows.Forms.SendKeys]::SendWait("{TAB}")
Start-Sleep -Milliseconds 400
Shot "shot-n06b-tab1.png"
[System.Windows.Forms.SendKeys]::SendWait("{TAB}")
Start-Sleep -Milliseconds 400
Shot "shot-n06c-tab2.png"
[System.Windows.Forms.SendKeys]::SendWait("C:\taodb-sample\sample.db")
Start-Sleep -Milliseconds 500
Shot "shot-n06e-filled.png"
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 5
Shot "shot-n06f-saved.png"
foreach ($w in (Get-NavWindows -ProcId $script:navPid)) { Log "win-saved: $w" }

# 6.5) 功能导览可能再弹（v20 实证），再关一次
$tourGone2 = -not (Test-WindowClass -ClassPattern 'TFeatureTour*')
for ($i = 1; $i -le 3 -and -not $tourGone2; $i++) {
  Focus-WindowClass -ClassPattern 'TFeatureTour*' | Out-Null
  [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
  Log "tour2 esc $i"
  Start-Sleep -Seconds 2
  $tourGone2 = -not (Test-WindowClass -ClassPattern 'TFeatureTour*')
  Log ("tour2 dismissed=" + $tourGone2 + " by esc" + $i)
}

# 7) 树展开改纯键盘（v21 实证鼠标注入全程失效，DblClick 三连全落空）：RIGHT 展开连接 → DOWN 到库文件 → RIGHT 展开 → DOWN 到 users → ENTER 打开
[System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
Start-Sleep -Seconds 5
Shot "shot-n07-tree.png"
[System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
Start-Sleep -Milliseconds 800
[System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
Start-Sleep -Seconds 5
Shot "shot-n08-db.png"
[System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
Start-Sleep -Milliseconds 800
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 8
Shot "shot-n09-data.png"

# 8) 落 PremiumSoft 设置目录清单（注册表清单 v14 已到手：Registration18XCS）
try {
  $dirs = Get-ChildItem "$env:APPDATA\PremiumSoft" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 60
  foreach ($d in $dirs) { Log "settings: $($d.FullName.Replace($env:APPDATA,'~APPDATA'))" }
} catch { Log "settings dump failed: $_" }

Log "v21 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
