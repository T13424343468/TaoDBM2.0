namespace TaoDBM.Core;

/// <summary>剪贴板堆栈（批次一）：复制过的 SQL 片段按序入栈，可回看循环粘贴，进程内有效。</summary>
public static class ClipboardStack
{
    private static readonly object Gate = new();
    private static readonly List<string> Items_ = new();
    private const int Capacity = 20;

    /// <summary>入栈：与栈顶相同去重；已在栈中更早位置的移到栈顶；超容量淘汰最旧。</summary>
    public static void Push(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (Gate)
        {
            Items_.RemoveAll(x => x == text);
            Items_.Insert(0, text);
            if (Items_.Count > Capacity) Items_.RemoveRange(Capacity, Items_.Count - Capacity);
        }
    }

    /// <summary>当前堆栈快照（新→旧）。</summary>
    public static List<string> Snapshot()
    {
        lock (Gate) { return new List<string>(Items_); }
    }

    /// <summary>清空堆栈。</summary>
    public static void Clear()
    {
        lock (Gate) { Items_.Clear(); }
    }
}
