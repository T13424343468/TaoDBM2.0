﻿# navicat-walk.ps1 — 竞品对照走查：云真机真装真开真截图（Navicat Premium Lite 简体中文版）
# 口径：不猜界面，每步截图留证；首跑以「装上、开起、主界面、新建连接」为硬目标，逐轮迭代加深
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$mouseSrc = @'
using System;
using System.Runtime.InteropServices;
public class MouseNative {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
  const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
  public static void DblClick(int x, int y) { Click(x, y); System.Threading.Thread.Sleep(80); mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero); }
  public static void RightClick(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); mouse_event(RIGHTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(RIGHTUP, 0, 0, 0, IntPtr.Zero); }
}
'@
Add-Type -TypeDefinition $mouseSrc

function Save-Shot([string]$name) {
  try {
    $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
    $b.Save("$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
    Write-Host "[SHOT] $name"
  } catch { Write-Host "[SHOT-FAIL] $name : $_" }
}

function Focus-Navicat {
  $p = Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) {
    $sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
    $t = Add-Type -MemberDefinition $sig -Name FG -Namespace W -PassThru
    $t::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600
  }
  return $p
}

Write-Host "[WALK] start exe=$ExePath"
# n00: 启动
Start-Process -FilePath $ExePath
Start-Sleep -Seconds 40
Save-Shot "nav-00-firstrun"
# 首启弹窗兜底：回车（默认按钮多为同意/确定）再截
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 6
Save-Shot "nav-01-after-enter"
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 3
Save-Shot "nav-02-main"

# n03: 新建连接（Ctrl+N）
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("^n")
Start-Sleep -Seconds 6
Save-Shot "nav-03-newconn"
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 2

# n04: 主菜单兜底（Alt 键拉出菜单栏）
Focus-Navicat | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("%")
Start-Sleep -Seconds 2
Save-Shot "nav-04-altmenu"
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")

Write-Host "[WALK] done"
Get-Process -Name "navicat" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
exit 0
