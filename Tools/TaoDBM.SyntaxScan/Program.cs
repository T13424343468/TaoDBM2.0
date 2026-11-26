using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;
using System.Text.RegularExpressions;

// 门禁 1.10：App 层语法级扫描（#303 教训：CS8361 插值裸条件本地编不到 App 层只能云测首验——
// Roslyn 解析器不依赖 WinUI 引用，语法级错误本地即可拦截；语义级仍归云测首验）
// 2026-11-27 增补：云测 run398 暴露 WPF 习惯用法三类语义错（Brushes/Grid 附加属性初始化器/TreeView.Roots），
// 本地编不到 App 层拦不住语义，但模式可正则拦截——新增 WPF-ism 规则，同类错误本地即红。
var roots = new[] { "Src", "Tests" };
var bad = 0;

// WPF-ism 规则（WinUI 3 无这些写法，命中即报）：
//   1) Brushes.*          —— WinUI 无 Brushes 类，用 new SolidColorBrush(Microsoft.UI.Colors.X) 或 Ant 令牌
//   2) Grid.Column = n 等附加属性对象初始化器 —— WinUI 附加属性须 SetValue/SetXxx
//   3) .Roots              —— WinUI TreeView 无 Roots，取根节点用 RootNodes
var wpfRules = new (Regex Re, string Msg)[]
{
    (new Regex(@"\bBrushes\.\w+", RegexOptions.Compiled), "WinUI 无 Brushes 类——用 new SolidColorBrush(Microsoft.UI.Colors.X) 或 Ant 令牌"),
    (new Regex(@"^\s*(?:Grid|Canvas|RelativePanel)\.\w+\s*=", RegexOptions.Compiled), "WinUI 附加属性不能用对象初始化器语法——用 SetValue(XxxProperty, v) 或 SetXxx"),
    (new Regex(@"\.Roots\b", RegexOptions.Compiled), "WinUI TreeView 无 Roots——取根节点用 RootNodes"),
};

foreach (var root in roots)
{
    foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
    {
        var text = File.ReadAllText(f);
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), path: f);
        foreach (var d in tree.GetDiagnostics())
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                var pos = d.Location.GetLineSpan().StartLinePosition;
                Console.WriteLine($"{f}({pos.Line + 1},{pos.Character + 1}): {d.Id} {d.GetMessage()}");
                bad++;
            }
        }
        // WPF-ism 语义模式（仅 App 层；Tests/Tools 不用 WinUI 误报无意义）
        if (f.StartsWith("Src/TaoDBM.App") || f.StartsWith(@"Src\TaoDBM.App"))
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                foreach (var (re, msg) in wpfRules)
                    if (re.IsMatch(lines[i]))
                    {
                        Console.WriteLine($"{f}({i + 1}): WPF-ISM {msg}：{lines[i].Trim()}");
                        bad++;
                    }
        }
    }
}
if (bad > 0) { Console.WriteLine($"[SYNTAX-SCAN] {bad} error(s)"); return 1; }
Console.WriteLine("[SYNTAX-SCAN] clean");
return 0;
