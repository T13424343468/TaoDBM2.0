using System;
using System.Threading.Tasks;

namespace TaoDBM.UI;

/// <summary>结果网格就地编辑钩子（2026-11-24 老板令：不要弹窗，直接在结果集里改）。
/// 浏览页把「表名/主键/提交写库」交给网格，网格只管交互——双击单元格进编辑、
/// Enter 提交、Esc 取消、右键设 NULL / 编辑整行；写库口径全部走 WorkTab 既有出口
/// （主键定位 + 单列 UPDATE，与行编辑弹层同一套字面量转义），不另起写库逻辑。</summary>
public sealed class GridEditHook
{
    /// <summary>(rowIdx, colIdx) 是否可就地编辑（主键列 false；无主键表整体 false）。</summary>
    public Func<int, int, bool> CanEdit { get; init; } = (_, _) => false;

    /// <summary>编辑初值：原始口径（Exporter.Format），NULL 一律空串——提交空串按列类型落 NULL/空串，与弹层同语义。</summary>
    public Func<int, int, string> EditText { get; init; } = (_, _) => "";

    /// <summary>提交就地编辑：text=null 表示「设为 NULL」；成功返回 null，失败返回错误消息（网格还原原值并提示）。</summary>
    public Func<int, int, string?, Task<string?>> CommitAsync { get; init; } = (_, _, _) => Task.FromResult<string?>("未接提交出口");

    /// <summary>右键「编辑整行」：打开既有行编辑弹层（宽行/多列批量改走这里）。</summary>
    public Action<int>? EditWholeRow { get; init; }
}
