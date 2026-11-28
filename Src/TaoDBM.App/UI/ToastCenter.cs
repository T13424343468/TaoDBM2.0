using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TaoDBM.Core;

namespace TaoDBM.UI;

/// <summary>
/// P4 系统推送级：Toast 通知中心。生产方 Publish(NotifyEvent)，内部先经 NotificationDispatcher
/// 规则判定（总开关/单类开关/免打扰/节流），够格才弹系统级 Toast；点击通知按 target 路由跳功能入口。
/// 内容只在本地生成、本地展示，不外发。
/// 需真机验证：弹窗样式、点击跳转、通知权限被关后的表现——沙箱无法真机验证，激活路由以日志留痕为准。
/// </summary>
public static class ToastCenter
{
    private static readonly NotificationDispatcher Dispatcher = new();
    private static bool _registered;

    /// <summary>通知点击路由事件（MainWindow 订阅；NotificationInvoked 在后台线程派发，订阅方负责切 UI 线程）。</summary>
    public static event Action<string>? RouteRequested;

    /// <summary>注册系统通知（官方流程：先挂 NotificationInvoked 再 Register，且必须早于任何 Show）。
    /// 未打包应用由 Windows App SDK 处理应用身份；重复注册安全（内部幂等）。</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                AppLog.Write("Toast: 当前系统不支持通知，P4 功能降级为应用内提示");
                return;
            }
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
            AppLog.Write("Toast: 系统通知已注册");
        }
        catch (Exception ex)
        {
            // 注册失败绝不拖垮启动：应用内提示照旧
            AppLog.Write($"Toast: 注册失败 {ex.Message}");
        }
    }

    /// <summary>事件发布口：先落应用内收件箱（2026-11-28 拍板：应用内为主，系统通知默认关），
    /// 再按用户配置判定够不够格弹系统级 Toast。任何异常只进日志，不影响业务流程。</summary>
    public static void Publish(NotifyEvent evt)
    {
        try
        {
            // 应用内收件箱：所有事件先落箱，打开程序即可见（不依赖系统通知开关）
            InboxStore.Append(evt);
            Dispatcher.Options = NotifyPrefs.Load();
            var decision = Dispatcher.Evaluate(evt);
            AppLog.Write($"Toast: 事件 {evt.Kind}/{evt.Key} 判定 {decision.Reason}");
            if (!decision.Notify) return;
            if (!_registered)
            {
                AppLog.Write($"Toast: 未注册，事件仅入日志：{evt.Title}");
                return;
            }
            var setting = AppNotificationManager.Default.Setting;
            if (setting != AppNotificationSetting.Enabled)
            {
                AppLog.Write($"Toast: 系统通知权限受限（{setting}），事件仅入日志：{evt.Title}（需真机验证权限引导路径）");
                return;
            }
            var notification = new AppNotificationBuilder()
                .AddText(evt.Title)
                .AddText(evt.Message)
                .AddArgument("target", Route(evt.Kind))
                .SetTag(NotificationDispatcher.ThrottleKey(evt))
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            AppLog.Write($"Toast: 已弹系统通知 {evt.Title}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Toast: 发布失败 {ex.Message}");
        }
    }

    /// <summary>事件种类 → 功能入口路由键（MainWindow 按此跳转；未识别的种类回落主窗）。</summary>
    public static string Route(NotifyKind kind) => kind switch
    {
        NotifyKind.RiskySqlDone => "risky",
        NotifyKind.Backup => "backup",
        NotifyKind.Restore => "backup",   // 还原与备份共用同一入口
        NotifyKind.Migration => "migration",
        NotifyKind.LongTask => "taskcenter",
        NotifyKind.HealthCheck => "health",
        _ => "",
    };

    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var target = args.Arguments.TryGetValue("target", out var v) ? v : "";
        AppLog.Write($"Toast: 通知被点击 target={target}");
        RouteRequested?.Invoke(target);
    }
}
