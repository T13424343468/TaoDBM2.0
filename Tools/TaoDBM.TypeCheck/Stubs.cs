// 类型检查桩：只补 XAML 代码生成的字段与方法体，真实 WinUI API 来自引用的投影程序集。
namespace TaoDBM
{
    public sealed partial class MainWindow
    {
        public void InitializeComponent() { }

        // MainWindow.xaml 中 x:Name 的字段（真实工程由 XAML 编译器生成）
        internal Microsoft.UI.Xaml.Controls.Image LogoImg = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnNewConn = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnCenter = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnAbout = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnLogo = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnTheme = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnRefresh = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnQuery = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnData = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnDesign = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnBackup = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnSync = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnErd = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnEdit = null!;
        internal Microsoft.UI.Xaml.Controls.Button BtnDel = null!;
        internal Microsoft.UI.Xaml.Controls.TreeView ConnTree = null!;
        internal Microsoft.UI.Xaml.Controls.TabView WorkTabs = null!;
        internal Microsoft.UI.Xaml.Controls.TextBlock AppTitleText = null!;
        internal Microsoft.UI.Xaml.Controls.TextBlock StatusText = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem ThemeInk = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem ThemeSky = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem ThemeNight = null!;
        internal Microsoft.UI.Xaml.Controls.ColumnDefinition TreeCol = null!;
        internal Microsoft.UI.Xaml.Controls.ColumnDefinition MainCol = null!;
        internal Microsoft.UI.Xaml.Controls.Border TreeBorder = null!;
        internal Microsoft.UI.Xaml.Controls.GridSplitter TreeSplitter = null!;
        internal Microsoft.UI.Xaml.Controls.InfoBar MetaRetryBar = null!;
    }

    public partial class App
    {
        public void InitializeComponent() { }
    }
}

// 桩 WinUI.dll 与投影程序集中均不含 GridSplitter 类型，按真实 WinUI 元数据补齐供编译校验。
namespace Microsoft.UI.Xaml.Controls
{
    public class GridSplitter : Control
    {
        public GridSplitter() { }
        public GridResizeBehavior ResizeBehavior { get; set; }
        public GridResizeDirection ResizeDirection { get; set; }
        public GridSplitterIncrementMode IncrementControl { get; set; }
        public double KeyboardIncrement { get; set; }
    }

    public enum GridResizeBehavior { BasedOnAlignment, CurrentAndNext, PreviousAndCurrent, PreviousAndNext }
    public enum GridResizeDirection { Auto, Columns, Rows }
    public enum GridSplitterIncrementMode { Pixel, Uniform }
}

// .sql 三件套：Windows App SDK 的 Microsoft.Windows.AppLifecycle 投影不在这批桩程序集里，
// 按真实元数据（1.x AppInstance API）补齐，仅供单实例重定向的纯编译校验；真机行为以真机为准。
namespace Microsoft.Windows.AppLifecycle
{
    public sealed class AppActivationArguments
    {
        public Microsoft.Windows.AppLifecycle.AppActivationKind Kind => Microsoft.Windows.AppLifecycle.AppActivationKind.Launch;
        public object? Data => null;
    }

    public enum AppActivationKind { Launch, File, Protocol, CommandLine }

    public sealed class AppInstance
    {
        private AppInstance() { }
        public static Microsoft.Windows.AppLifecycle.AppInstance FindOrRegisterForKey(string key) =>
            throw new System.NotSupportedException();
        public static Microsoft.Windows.AppLifecycle.AppActivationArguments GetActivatedEventArgs() =>
            throw new System.NotSupportedException();
        public bool IsRunning => false;
        public global::Windows.Foundation.IAsyncAction RedirectActivationToAsync(Microsoft.Windows.AppLifecycle.AppActivationArguments args) =>
            throw new System.NotSupportedException();
        // 桩事件：只供订阅方编译（CS0067 以空 add/remove 压制）
        public event global::Windows.Foundation.TypedEventHandler<Microsoft.Windows.AppLifecycle.AppInstance, Microsoft.Windows.AppLifecycle.AppActivationArguments>? Activated
        {
            add { }
            remove { }
        }
    }
}

// P4 系统推送级：Windows App SDK 的 Microsoft.Windows.AppNotifications(+Builder) 投影不在这批桩程序集里，
// 按真实元数据（2.6.5 包反射核实的签名）补齐，仅供 ToastCenter.cs 纯编译校验；真机行为以真机为准。
namespace Microsoft.Windows.AppNotifications
{
    public enum AppNotificationSetting
    {
        Enabled, DisabledForApplication, DisabledForUser, DisabledByGroupPolicy, DisabledByManifest, Unsupported,
    }

    public sealed class AppNotificationActivatedEventArgs
    {
        public System.Collections.Generic.IDictionary<string, string> Arguments { get { throw new System.NotSupportedException(); } }
        public System.Collections.Generic.IDictionary<string, string> UserInput { get { throw new System.NotSupportedException(); } }
        public string Argument { get { throw new System.NotSupportedException(); } }
    }

    public sealed class AppNotification
    {
        public AppNotification(string payload) { Payload = payload; }
        public string Payload { get; }
        public string? Tag { get; set; }
        public string? Group { get; set; }
        public bool ExpiresOnReboot { get; set; }
        public AppNotificationPriority Priority { get; set; }
    }

    public enum AppNotificationPriority { Default, High }

    public sealed class AppNotificationManager
    {
        private AppNotificationManager() { }
        public static AppNotificationManager Default { get { throw new System.NotSupportedException(); } }
        public static bool IsSupported() => true;
        public void Register() { }
        public void Unregister() { }
        public void Show(AppNotification notification) { }
        public AppNotificationSetting Setting => AppNotificationSetting.Enabled;
        // 桩事件：只供订阅方编译（真实实现由 Windows App SDK 投影承载，CS0067 以空 add/remove 压制）
        public event global::Windows.Foundation.TypedEventHandler<AppNotificationManager, AppNotificationActivatedEventArgs>? NotificationInvoked
        {
            add { }
            remove { }
        }
    }
}

namespace Microsoft.Windows.AppNotifications.Builder
{
    public sealed class AppNotificationBuilder
    {
        public AppNotificationBuilder AddText(string text) => this;
        public AppNotificationBuilder AddArgument(string key, string value) => this;
        public AppNotificationBuilder SetTag(string tag) => this;
        public Microsoft.Windows.AppNotifications.AppNotification BuildNotification() =>
            new Microsoft.Windows.AppNotifications.AppNotification("");
    }
}
