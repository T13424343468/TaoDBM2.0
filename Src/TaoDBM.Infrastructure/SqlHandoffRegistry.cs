using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TaoDBM.Core;

/// <summary>.sql 跨实例交接的实例登记簿（P-5 拍板落地，2026-11-20 夜批③）：
/// 每个实例经命名管道收 .sql 交接；本簿记录「管道名 / 进程号 / 最近激活时刻」，
/// 新进程据此把 .sql 交给最近激活的存活实例。纯文件逻辑，无 UI 依赖，LogicTest 直测：
/// 解析容错、死亡进程清理、最近激活排序、写读往返。</summary>
public static class SqlHandoffRegistry
{
    private static readonly object Gate = new();

    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("sqlhandoff.registry");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    public sealed record Entry(string PipeName, int Pid, DateTime LastActiveUtc);

    /// <summary>登记本实例（管道名 + 进程号），激活时刻 = 当前 UTC。</summary>
    public static void Register(string pipeName, int pid) => Upsert(pipeName, pid, DateTime.UtcNow);

    /// <summary>窗口激活即刷新本实例的最近激活时刻（「进最近激活窗口」的排序依据）。</summary>
    public static void Touch(string pipeName, int pid) => Upsert(pipeName, pid, DateTime.UtcNow);

    /// <summary>实例退出即摘除登记，避免交给已退出进程。</summary>
    public static void Unregister(string pipeName)
    {
        lock (Gate)
        {
            try
            {
                var entries = LoadInner().Where(e => !string.Equals(e.PipeName, pipeName, StringComparison.Ordinal)).ToList();
                WriteInner(entries);
            }
            catch { /* 登记簿写失败不拖垮退出 */ }
        }
    }

    /// <summary>最近激活的存活实例（不含本进程自身）：死亡进程条目即时清理。</summary>
    public static Entry? MostRecentAlive(int selfPid)
    {
        lock (Gate)
        {
            try
            {
                var entries = LoadInner();
                var alive = entries.Where(e => e.Pid != selfPid && PidAlive(e.Pid))
                                   .OrderByDescending(e => e.LastActiveUtc).ToList();
                var dead = entries.Where(e => e.Pid != selfPid && !PidAlive(e.Pid)).ToList();
                if (dead.Count > 0) WriteInner(entries.Where(e => !dead.Contains(e)).ToList());
                return alive.Count > 0 ? alive[0] : null;
            }
            catch { return null; }
        }
    }

    /// <summary>管道名是否已登记（防同实例重复起服务）。</summary>
    public static bool IsRegistered(string pipeName)
    {
        lock (Gate)
        {
            try { return LoadInner().Any(e => string.Equals(e.PipeName, pipeName, StringComparison.Ordinal)); }
            catch { return false; }
        }
    }

    private static void Upsert(string pipeName, int pid, DateTime at)
    {
        lock (Gate)
        {
            try
            {
                var entries = LoadInner().Where(e => !string.Equals(e.PipeName, pipeName, StringComparison.Ordinal)).ToList();
                entries.Add(new Entry(pipeName, pid, at));
                WriteInner(entries);
            }
            catch { /* 登记簿写失败不拖垮启动 */ }
        }
    }

    private static List<Entry> LoadInner()
    {
        var list = new List<Entry>();
        if (!FileExists()) return list;
        foreach (var raw in System.IO.File.ReadAllLines(File))
        {
            var e = Parse(raw);
            if (e is not null) list.Add(e);
        }
        return list;
    }

    /// <summary>行格式：pipeName|pid|yyyyMMddHHmmssfff（UTC）。坏行整行丢弃，绝不闪退。</summary>
    public static Entry? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split('|');
        if (parts.Length != 3 || parts[0].Length == 0) return null;
        if (!int.TryParse(parts[1], out var pid) || pid <= 0) return null;
        if (!DateTime.TryParseExact(parts[2], "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return null;
        return new Entry(parts[0], pid, at.ToUniversalTime());
    }

    public static string Format(Entry e) => $"{e.PipeName}|{e.Pid}|{e.LastActiveUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}";

    private static void WriteInner(List<Entry> entries)
    {
        var dir = Path.GetDirectoryName(File);
        if (dir is not null && dir.Length > 0) Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, string.Join(Environment.NewLine, entries.Select(Format)) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static bool FileExists() => _file.Length > 0 ? System.IO.File.Exists(File) : System.IO.File.Exists(TaoPaths.FilePath("sqlhandoff.registry"));

    private static bool PidAlive(int pid)
    {
        try
        {
            Process.GetProcessById(pid);
            // Linux 僵尸态（已死未收尸）判死：进程不可能再收管道交接，按存活只会把交接交给尸体；
            // Windows 无 /proc，读取失败走原口径，行为不变。
            try
            {
                var stat = System.IO.File.ReadAllText($"/proc/{pid}/stat");
                var close = stat.LastIndexOf(')');
                if (close >= 0 && close + 2 < stat.Length && stat[close + 2] == 'Z') return false;
            }
            catch { }
            return true;
        }
        catch { return false; }
    }
}
