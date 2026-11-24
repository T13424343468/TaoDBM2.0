#!/usr/bin/env bash
# 推送前自检闸门（2026-11-19 16:15 落制：让机器替人"注意"，不靠记性）
# 口径：制度 tao-testing-standard 四.7 / 修坑必带回归断言；本沙箱验不了的部分（App XAML 编译、真机 UI、老库安装）唯一归云测首验。
# 用法：bash Scripts/local-gate.sh [--full]
#   --full 追加本地全测（约 5 分钟，盯尾部 ALL-DONE）；不加则跑前两关后提示。
set -uo pipefail
cd "$(dirname "$0")/.."
fail=0

# 1) 编码规约：改动过的 .cs/.xaml 必须带 UTF-8 BOM（暂存区口径）
for f in $(git diff --cached --name-only --diff-filter=ACMR 2>/dev/null | grep -E '\.(cs|xaml)$' || true); do
  head=$(head -c3 "$f" | od -An -tx1 | tr -d ' ')
  if [ "$head" != "efbbbf" ]; then echo "[GATE] 缺 UTF-8 BOM: $f"; fail=1; fi
done

# 1.5) App 层 Core 类型 using 检查（#247 教训：本地沙箱编不了 App XAML，缺 using 只能云测首验才爆——
#      引用 Core 静态/实体类型的 App 源文件必须显式 using TaoDBM.Core，机器替人注意）
for f in $(git diff --cached --name-only --diff-filter=ACMR 2>/dev/null | grep -E '^Src/TaoDBM.App/.*\.cs$' || true); do
  if grep -qE '\b(SqlFolder|SqlFindReplace|SqlFindOptions|SqlStatementLocator|SqlBeautifier|SqlCommentToggler|SqlSnippets|SqlBracketMatcher|ExplainPlanEngine|ClipboardStack|AppLog)\b' "$f" \
     && ! grep -q 'using TaoDBM.Core;' "$f"; then
    echo "[GATE] 引用 Core 类型但缺 using TaoDBM.Core: $f"; fail=1
  fi
done

# 1.6) XAML 禁用裸 AutomationId（#248 教训：WinUI 无 AutomationId 属性，XamlCompiler WMC0011 只能云测首验——
#      控件标识一律用 AutomationProperties.AutomationId 附加属性，机器替人注意）
for f in $(git diff --cached --name-only --diff-filter=ACMR 2>/dev/null | grep -E '^Src/TaoDBM\.App/.*\.xaml$' || true); do
  if grep -nE '(^|[^.])\bAutomationId=' "$f" | grep -qv 'AutomationProperties\.AutomationId='; then
    echo "[GATE] XAML 误用裸 AutomationId（应为 AutomationProperties.AutomationId）: $f"; fail=1
  fi
done

# 1.7) C# 禁用裸 AutomationId（#268 教训：WinUI 控件代码侧同样无 AutomationId 属性，对象初始化器里写
#      AutomationId = "..." 只能云测首验才爆——代码侧一律 AutomationProperties.SetAutomationId，机器替人注意）
for f in $(git diff --cached --name-only --diff-filter=ACMR 2>/dev/null | grep -E '^Src/TaoDBM\.App/.*\.cs$' || true); do
  if grep -nE '(^|[^.\w])AutomationId[[:space:]]*=[[:space:]]*["]' "$f" | grep -qv 'AutomationProperties\.AutomationId'; then
    echo "[GATE] C# 误用裸 AutomationId（应为 AutomationProperties.SetAutomationId）: $f"; fail=1
  fi
done

# 1.8) 设计令牌单一出口（批次六格1：色值/字号/圆角字面量只准在 Ant.cs 定义，UI 文件一律走 Ant.*/AntTokens.*）
for f in $(git diff --cached --name-only --diff-filter=ACMR 2>/dev/null | grep -E '^Src/TaoDBM\.App/.*\.cs$' | grep -v 'UI/Ant\.cs' || true); do
  if grep -nE 'Brush\("#|FontSize[[:space:]]*=[[:space:]]*[0-9]|CornerRadius[[:space:]]*=[[:space:]]*new CornerRadius\([0-9]' "$f" | grep -qv '^\s*//'; then
    echo "[GATE] 设计令牌字面量（应走 Ant.*/AntTokens.*）: $f"; grep -nE 'Brush\("#|FontSize[[:space:]]*=[[:space:]]*[0-9]|CornerRadius[[:space:]]*=[[:space:]]*new CornerRadius\([0-9]' "$f" | head -3; fail=1
  fi
done

# 1.9) App 层重复声明扫描（#302 教训：合并残留重复 using/字段，本地沙箱编不了 App XAML 只能云测首验——
#      逐文件按类作用域扫重复 using 与重复私有字段，机器替人注意）
python3 - <<'PYEOF' || { echo "[GATE] App 层重复声明（见上）"; fail=1; }
import glob, re, sys
bad = False
for f in glob.glob('Src/TaoDBM.App/**/*.cs', recursive=True):
    lines = open(f, encoding='utf-8-sig').read().split('\n')
    seen_using = set()
    for i, line in enumerate(lines, 1):
        m = re.match(r'\s*using\s+([\w.]+)\s*;', line)
        if m:
            if m.group(1) in seen_using:
                print(f"{f}({i}): CS0105 重复 using {m.group(1)}"); bad = True
            seen_using.add(m.group(1))
    classes = []
    for i, line in enumerate(lines, 1):
        mc = re.match(r'\s*(?:public|internal|sealed|partial|static|abstract)*\s*(?:sealed |partial )*(?:class|record)\s+(\w+)', line)
        if mc: classes.append((mc.group(1) + f"@{i}", i))
    def owner(i):
        o = None
        for name, ln in classes:
            if ln < i: o = name
        return o
    decls = {}
    for i, line in enumerate(lines, 1):
        m = re.match(r'\s*private\s+[\w?<>\[\], .]+\s+(_\w+)\s*[;=]', line)
        if m:
            decls.setdefault((owner(i), m.group(1)), []).append(i)
    for (cls, name), v in decls.items():
        if len(v) > 1:
            print(f"{f}: 类 {cls} 重复私有字段 {name} 行 {v}"); bad = True
sys.exit(1 if bad else 0)
PYEOF

export DOTNET_ROOT="${DOTNET_ROOT:-/root/workspace/.local/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

# 2) 严格口径双档零警告（今天 CS8765 漏网即此前只验单档的教训：Debug/Release 都要跑）
for cfg in Debug Release; do
  if ! dotnet build Tests/TaoDBM.LogicTest -c "$cfg" --nologo -warnaserror > "/tmp/gate-$cfg.log" 2>&1; then
    echo "[GATE] 严格口径 $cfg 红，尾部如下："; tail -20 "/tmp/gate-$cfg.log"; fail=1
  else
    echo "[GATE] 严格口径 $cfg 清零"
  fi
done

# 3) 本地全测（可选 --full；定版推送前必跑）
if [ "${1:-}" = "--full" ]; then
  if dotnet run --project Tests/TaoDBM.LogicTest | tail -5; then
    echo "[GATE] 本地全测通过"
  else
    echo "[GATE] 本地全测红"; fail=1
  fi
else
  echo "[GATE] 提示：定版推送前须跑全测：dotnet run --project Tests/TaoDBM.LogicTest（约 5 分钟，盯 ALL-DONE）"
fi

[ "$fail" = 0 ] && echo "GATE-PASS" || { echo "GATE-FAIL"; exit 1; }
