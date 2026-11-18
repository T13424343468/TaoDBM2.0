#!/bin/bash
# 双模型问库兜底实测（云端 Windows 真机口径）：规则层接不住的问题交模型，
# 度量：加载耗时、首包前等待、出字速度、完整回答耗时、SQL 正确性（输出留档人工核对）
# 用法: run-cloud.sh <model.gguf> <tag> <max_tokens>
MODEL="$1"; TAG="$2"; NTOK="${3:-512}"
OUT="Tools/ModelBench/out"
BIN="llama-bench-bin/llama-cli.exe"
mkdir -p "$OUT"

for qf in Tools/ModelBench/questions/*.txt; do
  qid=$(basename "$qf" .txt)
  res="$OUT/${TAG}_${qid}.out"
  start=$(date +%s)
  "$BIN" -m "$MODEL" -p "$(cat "$qf")" -n "$NTOK" --temp 0 --single-turn \
    -c 4096 -t 4 2>"$res"
  end=$(date +%s)
  echo "$TAG $qid wall=$((end-start))s" >> "$OUT/run.log"
done
echo "BENCH_DONE $TAG" >> "$OUT/run.log"
