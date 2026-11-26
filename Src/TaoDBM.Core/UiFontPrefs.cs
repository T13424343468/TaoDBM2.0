using System.Text.Json;

namespace TaoDBM.Core;

/// <summary>
/// 发现152：界面外观偏好——编辑器 / 网格字体字号（uifonts.json）。
/// 损坏、缺失回落默认，绝不闪退；字号钳制 12-24。独立于 UiPrefs（发现151 通用键值）与 AppPrefs（关闭行为）。
/// </summary>
public sealed class UiFontPrefs
{
    public const int MinFontSize = 12;
    public const int MaxFontSize = 24;

    public string EditorFontFamily { get; set; } = "Consolas";
    public int EditorFontSize { get; set; } = 13;
    public string GridFontFamily { get; set; } = "Consolas";
    public int GridFontSize { get; set; } = 12;

    /// <summary>字号钳制：越界回边界，不抛错（输入框随手多打一位也不崩）。</summary>
    public static int ClampFontSize(int v) => Math.Clamp(v, MinFontSize, MaxFontSize);

    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("uifonts.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    public static UiFontPrefs Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new UiFontPrefs();
            var v = JsonSerializer.Deserialize<UiFontPrefs>(System.IO.File.ReadAllText(File));
            if (v is null) return new UiFontPrefs();
            v.EditorFontSize = ClampFontSize(v.EditorFontSize);
            v.GridFontSize = ClampFontSize(v.GridFontSize);
            if (string.IsNullOrWhiteSpace(v.EditorFontFamily)) v.EditorFontFamily = "Consolas";
            if (string.IsNullOrWhiteSpace(v.GridFontFamily)) v.GridFontFamily = "Consolas";
            return v;
        }
        catch { return new UiFontPrefs(); }
    }

    public void Save()
    {
        EditorFontSize = ClampFontSize(EditorFontSize);
        GridFontSize = ClampFontSize(GridFontSize);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Write($"外观偏好保存失败: {ex.Message}"); }
    }
}

/// <summary>
/// 发现152：快捷键偏好——清单展示＋改绑（冲突当场人话提示不落盘）＋复位（shortcuts.json）。
/// 绑定串口径：修饰键 Ctrl/Shift/Alt/Win ＋ 主键，如 "Ctrl+Shift+N"、"F5"、"Ctrl+Enter"。
/// MainWindow 侧加速键经 Effective(actionKey) 取生效绑定，改绑即生效；编辑器内置四键随 WorkTab 接线同口径。
/// </summary>
public static class ShortcutPrefs
{
    /// <summary>可改绑动作清单（Key=动作标识，Label=界面文案，DefaultBinding=出厂绑定）。</summary>
    public sealed record ActionDef(string Key, string Label, string DefaultBinding);

    public static readonly List<ActionDef> Actions = new()
    {
        new ActionDef("manual", "打开操作手册", "F1"),
        new ActionDef("focus", "专注模式", "F11"),
        new ActionDef("newquery", "新建查询", "Ctrl+N"),
        new ActionDef("closetab", "关闭当前页签", "Ctrl+W"),
        new ActionDef("cycletab", "切换工作页签", "Ctrl+Tab"),
        new ActionDef("run", "执行 SQL（F5）", "F5"),
        new ActionDef("runctrl", "执行 SQL（Ctrl+Enter）", "Ctrl+Enter"),
        new ActionDef("complete", "SQL 补全", "Ctrl+Space"),
        new ActionDef("savequery", "保存查询", "Ctrl+S"),
    };

    /// <summary>编辑器内置（WorkTab 加速键）动作：改绑落盘后由 WorkTab 注册处取生效绑定。</summary>
    public static bool IsEditorBuiltin(string actionKey) => actionKey is "run" or "runctrl" or "complete" or "savequery";

    public static ActionDef? Def(string actionKey) => Actions.FirstOrDefault(a => a.Key == actionKey);

    private static string _file = "";
    private static string File => _file.Length > 0 ? _file : TaoPaths.FilePath("shortcuts.json");

    /// <summary>测试或自定义存储位置。</summary>
    public static void UseFile(string path) => _file = path;

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File))
                   ?? new Dictionary<string, string>();
        }
        catch { return new Dictionary<string, string>(); }   // 损坏回默认，不闪退（发现152 验收3）
    }

    private static void SaveMap(Dictionary<string, string> map)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            AtomicWrite.Text(File, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Write($"快捷键偏好保存失败: {ex.Message}"); }
    }

    /// <summary>生效绑定：有改绑用改绑，否则出厂值；动作未知返回空串（调用方保持内置键不动）。</summary>
    public static string Effective(string actionKey)
    {
        var def = Def(actionKey);
        if (def is null) return "";
        var map = LoadMap();
        return map.TryGetValue(actionKey, out var b) && Parse(b).Ok ? b : def.DefaultBinding;
    }

    /// <summary>绑定串解析与归一：修饰键大小写不敏感、顺序归一；主键限字母/数字/F1-F12/Enter/Space/Tab/方向等。</summary>
    public static (bool Ok, string Error, string Normalized) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "请按下的新组合不能为空。", "");
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToList();
        if (parts.Count == 0) return (false, "没识别出按键组合，请重试。", "");
        string key = parts[^1];
        var mods = parts.Take(parts.Count - 1).ToList();
        foreach (var m in mods)
            if (m is not ("ctrl" or "shift" or "alt" or "win"))
                return (false, $"不认识的修饰键「{m}」：修饰键只支持 Ctrl / Shift / Alt / Win。", "");
        bool keyOk = System.Text.RegularExpressions.Regex.IsMatch(key,
            @"^[a-z0-9]$|^(f([1-9]|1[0-2]))$|^(enter|space|tab|escape|home|end|pageup|pagedown|(up|down|left|right))$");
        if (!keyOk) return (false, $"不认识的主键「{key}」：用字母 / 数字 / F1-F12 / Enter / Space 等常用键。", "");
        var norm = string.Join("+", mods.OrderBy(m => m is "ctrl" ? 0 : m is "shift" ? 1 : m is "alt" ? 2 : 3)
            .Select(m => m switch { "ctrl" => "Ctrl", "shift" => "Shift", "alt" => "Alt", _ => "Win" })
            .Concat(new[] { key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..] }));
        return (true, "", norm);
    }

    /// <summary>改绑：冲突当场人话提示不落盘（发现152 验收2）；成功返回空串。</summary>
    public static string Rebind(string actionKey, string binding)
    {
        if (Def(actionKey) is null) return "没有这个快捷键动作。";
        var (ok, err, norm) = Parse(binding);
        if (!ok) return err;
        var map = LoadMap();
        foreach (var other in Actions)
        {
            if (other.Key == actionKey) continue;
            string otherBinding = map.TryGetValue(other.Key, out var b) && Parse(b).Ok ? b : other.DefaultBinding;
            if (string.Equals(otherBinding, norm, StringComparison.OrdinalIgnoreCase))
                return $"「{norm}」已经用于「{other.Label}」，请换一个组合。";
        }
        if (norm == Def(actionKey)!.DefaultBinding) map.Remove(actionKey);   // 改回出厂即清覆盖
        else map[actionKey] = norm;
        SaveMap(map);
        AppLog.Write($"[快捷键] rebind {actionKey} -> {norm}");
        return "";
    }

    /// <summary>复位单条：删覆盖回出厂。</summary>
    public static void Reset(string actionKey)
    {
        var map = LoadMap();
        if (map.Remove(actionKey)) SaveMap(map);
    }
}
