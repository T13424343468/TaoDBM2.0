using System.IO;

namespace TaoDBM.Core;

/// <summary>
/// 配置/账本落盘原子替换（tao-code-standard 三.6 全线家法，2026-11-14 收敛）：
/// 一律先写 path.tmp 并 flush 到磁盘，再 File.Move(overwrite) 原子替换目标文件。
/// 断电/崩溃时目标文件要么是完整旧版要么是完整新版，绝不整本清零、绝不留半截文件。
/// </summary>
public static class AtomicWrite
{
    public static void Text(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs) { AutoFlush = false })
        {
            w.Write(content);
            w.Flush();
            fs.Flush(true);   // 强制落盘：断电后 tmp 要么完整要么不存在，move 后目标必完整
        }
        // Windows 现实环境（杀软实时扫描/搜索索引）可能短暂持有刚关闭的 tmp 句柄，Move 立刻被拒
        // （UnauthorizedAccessException/IOException）。有界退避重试后放行，绝不吞异常、绝不放弃原子性。
        // 实锤：GH Actions windows-latest Defender 锁 tmp，run #63 N38 后秒挂，2026-11-18。
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(attempt * 25);   // 最坏约 1.1s：杀软扫描窗口通常 <100ms
            }
        }
    }
}
