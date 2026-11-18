#!/bin/bash
# 并行喂养版：题目分桶，多进程并行跑（每进程单线程），适配云真机 4 核
# 用法: run-cloud-parallel.sh <model.gguf> <tag> <max_tokens> [workers]
MODEL="$1"; TAG="$2"; NTOK="${3:-512}"; W="${4:-4}"
OUT="Tools/ModelBench/out"
BIN="llama-bench-bin/llama-cli.exe"
mkdir -p "$OUT"

MAPDIR=$(mktemp -d)
ls Tools/ModelBench/questions_gen/*.txt | awk -v w="$W" '{ i = NR % w; file = "'"$MAPDIR"'/bucket" i ".txt"; print > file }'

for f in "$MAPDIR"/bucket*.txt; do
  (
    while IFS= read -r qf; do
      qid=$(basename "$qf" .txt)
      res="$OUT/${TAG}_${qid}.out"
      [ -s "$res" ] && continue   # 断点续跑：已有输出跳过
      start=$(date +%s)
      "$BIN" -m "$MODEL" -p "$(cat "$qf")" -n "$NTOK" --temp 0 --single-turn \
        -c 4096 -t 1 >"$res" 2>"$OUT/${TAG}_${qid}.err"
      end=$(date +%s)
      echo "$TAG $qid wall=$((end-start))s" >> "$OUT/run.log"
    done < "$f"
  ) &
done
wait
rm -rf "$MAPDIR"
echo "BENCH_DONE $TAG" >> "$OUT/run.log"
