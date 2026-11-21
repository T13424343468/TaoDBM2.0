using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TaoDBM.UI;

/// <summary>.sql 跨实例交接主机（P-5 拍板落地，2026-11-20 夜批③）：
/// 多实例并行不互斥；每个实例起一条命名管道收交接（管道名登记于 SqlHandoffRegistry，
/// 窗口激活即刷新最近激活时刻）。双击 .sql 的新进程先试最近激活实例，交出去即退出；
/// 无活跃实例才回落新实例命令行装载（第一批已落那半保留）。</summary>
public static class SqlHandoffHost
{
    private static string _pipeName = "";
    private static readonly object Gate = new();

    /// <summary>本实例的管道名（启动监听后非空）。</summary>
    public static string PipeName => _pipeName;

    /// <summary>命令行里的 .sql 文件（双击关联 / 拖到 exe）。</summary>
    public static System.Collections.Generic.List<string> SqlArgsFromCommandLine()
    {
        var paths = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var a in Environment.GetCommandLineArgs().Skip(1))
            {
                if (a.Length == 0 || a[0] == '-') continue;
                if (Path.GetExtension(a).Equals(".sql", StringComparison.OrdinalIgnoreCase))
                    paths.Add(a);
            }
        }
        catch (Exception ex) { Core.AppLog.Write($".sql 交接-参数: {ex.Message}"); }
        return paths;
    }

    /// <summary>新进程双击 .sql：尝试交给最近激活的存活实例。交付成功返回 true（调用方退出自身）。
    /// 无活跃实例、管道超时或写失败一律返回 false，回落新实例命令行装载。</summary>
    public static bool TryHandoffToExisting(System.Collections.Generic.IReadOnlyList<string> sqlPaths)
    {
        if (sqlPaths.Count == 0) return false;
        try
        {
            var target = Core.SqlHandoffRegistry.MostRecentAlive(selfPid: Environment.ProcessId);
            if (target is null) return false;
            using var client = new NamedPipeClientStream(".", target.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            foreach (var p in sqlPaths) writer.WriteLine(p);
            writer.WriteLine("END");
            Core.AppLog.Write($".sql 交接-交付 {sqlPaths.Count} 个文件 → {target.PipeName}");
            return true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Write($".sql 交接-交付失败（回落新实例）: {ex.Message}");
            return false;
        }
    }

    /// <summary>启动本实例的交接监听（管道 + 登记簿）。失败不拖垮启动；主窗口 Activated 时调 Touch。</summary>
    public static void BeginListening()
    {
        try
        {
            lock (Gate)
            {
                if (_pipeName.Length > 0) return;
                _pipeName = $"TaoDBM.SqlHandoff.{Guid.NewGuid():N}";
            }
            Core.SqlHandoffRegistry.Register(_pipeName, Environment.ProcessId);
            var thread = new Thread(PipeLoop) { IsBackground = true, Name = "SqlHandoffHost" };
            thread.Start();
        }
        catch (Exception ex) { Core.AppLog.Write($".sql 交接-监听启动失败: {ex.Message}"); }
    }

    /// <summary>窗口激活即刷新最近激活时刻；实例退出即摘除登记。</summary>
    public static void OnWindowActivated() { if (_pipeName.Length > 0) Core.SqlHandoffRegistry.Touch(_pipeName, Environment.ProcessId); }
    public static void OnAppClosing() { if (_pipeName.Length > 0) Core.SqlHandoffRegistry.Unregister(_pipeName); }

    /// <summary>管道服务循环：逐连接收文件路径行（END 收口），逐条转发主窗口装载（只装载不执行）。</summary>
    private static void PipeLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                var paths = new System.Collections.Generic.List<string>();
                using (var reader = new StreamReader(server))
                {
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (line == "END") break;
                        if (line.Trim().Length > 0) paths.Add(line.Trim());
                    }
                }
                if (paths.Count > 0)
                {
                    var shell = App.Shell as MainWindow;
                    shell?.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            foreach (var p in paths) _ = shell.LoadSqlFileAsync(p);
                            shell.ActivateFromHandoff();
                        }
                        catch (Exception ex) { Core.AppLog.Write($".sql 交接-装载: {ex.Message}"); }
                    });
                }
            }
            catch (Exception ex)
            {
                Core.AppLog.Write($".sql 交接-管道: {ex.Message}");
                Task.Delay(500).Wait();
            }
        }
    }
}
