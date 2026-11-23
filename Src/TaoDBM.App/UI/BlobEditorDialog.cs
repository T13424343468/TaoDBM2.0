using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>批次二格2：大字段编辑器——BLOB/TEXT/超长值三页签：多行文本（UTF-8）/ 十六进制（Hex+ASCII 双列，
/// 自写格式化）/ 图像预览（PNG/JPG/BMP 头魔数识别）。保存回写文本（Hex 模式回写字节串的十六进制文本），
/// 提交仍走 EditRow 既有 Literal 口径。零包自写，图像用系统 BitmapImage 直读。</summary>
public static class BlobEditorDialog
{
    /// <summary>图像魔数：PNG/JPG/BMP。</summary>
    public static bool IsImageBytes(byte[] b) => b.Length >= 8 && (
        (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||   // PNG
        (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                    // JPG
        (b[0] == 0x42 && b[1] == 0x4D));                                     // BMP

    /// <summary>Hex + ASCII 双列格式化（每行 16 字节）。</summary>
    public static string ToHexDump(byte[] b)
    {
        var sb = new System.Text.StringBuilder();
        for (int off = 0; off < b.Length; off += 16)
        {
            var line = new System.Text.StringBuilder($"{off:X8}  ");
            var ascii = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                if (off + i < b.Length)
                {
                    line.Append($"{b[off + i]:X2} ");
                    ascii.Append(b[off + i] >= 0x20 && b[off + i] < 0x7F ? (char)b[off + i] : '.');
                }
                else line.Append("   ");
                if (i == 7) line.Append(' ');
            }
            sb.Append(line).Append(" ").Append(ascii).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Hex 文本回解析为字节（容忍空格/冒号/0x 前缀；非法字符返回 null）。</summary>
    public static byte[]? FromHex(string text)
    {
        var t = text.Replace("0x", "").Replace(":", "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if (t.Length % 2 != 0) return null;
        var bytes = new byte[t.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(t.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        return bytes;
    }

    public static async Task<string?> Show(MainWindow owner, ColumnSpec col, string initialText)
    {
        byte[]? initialBytes = null;
        try { initialBytes = initialText.Length > 0 ? System.Text.Encoding.UTF8.GetBytes(initialText) : Array.Empty<byte>(); } catch { }

        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 260, Text = initialText, IsReadOnly = true };
        var hex = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), MinHeight = 260, Text = initialBytes is { Length: > 0 } ? ToHexDump(initialBytes) : "（空值）" };
        var img = new StackPanel { Spacing = 6, MinHeight = 260 };
        string mode = "text";

        var tabs = new Pivot { MinWidth = 560 };
        var pText = new PivotItem { Header = "文本", Content = text };
        var pHex = new PivotItem { Header = "十六进制", Content = hex };
        var pImg = new PivotItem { Header = "图像", Content = img };
        tabs.Items.Add(pText); tabs.Items.Add(pHex); tabs.Items.Add(pImg);

        if (initialBytes is { Length: > 0 } && IsImageBytes(initialBytes))
        {
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"taodbm-blob-{Guid.NewGuid():N}.bin");
                await File.WriteAllBytesAsync(tmp, initialBytes);
                img.Children.Add(new Image { Source = new BitmapImage(new Uri(tmp)), MaxHeight = 320, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
                img.Children.Add(new TextBlock { Text = $"图像 {initialBytes.Length:N0} 字节（临时文件 {Path.GetFileName(tmp)}）", FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub });
            }
            catch (Exception ex)
            {
                img.Children.Add(new TextBlock { Text = $"图像预览失败：{ex.Message}", Foreground = Ant.Error, TextWrapping = TextWrapping.Wrap });
            }
        }
        else img.Children.Add(new TextBlock { Text = "该值不是 PNG/JPG/BMP 图像（魔数不符），无法预览。", Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap });

        var dlg = new ContentDialog
        {
            Title = $"大字段 · {col.Name}（{col.DataType}）",
            Content = new StackPanel { Spacing = 8, Children = { tabs } },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = owner.Content.XamlRoot,
        };
        tabs.SelectionChanged += (_, _) => mode = tabs.SelectedIndex switch { 1 => "hex", 2 => "img", _ => "text" };
        AppLog.Write($"[大字段] 列={col.Name} 切换={mode}");
        var r = await dlg.ShowThemedAsync();
        if (r != ContentDialogResult.Primary) return null;
        // Hex 页签保存时回写十六进制文本（与冻结稿口径一致）；文本页签原样返回
        return mode == "hex" && initialBytes is { Length: > 0 }
            ? Convert.ToHexString(initialBytes)
            : text.Text;
    }
}
