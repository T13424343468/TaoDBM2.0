using System;

namespace TaoDBM.Core;

/// <summary>
/// 统一错误文案层（㊴）：用户可见文案说人话——发生了什么＋怎么办；
/// 原始异常全文只进 AppLog 不上屏。映射按模块归类：锁/超时/连接/登录/语法/磁盘/取消。
/// </summary>
public static class HumanError
{
    /// <summary>把异常转成用户可读文案；原始异常落 AppLog（带动作上下文）。本方法自身绝不抛。</summary>
    public static string Say(Exception ex, string action)
    {
        try
        {
            AppLog.Write($"[人话文案] {action}｜原始异常 {ex.GetType().Name}: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            var (what, how) = Describe(ex);
            var act = NormalizeAction(action);
            return $"{act}没成功：{what}。{how}";
        }
        catch { return $"{action}失败，详情见日志。"; }
    }

    /// <summary>动作名归一：去掉「失败/出错/错误」尾巴，避免「回滚失败没成功」。</summary>
    private static string NormalizeAction(string action)
    {
        var a = action?.Trim() ?? "操作";
        foreach (var tail in new[] { "失败", "出错", "错误" })
        {
            if (a.EndsWith(tail, StringComparison.Ordinal)) return a[..^tail.Length];
        }
        return a.Length == 0 ? "操作" : a;
    }

    /// <summary>按异常类型与消息关键词映射：what=发生了什么，how=怎么办。</summary>
    private static (string What, string How) Describe(Exception ex)
    {
        var m = ex.Message ?? "";
        // 锁：死锁/锁等待
        if (m.Contains("Deadlock", StringComparison.OrdinalIgnoreCase) || m.Contains("deadlock", StringComparison.OrdinalIgnoreCase) || m.Contains("锁等待") || m.Contains("Lock wait"))
            return ("这条事务和别的连接抢锁卡住了", "建议稍等几秒重试；反复出现可在「诊断」里看锁等待");
        // 超时
        if (m.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || m.Contains("timed out", StringComparison.OrdinalIgnoreCase) || m.Contains("超时"))
            return ("等数据库回应超时了（网络慢或语句太重）", "建议检查网络后重试；语句太重可先加条件缩小范围");
        // 登录/权限
        if (m.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || m.Contains("authentication", StringComparison.OrdinalIgnoreCase) || m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("permission", StringComparison.OrdinalIgnoreCase) || m.Contains("权限") || m.Contains("登录"))
            return ("登录被拒（账号、密码或权限不对）", "建议核对账号密码与该库的授权");
        // 连接
        if (m.Contains("connect", StringComparison.OrdinalIgnoreCase) || m.Contains("host", StringComparison.OrdinalIgnoreCase) || m.Contains("网络") || m.Contains("Connection", StringComparison.OrdinalIgnoreCase))
            return ("连不上数据库（地址不通或服务没起）", "建议确认地址、端口与服务状态，再点「测试连接」");
        // 语法
        if (m.Contains("syntax", StringComparison.OrdinalIgnoreCase) || m.Contains("语法") || m.Contains("SQL"))
            return ("SQL 语法没通过", "建议检查语句拼写；可单独执行定位出错位置");
        // 磁盘/IO
        if (m.Contains("disk", StringComparison.OrdinalIgnoreCase) || m.Contains("磁盘") || m.Contains("space", StringComparison.OrdinalIgnoreCase) || m.Contains("IO", StringComparison.Ordinal) && m.Contains("file", StringComparison.OrdinalIgnoreCase))
            return ("磁盘读写出了问题", "建议检查磁盘空间与文件是否被占用");
        // 取消
        if (m.Contains("cancel", StringComparison.OrdinalIgnoreCase) || m.Contains("取消") || m.Contains("abort", StringComparison.OrdinalIgnoreCase))
            return ("操作已取消", "");
        // 兜底
        return ("出了个意外问题", "详情已写入日志（帮助 → 打开日志目录），可带着日志反馈");
    }
}
