#!/usr/bin/env python3
# ModelBench 结果评分：qNNN.out 提取 SQL，与 corpus 标准答案归一化比对
# 用法: python3 score.py <out_dir>
import json, os, re, sys

out_dir = sys.argv[1] if len(sys.argv) > 1 else 'Tools/ModelBench/out'

def load_corpus():
    items = []
    with open('Tools/ModelBench/corpus/corpus.jsonl', encoding='utf-8-sig') as f:
        for l in f:
            l = l.strip()
            if l:
                items.append(json.loads(l))
    return items

def extract_sql(text):
    # 取第一段非空非注释正文里的 SQL（llama-cli stdout；可能有思考或解释）
    t = text
    # 去掉 <think>...</think>
    t = re.sub(r'<think>.*?</think>', '', t, flags=re.S)
    # 优先 ```sql 代码块
    m = re.search(r'```(?:sql)?\s*(.*?)```', t, flags=re.S | re.I)
    if m:
        t = m.group(1)
    # 取第一条以 SELECT/INSERT/UPDATE/DELETE/WITH 开头的语句
    m = re.search(r'\b(SELECT|WITH)\b[^;]*;?', t, flags=re.I | re.S)
    if m:
        return m.group(0).strip().rstrip(';').strip()
    return t.strip().rstrip(';').strip()[:200]

def norm(sql):
    s = sql.lower()
    s = re.sub(r'--[^\n]*', ' ', s)
    s = re.sub(r'\s+', ' ', s).strip()
    s = s.rstrip(';').strip()
    s = s.replace('%s', '?')
    s = re.sub(r'\bbacktick_remove\b', '', s)
    s = s.replace('`', '')
    # 等价写法容忍
    s = s.replace('!=', '<>')
    return s.strip()

def sim_key(s):
    # 抽取关键 token 集合近似比对
    return set(re.findall(r'[a-z_]+|\d+|\'[^\']*\'', s))

def main():
    corpus = load_corpus()
    results = {}
    runlog = {}
    runlog_path = os.path.join(out_dir, 'run.log')
    if os.path.exists(runlog_path):
        for l in open(runlog_path, encoding='utf-8', errors='replace'):
            m = re.match(r'(\S+) (q\d+) wall=(\d+)s', l.strip())
            if m:
                runlog[(m.group(1), m.group(2))] = int(m.group(3))

    tags = ('qwen17b', 'dsr15b')
    scores = {t: {'ok': 0, 'wrong': 0, 'empty': 0, 'details': []} for t in tags}
    for i, item in enumerate(corpus, 1):
        qid = f'q{i:03d}'
        for tag in tags:
            path = os.path.join(out_dir, f'{tag}_{qid}.out')
            if not os.path.exists(path):
                scores[tag]['details'].append((qid, item['id'], 'MISSING', '', 0))
                continue
            raw = open(path, encoding='utf-8', errors='replace').read()
            sql = extract_sql(raw)
            wall = runlog.get((tag, qid), 0)
            if not sql:
                scores[tag]['empty'] += 1
                scores[tag]['details'].append((qid, item['id'], 'EMPTY', '', wall))
                continue
            got, exp = norm(sql), norm(item['expected'])
            expected_refuse = item['expected'].startswith('-- REFUSE')
            if expected_refuse:
                # 陷阱题：答案里不应编造表名/直接给出查询
                ok = ('limit' not in got and not re.search(r'\bfrom\b', got)) or '不知道' in raw or '无法' in raw or '不存在' in raw
            else:
                if got == exp:
                    ok = True
                else:
                    gk, ek = sim_key(got), sim_key(exp)
                    # 表名、聚合函数、关键列都命中即算通过（宽松口径，逐条人工可复核）
                    ok = (ek <= gk) or (len(gk & ek) >= max(2, len(ek) - 2))
            if ok:
                scores[tag]['ok'] += 1
            else:
                scores[tag]['wrong'] += 1
            scores[tag]['details'].append((qid, item['id'], 'OK' if ok else 'WRONG', sql[:120], wall))

    for tag in tags:
        s = scores[tag]
        total = s['ok'] + s['wrong'] + s['empty']
        print(f"\n===== {tag}: {s['ok']}/{total} 正确 (空{ s['empty']}) =====")
        walls = [d[4] for d in s['details'] if d[4] > 0]
        if walls:
            print(f"单题耗时: min={min(walls)}s max={max(walls)}s avg={sum(walls)/len(walls):.0f}s 总计={sum(walls)}s")
        for d in s['details']:
            if d[2] != 'OK':
                print(f"  {d[0]} {d[1]} {d[2]}: {d[3]}")
    # 保存明细供报告引用
    with open(os.path.join(out_dir, 'score_detail.json'), 'w', encoding='utf-8') as f:
        json.dump({t: scores[t]['details'] for t in tags}, f, ensure_ascii=False, indent=1)

if __name__ == '__main__':
    main()
