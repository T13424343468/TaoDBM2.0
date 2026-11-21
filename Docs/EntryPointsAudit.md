# 入口对账（R4 盲区补齐，2026-11-21）

> 背景：2026-11-21 10:55 盲区复审令——R1 老库真机实测豁免（老板钦定），R2/R3/R4/R10 全部要补。
> 本文档对 R4「入口对账」逐项落账：七类用户入口的代码位置、自动化对账方式与 LogicTest 用例号。
> 对账分两路：**Infrastructure 层可实例化的走真测**（LogicTest 直测），**App 层 UI 类会拉 WinUI 依赖，
> LogicTest 不引用，用源码文本断言**（读 .cs 源文件断言关键 API 存在，防回归删除）。

## 七类入口逐项对账

| # | 入口 | 代码位置 | 对账方式 | 用例号 |
|---|------|----------|----------|--------|
| 1 | 快捷键 F1 全局手册 | `Src/TaoDBM.App/MainWindow.xaml.cs`（KeyboardAccelerator 挂根 Grid，任何焦点下响应） | 源码文本断言 | ENT-快捷键-F1全局手册挂根Grid |
| 1b | 工作表快捷键 F5 / Ctrl+Enter / Ctrl+Space / Ctrl+S | `Src/TaoDBM.App/UI/WorkTab.cs`（AddAccelerator 四连） | 源码文本断言 | ENT-快捷键-工作表四键齐全 |
| 2 | 命令行 .sql 参数 | `Src/TaoDBM.App/UI/SqlHandoffHost.cs` SqlArgsFromCommandLine（跳过 `-` 开头、只收 .sql 后缀）；`App.xaml.cs` 启动即试交接、交出即退出 | 源码文本断言 | ENT-命令行-只收sql后缀 / ENT-命令行-交接失败回落新实例 |
| 2b | 管道交接登记簿 | `Src/TaoDBM.Infrastructure/SqlHandoffRegistry.cs`（pipeName\|pid\|yyyyMMddHHmmssfff UTC；pid≤0 拒收；坏行整行丢弃；死亡进程即时清理） | **真测**（UseFile 重定向临时文件） | ENT-管道-登记与防重复 / 重复登记不叠行 / pid非正拒收 / 坏行整行丢弃 / 好行往返 / 死亡进程被清理 |
| 3 | .sql 文件关联（双击打开） | `Installer/Package.wxs`（HKLM Software\Classes\TaoDBM.SqlFile + .sql 键） | 源码文本断言 | ENT-文件关联-注册表键齐全 |
| 4 | 拖放 .sql 进窗口 | `Src/TaoDBM.App/MainWindow.SqlLoad.cs`（WorkTabs.AllowDrop = true） | 源码文本断言 | ENT-拖放-允许拖入 |
| 5 | 托盘图标 | `Src/TaoDBM.App/UI/TrayIcon.cs`（单例；OpenRequested / NewConnectionRequested / ThemeRequested / SettingsRequested / ExitRequested 五事件；最近列表 RecentMax=8） | 源码文本断言 | ENT-托盘-五事件齐全 / ENT-托盘-最近列表上限8 |
| 6 | 窗口状态记忆 | `Src/TaoDBM.Infrastructure/WindowStateStore.cs`（windowstate.json；损坏/缺失回落默认；默认尺寸工作区 80% 封顶 1600×900 下限 960×640） | **真测**（UseFile 重定向） | ENT-窗口-缺失回默认 / 写读往返 / 损坏回默认 / 默认尺寸口径 |
| 6b | .sql 打开偏好 | `Src/TaoDBM.Infrastructure/SqlOpenPrefs.cs`（sqlopen.json；默认「最近激活窗口」开；损坏回落默认） | **真测**（UseFile 重定向） | ENT-偏好-缺失默认开 / 写读往返 / 损坏回落默认 |
| 7 | 数据目录迁移 | `Src/TaoDBM.Infrastructure/TaoPaths.cs`（Tao 系列存储默认 D 盘探测回退 LOCALAPPDATA；MigrateLegacyFiles 首启迁移 connections/favorites/tabsession/theme/history 五文件） | 真测（路径口径）+ 迁移逻辑既有覆盖 | ENT-迁移-路径口径可复现 |

## 与 R10 的衔接

R10「新用户全流程走查」由 `Scripts/ui-walk-plan-newuser.json` 承载（首次启动 → 建连接 → 浏览 → 查询 → 导出），
在 `.github/workflows/WindowsBuild.yml` 独立 matrix 作业跑真机 UIA 走查，与本对账互补：
本档管「入口在不在、坏了知不知道」，R10 管「新用户从头走到尾通不通」。

## 已知边界（如实记）

- 托盘图标在无交互 CI 会话（Session 0）不渲染气泡与图标，托盘行为只能事件级对账，真机气泡留 R10 人工抽验。
- 文件关联注册表键在 MSI 安装作业里生效，LogicTest 只断言安装器源码含键；真双击链路由 R10 走查兜底。
- 命名管道端到端交接（跨进程真写管道）属进程级，LogicTest 不起第二个实例；kill/重启演练
  （`Scripts/kill-restart-drill.ps1`）覆盖登记簿的死亡进程清理半场，管道写半场由 R10 人工抽验。
