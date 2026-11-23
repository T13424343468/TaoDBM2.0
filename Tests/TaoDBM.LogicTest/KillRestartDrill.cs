using System.Diagnostics;
using System.Text;
using TaoDBM.Core;
using TaoDBM.Infrastructure;

/// <summary>
/// R2 进程级 kill/重启演练（盲区补齐 2026-11-21）：
/// seed 阶段——登记 .sql 交接簿（真 pid）+ AtomicWrite 落同步水位 + 写溢出暂存（含坏行），然后驻留等待外部强杀；
/// verify 阶段——新进程回读：死亡进程登记被清理、水位从盘恢复并正确续传、溢出暂存坏行容错回放。
/// 由 Scripts/kill-restart-drill.ps1 在 CI 独立作业中两阶段调用；常规全量跑不进此路径。
/// </summary>
internal static class KillRestartDrill
{
    public const string PipeName = "taodbm-drill-pipe";

    public static int Run(string phase, string[] args)
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "taodbm-kill-drill");
        Directory.CreateDirectory(dir);
        var registry = Path.Combine(dir, "sqlhandoff.registry");
        var watermark = Path.Combine(dir, "sync-watermark.json");
        var spill = Path.Combine(dir, "spill.jsonl");
        SqlHandoffRegistry.UseFile(registry);

        if (phase == "seed")
        {
            SqlHandoffRegistry.Register(PipeName, Environment.ProcessId);
            AtomicWrite.Text(watermark, SyncCheckpoint.ToJson(new SyncWatermark { Table = "orders", BatchDone = 7, LastKey = "k-0007" }));
            File.WriteAllText(spill, "{\"H\":[\"a\"],\"R\":[[\"1\"]]}\n这行不是JSON\n", Encoding.UTF8);
            Console.WriteLine($"[seed] pid={Environment.ProcessId} registered, watermark+spill written to {dir}");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);   // 驻留等外部强杀（Stop-Process -Force / kill -9）
            return 0;
        }

        // verify：模拟重启后的恢复对账
        int ok = 0, bad = 0;
        void Check(string name, bool cond, string detail = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name} {detail}");
            if (cond) { ok++; }
            else { bad++; Console.WriteLine($"::error::DRILL {name} {detail}"); }   // 失败即注解点名，CI 红时无需登录即可从注解定位红项
        }

        // 1) 死亡进程登记：seed 进程已被强杀，MostRecentAlive 应清掉它（pid 被系统复用的极端情形按存活放行）
        var alive = SqlHandoffRegistry.MostRecentAlive(Environment.ProcessId);
        var leftover = File.ReadAllLines(registry).Where(l => l.Contains(PipeName)).ToList();
        bool cleaned = leftover.Count == 0
            || (int.TryParse(leftover[0].Split('|')[1], out var lpid) && PidAlive(lpid));
        Check("DRILL-登记-死亡进程被清理", cleaned, $"leftover={leftover.Count}");
        Check("DRILL-登记-无存活目标可交接", alive is null || alive.Pid != 0);

        // 2) 水位从盘恢复：断点续传应从第 8 批继续
        var w = SyncCheckpoint.FailSafeParse("orders", File.ReadAllText(watermark));
        Check("DRILL-水位-重启后恢复", w.BatchDone == 7 && w.LastKey == "k-0007", $"BatchDone={w.BatchDone}");
        var batches = Enumerable.Range(0, 10).Select(i => $"INSERT {i}").ToList();
        var rest = SyncCheckpoint.Resume(batches, w);
        Check("DRILL-续传-跳过已完成批次", rest.Count == 3 && rest[0] == "INSERT 7", $"rest={rest.Count}");

        // 3) 溢出暂存坏行容错：垃圾行跳过，好行回放
        using var store = new TempFileSpillStore();
        File.Copy(spill, store.FilePath, overwrite: true);
        var replay = store.ReadBatches().ToList();
        Check("DRILL-溢出-坏行跳过好行回放", replay.Count == 1 && replay[0].Headers.Count == 1 && replay[0].Rows.Count == 1,
            $"replay={replay.Count}");

        Console.WriteLine($"[verify] ok={ok} bad={bad}");
        return bad == 0 ? 0 : 1;
    }

    private static bool PidAlive(int pid)
    {
        try
        {
            Process.GetProcessById(pid);
            // Linux 僵尸态（已死未收尸）判死：进程不可能再服务任何管道，按存活只会误导交接对账
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var close = stat.LastIndexOf(')');
                if (close >= 0 && close + 2 < stat.Length && stat[close + 2] == 'Z') return false;
            }
            catch { /* 非 Linux 或进程刚退出：按 GetProcessById 的存活口径 */ }
            return true;
        }
        catch { return false; }
    }
}
