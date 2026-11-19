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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // P-5 拍板（11-19）：多应用实例并行、不做单实例互斥锁——#136 红账移除夜间批次③的单实例重定向
            // （其用了桌面桥版 AppInstance 实例 API，WinAppSDK 无 IsRunning 且 GetActivatedEventArgs 为静态，编译必红）。
            // 双击 .sql 由新实例按自身命令行直接装载（MainWindow.SqlLoad 命令行装载段）；
            // 「进最近激活窗口/实例」的跨实例交接按 P-5 另行批次落码，不在此修红轮顺手实现。

            // Tao 系列存储默认 D 盘：首次运行把旧 C 盘数据迁过去（已有同名文件不覆盖）
            Core.TaoPaths.MigrateLegacyFiles();
            // P4：系统通知注册（官方流程：先挂 NotificationInvoked 再 Register）。注册失败只进日志，不拖垮启动。
            // 需真机验证：通知弹窗样式与点击跳转，沙箱无法真机验证。
            UI.ToastCenter.EnsureRegistered();
            // 水墨为默认主题：安装资源画刷并应用已存选择（Tao 系列默认水墨，可切回清爽蓝）
            UI.Ant.ApplyTheme(Core.ThemeStore.Load());
            Shell = new MainWindow();
            // 进程残留专项：最后一个窗口关闭后，框架消息循环/COM 可能把自包含进程吊住，
            // 显式退出并加延迟强杀兜底，确保任务管理器里不留残影。
            Shell.Closed += (_, _) =>
            {
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
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不进去也不能让程序再崩一次
        }
    }
}
