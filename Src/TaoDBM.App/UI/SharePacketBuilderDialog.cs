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

    public static FrameworkElement BuildPanel(MainWindow owner, NodeInfo info)
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

        // 整改三批三-13（2026-11-24 老板令「抓紧时间」）：演示式接真实数据——共享条目从当前连接档案生成，
        // 载荷只含连接元信息（引擎/主机/端口/库/用户名），密码与 DPAPI 密文不入包，再过一遍净化黑名单兜底。
        var genBtn = new Button { Content = "从当前连接生成条目", CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd) };
        genBtn.Click += async (_, _) => await RunRealItemsAsync(info, itemsBox, report, status);

        buildBtn.Click += async (_, _) =>
        {
            report.Children.Clear();
            saveBtn.IsEnabled = false;
            var items = TeamShareMerge.FromJson(itemsBox.Text ?? "");
            if (items is null) { status.Text = "共享条目 JSON 无法解析（应为 ShareItem 数组）。"; return; }
            try
            {
                // 发现107：封包前逐条净化——不再只依赖「团队合并」阶段，粘贴路径同样过黑名单兜底。
                var droppedAll = new List<string>();
                var cleanItems = new List<ShareItem>(items.Count);
                foreach (var it in items)
                {
                    var (clean, dropped) = TeamShareMerge.Sanitize(it);
                    cleanItems.Add(clean);
                    droppedAll.AddRange(dropped);
                }
                if (droppedAll.Count > 0)
                    AppLog.Write($"[共享包] build sanitize dropped={string.Join(",", droppedAll.Distinct())}");   // 发现107 留痕
                var packet = SharePacketBuilder.Build(cleanItems, sender, DateTimeOffset.Now);
                _pending = packet;
                saveBtn.IsEnabled = true;
                ShowHeader(report, packet);
                if (droppedAll.Count > 0)
                    AddLine(report, $"⚠ 载荷命中凭据黑名单并已剔除：{string.Join(", ", droppedAll.Distinct())}", Ant.Warn, 1);
                if (int.TryParse((chunkBox.Text ?? "").Trim(), out var cc) && cc > 0)
                    AddLine(report, $"分块预演：{SharePacketBuilder.Chunk(packet, cc).Count} 块（每块 {cc} 字符，按序重装后整体验签）", null, 1);
                status.Text = "封包完成：指纹头已生成，可另存包文件交同事搬运，或留作验包对照。";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "封包失败"); }
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
                var path = TaoPaths.FilePathIn("share", $"share-packet-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                await System.IO.File.WriteAllTextAsync(path, json);
                status.Text = $"已保存：{path}";
            }
            catch (Exception ex) { status.Text = HumanError.Say(ex, "保存失败"); }
            await Task.CompletedTask;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(buildBtn); row1.Children.Add(verifyBtn); row1.Children.Add(chunkBox); row1.Children.Add(saveBtn); row1.Children.Add(genBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 680 };
        // 407 原型：面板标题行
        panel.Children.Add(new TextBlock { Text = "分享包构建", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = AntTokens.FontBody });
        panel.Children.Add(new TextBlock
        {
            Text = $"局域网搬运（{sender}）：封包生成 SHA256 指纹头，接收方重算指纹防篡改防截断；大包可分块过线。封包前逐条过凭据黑名单，命中即剔除并留痕。载荷只读展示。",
            TextWrapping = TextWrapping.Wrap, Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor,
        });
        panel.Children.Add(itemsBox);
        panel.Children.Add(packetBox);
        panel.Children.Add(row1);
        panel.Children.Add(new ScrollViewer { Content = report, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(status);

        return panel;
    }

    /// <summary>三-13：真实条目生成——从当前连接档案出一条 Kind=connection 的共享条目回填条目框。
    /// 载荷只含连接元信息，密码与 DPAPI 密文不入包；生成后再过净化黑名单兜底。</summary>
    private static async Task RunRealItemsAsync(NodeInfo info, TextBox itemsBox, StackPanel report, TextBlock status)
    {
        report.Children.Clear();
        try
        {
            var p = info.Profile;
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                displayName = p.DisplayName,
                engine = p.Engine.ToString(),
                host = p.Host,
                port = p.Port,
                database = p.Database,
                user = p.User,
            });
            var item = new ShareItem(
                $"conn-{p.Id}", "connection", payload, DateTimeOffset.Now,
                string.IsNullOrWhiteSpace(Environment.UserName) ? "taodbm" : Environment.UserName);
            var (clean, dropped) = TeamShareMerge.Sanitize(item);
            if (dropped.Count > 0)
                AddLine(report, $"⚠ 载荷命中凭据黑名单并已剔除：{string.Join(", ", dropped)}", Ant.Warn, 1);
            itemsBox.Text = System.Text.Json.JsonSerializer.Serialize(
                new[] { clean }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            AddSection(report, $"真实条目：连接「{p.DisplayName}」");
            AddLine(report, "  条目已回填：载荷仅连接元信息（无凭据），点「封包」出指纹头。");
            status.Text = "条目生成完成。";
            AppLog.Write($"[共享条目] conn={p.DisplayName} dropped={dropped.Count}");
        }
        catch (Exception ex)
        {
            status.Text = HumanError.Say(ex, "条目生成失败");
            AppLog.Write($"[共享条目] error={ex.Message}");
        }
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
