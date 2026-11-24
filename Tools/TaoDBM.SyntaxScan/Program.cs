using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

// 门禁 1.10：App 层语法级扫描（#303 教训：CS8361 插值裸条件本地编不到 App 层只能云测首验——
// Roslyn 解析器不依赖 WinUI 引用，语法级错误本地即可拦截；语义级仍归云测首验）
var roots = new[] { "Src", "Tests" };
var bad = 0;
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
    }
}
if (bad > 0) { Console.WriteLine($"[SYNTAX-SCAN] {bad} error(s)"); return 1; }
Console.WriteLine("[SYNTAX-SCAN] clean");
return 0;
