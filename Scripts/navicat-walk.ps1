# navicat-walk.ps1 — 竞品对照走查 v12：真库对照·像素门控（v11 实锤：试用弹窗出现晚于点击，全程落空；Qt 无 UIA，count=0）
# v12 策略：① 像素轮询等试用弹窗（白面板检测）再点试用；② 向导/关弹窗沿用 v10 定谳坐标；
# ③ 连接下拉打开后扫描菜单文字行落日志并点第 6 行（SQLite 猜测位）；④ SQLite 表单盲填路径+保存；
# ⑤ 树双击展开；⑥ 每步截图；⑦ 落 %APPDATA%\PremiumSoft 文件清单（为 v13 预置连接文件定位）。
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
public class Win12 {
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@
Add-Type -TypeDefinition $sig
[Win12]::ShowWindow([Win12]::GetConsoleWindow(), 6) | Out-Null

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative12 {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(150); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
  public static void DblClick(int x, int y) { Click(x, y); System.Threading.Thread.Sleep(80); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Focus-Navicat {
  $p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    [Win12]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null
    Start-Sleep -Milliseconds 800
  }
  return $p
}

Log "v12 start exe=$ExePath"

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

# 1) 像素轮询等试用弹窗（白面板 @512,300），最长 90s
$trialReady = $false
for ($i = 0; $i -lt 45; $i++) {
  Start-Sleep -Seconds 2
  try {
    $c = Get-Pixel 512 300
    if ($c.R -gt 235 -and $c.G -gt 235 -and $c.B -gt 235) { $trialReady = $true; Log "trial dialog detected at t=$((25+$i*2))s pixel=$($c.R),$($c.G),$($c.B)"; break }
  } catch {}
}
if (-not $trialReady) { Log "WARN trial dialog not detected by pixel; click anyway" }
Shot "shot-n01-trial.png"
[MouseNative12]::Click(717, 515)
Start-Sleep -Seconds 20

# 2) 向导 4 连击 + 关新功能弹窗（v10 定谳坐标）
for ($i = 1; $i -le 4; $i++) { [MouseNative12]::Click(869, 669); Start-Sleep -Seconds 6 }
$p = Focus-Navicat
Log "after wizard window='$($p.MainWindowTitle)'"
[MouseNative12]::Click(696, 187)
Start-Sleep -Seconds 3
Shot "shot-n02-main.png"

# 3) 连接下拉 → 扫描菜单行 → 点第 6 行（SQLite 猜测位）
[MouseNative12]::Click(48, 75)
Start-Sleep -Seconds 3
Shot "shot-n03-menu.png"
# 扫描菜单文字行：x∈[25,220]，y∈[90,520]，找暗像素行带
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
  [MouseNative12]::Click(100, $rows[5])
  Log "clicked menu row 6 at y=$($rows[5])"
} elseif ($rows.Count -gt 0) {
  [MouseNative12]::Click(100, $rows[$rows.Count - 1])
  Log "clicked last menu row at y=$($rows[$rows.Count - 1])"
} else {
  Log "WARN no menu rows detected; fallback click y=249"
  [MouseNative12]::Click(100, 249)
}
Start-Sleep -Seconds 5
Shot "shot-n04-conn-dialog.png"

# 4) SQLite 连接表单：连接名 → 数据库文件路径 → 保存（盲坐标+截图取证）
[MouseNative12]::Click(450, 180)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("sample-sqlite")
Start-Sleep -Seconds 1
[MouseNative12]::Click(450, 265)
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("C:\taodb-sample\sample.db")
Start-Sleep -Seconds 1
Shot "shot-n05-filled.png"
[MouseNative12]::Click(640, 660)
Start-Sleep -Seconds 5
Shot "shot-n06-saved.png"

# 5) 树：双击连接节点（我的连接下第一项）→ main → users
[MouseNative12]::DblClick(100, 140)
Start-Sleep -Seconds 6
Shot "shot-n07-tree.png"
[MouseNative12]::DblClick(120, 170)
Start-Sleep -Seconds 4
Shot "shot-n08-db.png"
[MouseNative12]::DblClick(250, 220)
Start-Sleep -Seconds 8
Shot "shot-n09-data.png"

# 6) 落 PremiumSoft 设置目录清单（为 v13 预置连接文件定位）
try {
  $dirs = Get-ChildItem "$env:APPDATA\PremiumSoft" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 80
  foreach ($d in $dirs) { Log "settings: $($d.FullName.Replace($env:APPDATA,'~APPDATA'))" }
} catch { Log "settings dump failed: $_" }

Log "v12 done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
