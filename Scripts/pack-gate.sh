#!/usr/bin/env bash
# 出包硬闸（2026-11-24 22:33 老板令三道闸之三：检查不过包不出）
# 口径：接令开工闸管早发现（高概率），出包硬闸管拦截（百分百）。
# 五项硬清单（参照小雪 TaoOps 线 pack-gate.sh 五项口径，TaoDBM 线落地）：
#   1) 总线当日已拉（taobus FETCH_HEAD 当日校验）
#   2) 五张对账表在位且 grep 无挂账标记（v3.22：随包 SHA-256 归档、验收必核账齐全且全 100%）
#   3) 全测绿回执提交号与 HEAD 一致
#   4) HEAD 已 push（ls-remote 核验远端 main）
#   5) 以人为本看后记录在位（当日记录）
# 用法：bash Scripts/pack-gate.sh
# 任一项不过即 GATE-FAIL 退出码 1，包组不得出包。
set -uo pipefail
cd "$(dirname "$0")/.."
fail=0
TODAY=$(date +%Y-%m-%d)
BUS_DIR="${BUS_DIR:-/root/workspace/taobus}"

echo "== 出包硬闸 $TODAY =="

# ── 1) 总线当日已拉 ──
if [ ! -f "$BUS_DIR/.git/FETCH_HEAD" ]; then
  echo "[PACK-GATE 1] 总线仓不可达：$BUS_DIR/.git/FETCH_HEAD 不存在（BUS_DIR 可用环境变量覆盖）"; fail=1
else
  fd=$(date -r "$BUS_DIR/.git/FETCH_HEAD" +%Y-%m-%d 2>/dev/null || echo none)
  if [ "$fd" = "$TODAY" ]; then
    echo "[PACK-GATE 1] 总线当日已拉（FETCH_HEAD $fd）"
  else
    echo "[PACK-GATE 1] 总线非当日拉取：FETCH_HEAD 日期 $fd ≠ $TODAY，先拉总线查新再出包"; fail=1
  fi
fi

# ── 2) 五张对账表在位且无挂账标记 ──
LEDGER_DIR="Docs/对账"
TABLES=("云测日志归档对账表" "录屏截图真验对账表" "崩溃日志随包对账表" "环境镜像复用对账表" "断言实效对账表")
if [ ! -d "$LEDGER_DIR" ]; then
  echo "[PACK-GATE 2] 五张对账表目录缺失：$LEDGER_DIR/ 不存在——表未建，先按 v3.22 建账再出包"; fail=1
else
  for t in "${TABLES[@]}"; do
    f="$LEDGER_DIR/$t.md"
    if [ ! -f "$f" ]; then
      echo "[PACK-GATE 2] 对账表缺失：$f"; fail=1; continue
    fi
    if grep -nE '挂账|\[挂账\]|TODO-挂账|待补齐|未归档' "$f" >/dev/null 2>&1; then
      echo "[PACK-GATE 2] 对账表存在挂账标记：$f"; grep -nE '挂账|\[挂账\]|TODO-挂账|待补齐|未归档' "$f" | head -3; fail=1
    fi
  done
  [ "$fail" = 0 ] && echo "[PACK-GATE 2] 五张对账表齐全且无挂账标记"
fi

# ── 3) 全测绿回执提交号与 HEAD 一致 ──
HEAD_SHA=$(git rev-parse HEAD 2>/dev/null || echo none)
RECEIPT_DIR="Docs/云测回执"
LATEST=$(ls -1t "$RECEIPT_DIR"/*.md 2>/dev/null | head -1 || true)
if [ -z "$LATEST" ]; then
  echo "[PACK-GATE 3] 全测绿回执缺失：$RECEIPT_DIR/ 下无回执文件——先出全测绿回执再出包"; fail=1
else
  if grep -q "$HEAD_SHA" "$LATEST" || grep -q "${HEAD_SHA:0:8}" "$LATEST"; then
    if grep -qE 'ALL-DONE|全测通过|全绿' "$LATEST"; then
      echo "[PACK-GATE 3] 全测绿回执与 HEAD 一致（$LATEST）"
    else
      echo "[PACK-GATE 3] 回执无全绿标记：$LATEST"; fail=1
    fi
  else
    echo "[PACK-GATE 3] 回执提交号与 HEAD 不一致：$LATEST 未含 $HEAD_SHA"; fail=1
  fi
fi

# ── 4) HEAD 已 push（远端 main 核验）──
REMOTE_SHA=$(git ls-remote taopush refs/heads/main 2>/dev/null | cut -f1 || echo none)
if [ "$REMOTE_SHA" = "$HEAD_SHA" ] && [ "$HEAD_SHA" != "none" ]; then
  echo "[PACK-GATE 4] HEAD 已推远端 main（${HEAD_SHA:0:8}）"
else
  echo "[PACK-GATE 4] HEAD 未推远端：远端 main=${REMOTE_SHA:0:8} ≠ 本地 HEAD=${HEAD_SHA:0:8}，先 commit+push 远端核验"; fail=1
fi

# ── 5) 以人为本看后记录在位（当日）──
HUMAN_FILE="Docs/以人为本看后记录.md"
if [ ! -f "$HUMAN_FILE" ]; then
  echo "[PACK-GATE 5] 以人为本看后记录缺失：$HUMAN_FILE 不存在——出包前须看后留记录"; fail=1
else
  if grep -q "$TODAY" "$HUMAN_FILE"; then
    echo "[PACK-GATE 5] 以人为本看后记录在位（含 $TODAY）"
  else
    echo "[PACK-GATE 5] 以人为本看后记录无当日条目（$TODAY）：出包当日须重新看后记录"; fail=1
  fi
fi

echo "==="
if [ "$fail" = 0 ]; then
  echo "PACK-GATE-PASS"
else
  echo "PACK-GATE-FAIL：检查不过包不出（三道闸·出包硬闸）"
  exit 1
fi
