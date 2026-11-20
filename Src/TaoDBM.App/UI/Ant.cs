using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>
/// 主题取色入口：画刷实例全程唯一，切主题时只改 Color ——
/// XAML ThemeResource 与代码引用同刻生效，全窗口无需重启即时换装。
/// 色值来自 Core/ThemePalette（水墨默认，Ant 蓝可切回）。
/// </summary>
public static class Ant
{
    public static Windows.UI.Color Color(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return new Windows.UI.Color
        {
            A = Convert.ToByte(hex[..2], 16),
            R = Convert.ToByte(hex.Substring(2, 2), 16),
            G = Convert.ToByte(hex.Substring(4, 2), 16),
            B = Convert.ToByte(hex.Substring(6, 2), 16),
        };
    }

    public static SolidColorBrush Brush(string hex) => new(Color(hex));

    public static readonly SolidColorBrush Primary = Brush("#1677FF");
    public static readonly SolidColorBrush PrimaryDark = Brush("#0958D9");
    public static readonly SolidColorBrush Bg = Brush("#F5F6F8");
    public static readonly SolidColorBrush Card = Brush("#FFFFFF");
    public static readonly SolidColorBrush Border = Brush("#E5E7EB");
    public static readonly SolidColorBrush Text = Brush("#1F2329");
    public static readonly SolidColorBrush TextSub = Brush("#6B7280");
    public static readonly SolidColorBrush Success = Brush("#52C41A");
    public static readonly SolidColorBrush Warn = Brush("#FAAD14");
    public static readonly SolidColorBrush Error = Brush("#FF4D4F");
    public static readonly SolidColorBrush HeadBg = Brush("#F0F5FF");
    public static readonly SolidColorBrush AltRow = Brush("#FAFBFC");
    /// <summary>语义浅底色（设计稿分级确认浅底）：错误浅底/主色浅底/警示浅底/成功浅底。</summary>
    public static readonly SolidColorBrush ErrorBg = Brush("#FFF1F0");
    public static readonly SolidColorBrush PrimaryBg = Brush("#E6F4FF");
    public static readonly SolidColorBrush WarnBg = Brush("#FFF7E8");
    public static readonly SolidColorBrush SuccessBg = Brush("#F6FFED");
    /// <summary>主色底上的文字（按钮字）：水墨/天青白字、墨夜近黑字（WCAG 按钮字 4.0 口径）。</summary>
    public static readonly SolidColorBrush OnPrimary = Brush("#FFFFFF");

    private static bool _installed;

    /// <summary>换肤后广播配色（标题栏等非客户区随象而染，订阅方即时重钉）。</summary>
    public static event Action<IReadOnlyDictionary<string, string>>? ThemeApplied;

    /// <summary>系统明暗象：水墨 / 天青走 Light，墨夜走 Dark。
    /// ContentDialog、MenuFlyout、ComboBox 下拉、TextBox 等系统控件按象取肤，
    /// 由窗口根元素重钉（FrameworkElement.RequestedTheme），子窗体与弹层全部随象而染。</summary>
    public static ElementTheme ElementTheme { get; private set; } = ElementTheme.Light;

    /// <summary>把画刷实例挂进应用资源（ThemeResource 按键可取）；重复调用安全。
    /// 系统强调色族一并挂到会变色的画刷上，弹窗按钮等系统控件随主题走（A2 教训）。</summary>
    public static void InstallResources()
    {
        var res = Application.Current.Resources;
        res["AntPrimary"] = Primary;
        res["AntPrimaryDark"] = PrimaryDark;
        res["AntBg"] = Bg;
        res["AntCard"] = Card;
        res["AntBorder"] = Border;
        res["AntText"] = Text;
        res["AntTextSub"] = TextSub;
        res["AntSuccess"] = Success;
        res["AntWarn"] = Warn;
        res["AntError"] = Error;
        res["AntHeadBg"] = HeadBg;
        res["AntAltRow"] = AltRow;
        res["AntOnPrimary"] = OnPrimary;
        res["SystemAccentColor"] = Primary.Color;
        res["SystemAccentColorLight1"] = Primary.Color;
        res["SystemAccentColorLight2"] = Primary.Color;
        res["SystemAccentColorLight3"] = Primary.Color;
        res["SystemAccentColorDark1"] = PrimaryDark.Color;
        res["SystemAccentColorDark2"] = PrimaryDark.Color;
        res["SystemAccentColorDark3"] = PrimaryDark.Color;
        res["SystemControlHighlightAccentBrush"] = Primary;
        res["SystemControlForegroundAccentBrush"] = Primary;
        res["SystemControlBackgroundAccentBrush"] = Primary;
        res["ToggleSwitchFillOn"] = Primary;
        _installed = true;
    }

    /// <summary>应用指定主题（未知名字回落水墨）；未 InstallResources 时先安装。</summary>
    public static void ApplyTheme(string? name)
    {
        if (!_installed) InstallResources();
        var pal = ThemePalette.Get(name);
        Primary.Color = Color(pal["AntPrimary"]);
        PrimaryDark.Color = Color(pal["AntPrimaryDark"]);
        Bg.Color = Color(pal["AntBg"]);
        Card.Color = Color(pal["AntCard"]);
        Border.Color = Color(pal["AntBorder"]);
        Text.Color = Color(pal["AntText"]);
        TextSub.Color = Color(pal["AntTextSub"]);
        Success.Color = Color(pal["AntSuccess"]);
        Warn.Color = Color(pal["AntWarn"]);
        Error.Color = Color(pal["AntError"]);
        HeadBg.Color = Color(pal["AntHeadBg"]);
        AltRow.Color = Color(pal["AntAltRow"]);
        ApplyCeramicCard(pal);
        OnPrimary.Color = ThemePalette.Normalize(name) == "night" ? Color("#131211") : Color("#FFFFFF");
        ElementTheme = ThemePalette.Normalize(name) == "night" ? ElementTheme.Dark : ElementTheme.Light;
        Application.Current.Resources["SystemAccentColor"] = Primary.Color;
        ThemeApplied?.Invoke(pal);
    }

    /// <summary>陶瓷质感（2026-11-06 用户定稿，对齐 TaoAgent）：卡片面换纵向釉面微渐变——
    /// 上亮(+15/通道)-主体(0.55 处)-下暗(-12/通道)，三套主题统一生效。
    /// 只换 XAML 资源项，代码单例画刷仍为主体纯色；任一异常退回纯色，不阻断换肤。</summary>
    private static void ApplyCeramicCard(IReadOnlyDictionary<string, string> pal)
    {
        try
        {
            var main = Color(pal["AntCard"]);
            byte Up(int v) => (byte)Math.Min(255, v + 15);
            byte Down(int v) => (byte)Math.Max(0, v - 12);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, Up(main.R), Up(main.G), Up(main.B)), Offset = 0.0 },
                    new GradientStop { Color = main, Offset = 0.55 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, Down(main.R), Down(main.G), Down(main.B)), Offset = 1.0 },
                }
            };
            Application.Current.Resources["AntCard"] = brush;
        }
        catch
        {
            Application.Current.Resources["AntCard"] = Card;
        }
    }
}

/// <summary>视觉尺规令牌（需求合并稿第十五章，2026-11-19 23:50 老板令）：
/// 字号六档 / 间距五档 / 圆角三档，界面落码一律取令牌、不再手写零散值。</summary>
public static class AntTokens
{
    /// <summary>字号六档（px 对应 XAML 像素）：说明文字/次要/正文/强调/标题/页面标题。</summary>
    public const double FontCaption = 11;
    public const double FontMinor = 12;
    public const double FontBody = 13;
    public const double FontEmphasis = 14;
    public const double FontTitle = 16;
    public const double FontPage = 20;

    /// <summary>间距五档：紧凑/默认/宽松/区块/页面留白。</summary>
    public const double SpaceXs = 4;
    public const double SpaceSm = 8;
    public const double SpaceMd = 12;
    public const double SpaceLg = 16;
    public const double SpaceXl = 24;

    /// <summary>圆角三档：直角感/默认/卡片。</summary>
    public const double RadiusSm = 2;
    public const double RadiusMd = 4;
    public const double RadiusLg = 6;

    /// <summary>胶囊圆角（徽标/状态点）：设计稿 99px 胶囊口径。</summary>
    public const double RadiusPill = 99;

    public static CornerRadius CornerRadius(double token) => new(token);
}

/// <summary>内容弹窗扩展：弹层挂在窗口内容之外，继承不到根元素明暗象，须在显示前显式钉定。</summary>
public static class ContentDialogExt
{
    public static async Task<ContentDialogResult> ShowThemedAsync(this ContentDialog dlg)
    {
        dlg.RequestedTheme = Ant.ElementTheme;
        return await dlg.ShowAsync();
    }
}

/// <summary>弹层（MenuFlyout 等）主题钉定：弹层挂窗口内容之外，继承不到根钉。
/// 每次打开前把弹出层样式里的明暗象重钉到当前象，换肤后再开也随象。</summary>
public static class FlyoutExt
{
    public static void PinTheme(this FlyoutBase fly)
    {
        fly.Opening += (_, _) =>
        {
            // #88 S11 红场：本回调由 WinRT 原生层直接调用，内部抛错会成为 stowed exception（0xC000027B）带崩整个进程——
            // 钉主题失败只落账不外溢；另留 opening 落账供崩溃定位（崩溃发生在 Click 之前时靠它分辨段位）。
            Core.AppLog.Write("[Flyout] opening");
            try
            {
                if (fly is MenuFlyout mf)
                {
                    mf.MenuFlyoutPresenterStyle = new Style
                    {
                        TargetType = typeof(MenuFlyoutPresenter),
                        Setters = { new Setter(FrameworkElement.RequestedThemeProperty, Ant.ElementTheme) }
                    };
                }
            }
            catch (Exception ex) { Core.AppLog.Write($"[Flyout] 钉主题失败: {ex.Message}"); }
        };
    }
}
