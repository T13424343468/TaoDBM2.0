# TaoDBM 安装器运行时保障 (2026-11-21 全系安装器新标准, 老板令 MSG-20261121T1310)
# 口径: 安装包不内置 .NET 运行时; 安装期以系统实际注册为准全盘检测所需运行时;
#   有则零下载直接用; 无则联网下载官方运行时并显示进度, 装进系统统一 .NET 目录
#   (C:\Program Files\dotnet, 与现有版本同处一地, 不单独建运行时目录);
#   无网且无运行时给通俗重试指引。
# 云测钩子: 标记文件 C:\ProgramData\TaoDBM\force-runtime-download.flag 或环境变量 TAODBM_FORCE_RUNTIME_DOWNLOAD=1 强制走下载路 (断言二用), 日常安装两者皆无即走检测路。
# 写法与 TaoZip 首发实现同口径 (TaoZip.PC/tools/msi/EnsureRuntime.ps1, 提交 1032c44), 不另起炉灶。
$ErrorActionPreference = 'Stop'
# #221 取证: CA 秒败(-196608)且 TEMP/Windows/ProgramData 三路日志全空, 疑脚本未起或秒崩;
# 顶部硬写启动标记(独立于 Write-RtLog, 就算日志函数本身炸也留痕), trap 兜底落异常
$rtCaOut = 'C:\ProgramData\TaoDBM\rt-ca-out.txt'
try {
    New-Item -ItemType Directory -Path (Split-Path $rtCaOut) -Force | Out-Null
    Add-Content -Path $rtCaOut -Value ("{0} rt-script-start pid=$PID ps=$($PSVersionTable.PSVersion) eap=$ErrorActionPreference" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
} catch { }
trap {
    try { Add-Content -Path $rtCaOut -Value ("{0} rt-script-trap: {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $_.Exception.ToString()) } catch { }
}
$log = Join-Path $env:TEMP 'taodbm-runtime.log'
$logMirror = Join-Path $env:ProgramData 'TaoDBM\taodbm-runtime.log'
try { New-Item -ItemType Directory -Path (Split-Path $logMirror) -Force | Out-Null } catch { }
function Write-RtLog([string]$m) {
    $line = ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m)
    try { Add-Content -Path $log -Value $line } catch { }
    try { Add-Content -Path $logMirror -Value $line } catch { }
}
function Test-DesktopRuntime10 {
    # 以系统实际注册为准: 注册表 InstalledVersions 任一 10.x 键, 或统一目录 shared 下有 10.x 桌面运行时
    # #215 定谳: CA 以 32 位 PowerShell 起动 (x86 包 SystemFolder→SysWOW64), 注册表与 ProgramFiles
    # 都被重定向到 32 位视图, 64 位运行时永远判缺 → 恒走下载路; 显式走 64 位注册表视图与 ProgramW6432
    try {
        $rk64 = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
        $sub = $rk64.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        if ($sub) { foreach ($n in $sub.GetSubKeyNames()) { if ($n -like '10.*') { return $true } } }
    } catch { }
    try {
        $pf = $env:ProgramW6432
        if (-not $pf) { $pf = $env:ProgramFiles }
        $shared = Get-ChildItem (Join-Path $pf 'dotnet\shared\Microsoft.WindowsDesktop.App') -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -like '10.*' }
        return ($null -ne $shared)
    } catch { return $false }
}
# #229 定谳: 环境变量经 msiexec 常驻服务传不到 CA, 云测强制下载路改用标记文件 (env 兼容保留)
$forceFlag = Join-Path (Split-Path $logMirror) 'force-runtime-download.flag'
$forced = ($env:TAODBM_FORCE_RUNTIME_DOWNLOAD -eq '1') -or (Test-Path $forceFlag)
if (-not $forced -and (Test-DesktopRuntime10)) {
    Write-RtLog 'runtime-present zero-download'
    exit 0
}
Write-RtLog 'runtime-missing, downloading official runtime'
# #212 定谳: 桌面运行时 exe 是 burn 包(内含 MSI), 在安装中的 MSI 自定义动作里再起 MSI 会撞
# _MSIExecute 互斥(嵌套安装), 静默链上表现为装机即回滚 1603 —— 弃用, 也不再下载当探测。
# 改用微软官方 dotnet-install.ps1(zip 解包, 无 MSI 事务), 装进系统统一目录 C:\Program Files\dotnet,
# 与「装进系统统一 .NET 目录」的令一致; Test-DesktopRuntime10 的 shared 目录检查可验。
# 联网探测 = 直接拉 dotnet-install.ps1, 拉不到即无网, 不再白拉 60MB burn 包。
$pf64 = $env:ProgramW6432
if (-not $pf64) { $pf64 = $env:ProgramFiles }
$installDir = Join-Path $pf64 'dotnet'
$dlScript = Join-Path $env:TEMP 'dotnet-install.ps1'
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dlScript -UseBasicParsing
} catch {
    Write-RtLog ("dotnet-install-script-download-failed " + $_.Exception.Message)
    # 无网且无运行时: 说人话重试指引 (交互装弹窗; 静默装落日志后中止)
    $msg = "TaoDBM 需要联网下载 .NET 运行时（约 60 MB），当前没有检测到网络连接。`n`n请连上网络后重新运行安装程序即可，无需其他操作。"
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($msg, 'TaoDBM 安装', 'OK', 'Information') | Out-Null
    } catch { }
    exit 1602
}
$eapBak = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $dlScript -Runtime windowsdesktop -Channel 10.0 -InstallDir $installDir -NoPath 2>&1 |
    ForEach-Object { Write-RtLog ("dotnet-install: " + $_) }
$ErrorActionPreference = $eapBak
Write-RtLog ("dotnet-install-exit " + $LASTEXITCODE)
# #231 定谳: dotnet-install.ps1 经 & 调用不设 LASTEXITCODE(空), '' -ne 0 为真 → 提前 exit 0,
# 'runtime-download-install ok' 永远落不了账; 且其提示走 Write-Host, 5.1 管道抓不到属正常。
# 唯一真闸门 = 装后实测 Test-DesktopRuntime10, 不再按 LASTEXITCODE 提前退场。
if (-not (Test-DesktopRuntime10)) { Write-RtLog 'verify-failed'; exit 31337 }
Write-RtLog 'runtime-download-install ok'
exit 0
