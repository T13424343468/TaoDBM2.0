#!/usr/bin/env python3
# ModelBench 结果评分：qNNN.out 提取 SQL，与 corpus 标准答案归一化比对
# v2 (2026-11-19 喂养监测线): 修复提取缺陷——只在真实回答区（[End thinking] 之后、
#   其后首个 [ Prompt: 之前）提取 SQL。v1 在全文上跑正则，先命中 llama-cli 回显中
#   提示词里的 CREATE TABLE 等 DDL，把提示词误算成答对（930 题实测虚高至 64.6%）。
#   无 [End thinking] 判为 TRUNCATED（思考链截断，单列统计）。
# v3 (2026-11-19): 支持 /no_think 禁链输出（B 组）——无任何思考链标记，llama-cli
#   提示词回显以 "(truncated)" 行结尾，真实回答在其后、"[ Prompt:" 统计行之前；
#   有 [End thinking] 时仍走 v2 路径（A 组结果不受影响）。
#   同时输出宽松/严格（精确匹配）双口径。评分属文本比对，不属模型推理。
# 用法: python3 score.py <out_dir>
import json, os, re, sys

out_dir = sys.argv[1] if len(sys.argv) > 1 else 'Tools/ModelBench/out'

END_THINK = '[End thinking]'
PROMPT_MARK = '[ Prompt:'

def load_corpus():
    items = []
    with open('Tools/ModelBench/corpus/corpus.jsonl', encoding='utf-8-sig') as f:
        for l in f:
            l = l.strip()
            if l:
                items.append(json.loads(l))
    return items

def answer_region(text):
    # 真实回答区：最后一个 [End thinking] 之后、其后首个 [ Prompt: 之前。
    # 提示词回显（含 CREATE TABLE 表结构、题目原文）全部在此区间之外。
    end = text.rfind(END_THINK)
    if end != -1:
        tail = text[end + len(END_THINK):]
        p = tail.find(PROMPT_MARK)
        return tail[:p] if p != -1 else tail
    # 禁链（/no_think）输出：无思考链标记。llama-cli 回显提示词以 "(truncated)" 行
    # 结尾，真实回答在其后、"[ Prompt:" 统计行之前。
    # 注意：带链但中途截断的文件（有 [Start thinking] 无 [End thinking]）必须仍判
    # TRUNCATED，不得落入禁链路径——其提示词回显同样含 "(truncated)"。
    if '[Start thinking]' in text:
        return None  # 思考链截断：模型未产出任何答案
    m = re.search(r'\(truncated\)\s*\n(.*?)\n\s*' + re.escape(PROMPT_MARK), text, re.S)
    if m:
        return m.group(1)
    # EOF 回退（2026-11-22）：c9b3f348 轮起输出缺 [ Prompt: 统计行，
    # 答案直接到文件尾；无该行时按文件尾取答案，避免整轮误判 TRUNCATED。
    m2 = re.search(r'\(truncated\)\s*\n(.*)', text, re.S)
    if m2:
        return m2.group(1)
    return None  # 思考链截断：模型未产出任何答案

def extract_sql(text):
    # 返回 (sql, status)；status in OK_REGION / TRUNCATED
    region = answer_region(text)
    if region is None:
        return '', 'TRUNCATED'
    t = region
    # 优先 ```sql 代码块
    m = re.search(r'```(?:sql)?\s*(.*?)```', t, flags=re.S | re.I)
    if m:
        t = m.group(1)
    # DDL/过程类：CREATE/CALL/EXEC/ALTER/DROP/SHOW 开头取整段（过程体内含分号）
    m = re.search(r'\b(CREATE(?:\s+OR\s+REPLACE)?|CALL|EXEC(?:UTE)?|ALTER|DROP|SHOW)\b.*', t, flags=re.I | re.S)
    if m:
        return m.group(0).strip().rstrip(';').strip(), 'OK_REGION'
    # 取第一条以 SELECT/WITH/INSERT/UPDATE/DELETE 开头的语句
    m = re.search(r'\b(SELECT|WITH|INSERT|UPDATE|DELETE)\b[^;]*;?', t, flags=re.I | re.S)
    if m:
        return m.group(0).strip().rstrip(';').strip(), 'OK_REGION'
    return t.strip().rstrip(';').strip()[:200], 'OK_REGION'

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
    runlog = {}
    runlog_path = os.path.join(out_dir, 'run.log')
    if os.path.exists(runlog_path):
        for l in open(runlog_path, encoding='utf-8', errors='replace'):
            m = re.match(r'(\S+) (q\d+) wall=(\d+)s', l.strip())
            if m:
                runlog[(m.group(1), m.group(2))] = int(m.group(3))

    tags = ('qwen17b', 'dsr15b')
    scores = {t: {'ok': 0, 'ok_strict': 0, 'wrong': 0, 'empty': 0, 'truncated': 0, 'details': []} for t in tags}
    for i, item in enumerate(corpus, 1):
        qid = f'q{i:03d}'
        for tag in tags:
            path = os.path.join(out_dir, f'{tag}_{qid}.out')
            if not os.path.exists(path):
                scores[tag]['details'].append((qid, item['id'], 'MISSING', '', 0))
                continue
            raw = open(path, encoding='utf-8', errors='replace').read()
            sql, status = extract_sql(raw)
            wall = runlog.get((tag, qid), 0)
            if status == 'TRUNCATED':
                scores[tag]['truncated'] += 1
                scores[tag]['details'].append((qid, item['id'], 'TRUNCATED', '', wall))
                continue
            if not sql:
                scores[tag]['empty'] += 1
                scores[tag]['details'].append((qid, item['id'], 'EMPTY', '', wall))
                continue
            region = answer_region(raw) or ''
            got = norm(sql)
            exps = [item['expected']] + item.get('alts', [])
            expected_refuse = item['expected'].startswith('--')
            if expected_refuse:
                # 陷阱题：回答区不应编造查询（按安全口径判定拒绝/解释）
                ok = ('limit' not in got and not re.search(r'\bfrom\b', got)) or '不知道' in region or '无法' in region or '不存在' in region or '安全' in region
            else:
                ok_strict = any(got == norm(e) for e in exps)
                ok = ok_strict
                if not ok:
                    for e in exps:
                        gk, ek = sim_key(got), sim_key(norm(e))
                        # 表名、聚合函数、关键列都命中即算通过（宽松口径，逐条人工可复核）
                        if (ek <= gk) or (len(gk & ek) >= max(2, len(ek) - 2)):
                            ok = True
                            break
                if ok_strict:
                    scores[tag]['ok_strict'] += 1
            if ok:
                scores[tag]['ok'] += 1
            else:
                scores[tag]['wrong'] += 1
            scores[tag]['details'].append((qid, item['id'], 'OK' if ok else 'WRONG', sql[:120], wall))

    for tag in tags:
        s = scores[tag]
        total = s['ok'] + s['wrong'] + s['empty'] + s['truncated']
        print(f"\n===== {tag}: 宽松 {s['ok']}/{total} | 严格 {s['ok_strict']}/{total} | 空{s['empty']} 截断{s['truncated']} =====")
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
