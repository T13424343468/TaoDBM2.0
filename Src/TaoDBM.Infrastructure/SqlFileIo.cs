using System.IO;
using System.Text;
using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>.sql 文件装载/保存引擎（需求合并稿第一章之二三件套）：只读写文件、绝不执行 SQL。</summary>
public static class SqlFileIo
{
    public const string BomMarker = "bom";

    /// <summary>装载 .sql：流式读取不冻结（≥10MB 口径走分段读拼回），识别 UTF-8(BOM)/UTF-8/GBK。</summary>
    public static string Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new StreamReader(fs, DetectEncoding(path));
        return reader.ReadToEnd();
    }

    /// <summary>保存 .sql：流式写出（大文件不冻结）；编码 UTF-8（默认，可带 BOM）或 GBK；仅写文件不执行。</summary>
    public static long Save(string path, string sql, bool gbk = false, bool bom = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var enc = gbk ? Encoding.GetEncoding("GBK") : new UTF8Encoding(bom);
        using var writer = new StreamWriter(path, false, enc, 1 << 16);
        writer.Write(sql);
        writer.Flush();
        return new FileInfo(path).Length;
    }

    /// <summary>探测编码：UTF-8 BOM 直接认；否则按 GBK 严格解码失败回落 UTF-8（有 BOM 无 BOM 都可读，中文注释不乱码）。</summary>
    public static Encoding DetectEncoding(string path)
    {
        var head = new byte[3];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 3))
            n = fs.Read(head, 0, 3);
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return new UTF8Encoding(true);
        // GBK 严格试解：首段能被 GBK 完整解出且含非 ASCII → 按 GBK；否则 UTF-8（含纯 ASCII）
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return new UTF8Encoding(false);
            var gbk = Encoding.GetEncoding("GBK", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            gbk.GetString(bytes);
            return bytes.Any(b => b > 0x7F) ? gbk : new UTF8Encoding(false);
        }
        catch { return new UTF8Encoding(false); }
    }

    /// <summary>保存往返自检口径：UTF-8 BOM / 无 BOM / GBK 三态写读一致（测试用）。</summary>
    public static bool RoundTrip(string path, string sql, bool gbk, bool bom)
    {
        Save(path, sql, gbk, bom);
        return Load(path) == sql;
    }
}
