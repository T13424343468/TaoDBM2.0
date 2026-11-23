using Microsoft.UI.Xaml;
using System.IO;

namespace TaoDBM;

public partial class App : Application
{
    public static Window? Shell;

    public App()
    {
        InitializeComponent();
        // .sql 三件套：GBK 编码需先注册 CodePages 提供者（.NET Core 默认不带）；失败不拖垮启动（回落 UTF-8 口径）
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch (Exception ex) { Core.AppLog.Write($"编码提供者注册失败: {ex.Message}"); }
        UnhandledException += (_, e) =>
        {
            CrashLog.Write($"UnhandledException: {e.Message}\n{e.Exception}");
            e.Handled = true;
        };
        // v3.22 崩溃日志随包落盘令：非 UI 线程崩溃与未观察任务异常同样留痕（进程级兜底，不拖垮启动）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write($"AppDomain.UnhandledException (Terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write($"TaskScheduler.UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // P-5 拍板（11-19）：多应用实例并行、不做单实例互斥锁——#136 红账移除夜间批次③的单实例重定向
            // （其用了桌面桥版 AppInstance 实例 API，WinAppSDK 无 IsRunning 且 GetActivatedEventArgs 为静态）。
            // 【2026-11-20 夜批③】.sql 双击按正确口径落码：新进程先把 .sql 交给最近激活的存活实例
            // （命名管道 + SqlHandoffRegistry 登记簿握手，无互斥锁）；无活跃实例才回落本实例命令行装载。
            var sqlArgs = UI.SqlHandoffHost.SqlArgsFromCommandLine();
            if (UI.SqlHandoffHost.TryHandoffToExisting(sqlArgs))
            {
                Current.Exit();
                return;
            }

            // Tao 系列存储默认 D 盘：首次运行把旧 C 盘数据迁过去（已有同名文件不覆盖）
            Core.TaoPaths.MigrateLegacyFiles();
            // P4：系统通知注册（官方流程：先挂 NotificationInvoked 再 Register）。注册失败只进日志，不拖垮启动。
            // 需真机验证：通知弹窗样式与点击跳转，沙箱无法真机验证。
            UI.ToastCenter.EnsureRegistered();
            // 水墨为默认主题：安装资源画刷并应用已存选择（Tao 系列默认水墨，可切回清爽蓝）
            UI.Ant.ApplyTheme(Core.ThemeStore.Load());
            Shell = new MainWindow();
            // P-5：本实例起 .sql 交接管道（进最近激活窗口）；窗口激活刷新最近激活时刻
            UI.SqlHandoffHost.BeginListening();
            Shell.Activated += (_, _) => UI.SqlHandoffHost.OnWindowActivated();
            // 进程残留专项：最后一个窗口关闭后，框架消息循环/COM 可能把自包含进程吊住，
            // 显式退出并加延迟强杀兜底，确保任务管理器里不留残影。
            Shell.Closed += (_, _) =>
            {
                UI.SqlHandoffHost.OnAppClosing();
                Current.Exit();
                Task.Run(() => Core.Safe.CleanupAsync(async () =>
                {
                    await Task.Delay(1000);
                    Environment.Exit(0);
                }, "退出强杀兜底"));
            };
            Shell.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write($"OnLaunched: {ex.Message}\n{ex}");
            throw;
        }
    }
}
/// <summary>崩溃日志：未处理异常与启动失败追加写入 TaoPaths.Root\crash.log（默认 D:\TaoDBMData\TaoDBM\crash.log，D 盘不可用回退 %LOCALAPPDATA%\TaoDBM）。</summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(string text)
    {
        try
        {
            var dir = Core.TaoPaths.Root;   // Tao 系列存储默认 D 盘
            Directory.CreateDirectory(dir);
            // v3.22：每条崩溃记录带版本号，异常类型与堆栈由调用方传入的 Exception.ToString() 承载
            var version = typeof(CrashLog).Assembly.GetName().Version?.ToString() ?? "unknown";
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [v{version}] {text}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不进去也不能让程序再崩一次
        }
    }
}
