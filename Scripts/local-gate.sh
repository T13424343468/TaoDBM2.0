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
