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
    }

    public partial class App
    {
        public void InitializeComponent() { }
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
