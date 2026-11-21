using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>共享包对话框：局域网搬运的封包与验包预演——条目 JSON 封包出指纹头（SHA256），
/// 粘贴包 JSON 可验包（防篡改/截断），支持分块预演与另存包文件。载荷只读展示。</summary>
public static class SharePacketBuilderDialog
{
    private static SharePacket? _pending;

    public static async Task Show(MainWindow owner, NodeInfo info)
    {
        _pending = null;
        var sender = info.Profile.DisplayName;
        var itemsBox = new TextBox
        {
            PlaceholderText = "粘贴共享条目 JSON（ShareItem 数组，封包用）", AcceptsReturn = true, Height = 100,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var packetBox = new TextBox
        {
            PlaceholderText = "或粘贴共享包 JSON（SharePacket，验包用）", AcceptsReturn = true, Height = 100,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
        };
        var chunkBox = new TextBox { PlaceholderText = "分块大小（字符，默认 4000）", MinWidth = 160 };
        var report = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = AntTokens.FontMinor, Foreground = Ant.TextSub, TextWrapping = TextWrapping.Wrap };
        var buildBtn = new Button { Content = "封包", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var verifyBtn = new Button { Content = "验包", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        var saveBtn = new Button { Content = "另存包文件", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), IsEnabled = false };

        buildBtn.Click += async (_, _) =>
        {
            report.Children.Clear();
            saveBtn.IsEnabled = false;
            var items = TeamShareMerge.FromJson(itemsBox.Text ?? "");
            if (items is null) { status.Text = "共享条目 JSON 无法解析（应为 ShareItem 数组）。"; return; }
            try
            {
                var packet = SharePacketBuilder.Build(items, sender, DateTimeOffset.Now);
                _pending = packet;
                saveBtn.IsEnabled = true;
                ShowHeader(report, packet);
                if (int.TryParse((chunkBox.Text ?? "").Trim(), out var cc) && cc > 0)
                    AddLine(report, $"分块预演：{SharePacketBuilder.Chunk(packet, cc).Count} 块（每块 {cc} 字符，按序重装后整体验签）", null, 1);
                status.Text = "封包完成：指纹头已生成，可另存包文件交同事搬运，或留作验包对照。";
            }
            catch (Exception ex) { status.Text = "封包失败：" + ex.Message; }
            await Task.CompletedTask;
        };

        verifyBtn.Click += async (_, _) =>
        {
            report.Children.Clear();
            saveBtn.IsEnabled = false;
            SharePacket? packet;
            try { packet = System.Text.Json.JsonSerializer.Deserialize<SharePacket>(packetBox.Text ?? ""); }
            catch (System.Text.Json.JsonException) { status.Text = "共享包 JSON 无法解析，请核对格式。"; return; }
            if (packet is null) { status.Text = "共享包为空。"; return; }
            var check = SharePacketBuilder.Verify(packet);
            ShowHeader(report, packet);
            if (check.Ok)
            {
                AddLine(report, $"验包通过：{check.Reason}；条目可安全合并（收到后走「团队合并」预演）", Ant.Primary, 1);
            }
            else
            {
                AddLine(report, $"验包未过：{check.Reason}", Ant.Error, 1);
                AddLine(report, "拒收：载荷被改或被截，请让发送方重出包。", Ant.Error, 1);
            }
            status.Text = "验包完成：结果如上。";
            await Task.CompletedTask;
        };

        saveBtn.Click += async (_, _) =>
        {
            if (_pending is null) { status.Text = "请先封包。"; return; }
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_pending, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaoDBM", "share", $"share-packet-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await System.IO.File.WriteAllTextAsync(path, json);
                status.Text = $"已保存：{path}";
            }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
            await Task.CompletedTask;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(buildBtn); row1.Children.Add(verifyBtn); row1.Children.Add(chunkBox); row1.Children.Add(saveBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = $"局域网搬运（{sender}）：封包生成 SHA256 指纹头，接收方重算指纹防篡改防截断；大包可分块过线。凭据已在「团队合并」净化阶段剔除。载荷只读展示。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(itemsBox);
        panel.Children.Add(packetBox);
        panel.Children.Add(row1);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        var dlg = new ContentDialog
        {
            Title = "共享包",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = owner.Content.XamlRoot,
        };
        _ = dlg.ShowThemedAsync();
        await Task.CompletedTask;
    }

    private static void ShowHeader(StackPanel report, SharePacket packet)
    {
        AddSection(report, $"包 {packet.Header.PacketId}");
        AddLine(report, $"发送方 {packet.Header.Sender}；{packet.Header.CreatedAt:yyyy-MM-dd HH:mm}；条目 {packet.Header.ItemCount}；载荷 {packet.Header.PayloadBytes} 字节", null, 1);
        AddLine(report, $"指纹 {packet.Header.Sha256}", null, 1);
        var preview = packet.Payload.Length > 200 ? packet.Payload[..200] + " …" : packet.Payload;
        AddLine(report, $"载荷预览：{preview}", null, 1);
    }

    private static void AddLine(StackPanel p, string text, Brush? brush = null, int indent = 0)
    {
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = AntTokens.FontMinor, Margin = new Thickness(indent * 16, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Ant.TextSub,
            FontFamily = new FontFamily("Consolas"),
        });
    }

    private static void AddSection(StackPanel p, string title)
    {
        p.Children.Add(new TextBlock
        {
            Text = title, FontSize = AntTokens.FontBody, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ant.Primary, Margin = new Thickness(0, 6, 0, 0),
        });
    }
}
