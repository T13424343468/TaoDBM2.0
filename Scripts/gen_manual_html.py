#!/usr/bin/env python3
# 从 ManualContent.cs 解析章节内容，生成 Docs/操作手册.html（与程序内手册同源，避免两处内容漂移）
import io, re, html

CS = 'Src/TaoDBM.App/UI/ManualContent.cs'
OUT = 'Docs/UserManual.html'

src = io.open(CS, encoding='utf-8-sig').read()
chapters_block = src[src.index('public static readonly Chapter[] Chapters'):]
chapter_chunks = re.split(r'new\("([^"]+)",\s*new Section\[\]', chapters_block)[1:]
chapters = []
for i in range(0, len(chapter_chunks), 2):
    title = chapter_chunks[i]
    chunk = chapter_chunks[i + 1]
    secs = []
    for m in re.finditer(r'new\("([^"]+)",\s*@"(.*?)"\)', chunk, re.S):
        secs.append((m.group(1), m.group(2).replace('""', '"')))
    chapters.append((title, secs))

parts = []
parts.append('''<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>TaoDBM 操作手册</title>
<style>
  :root{--primary:#1677FF;--primary-dark:#0958D9;--bg:#F5F6F8;--card:#FFFFFF;--border:#E5E7EB;--text:#1F2329;--sub:#6B7280}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font-family:"Segoe UI","Microsoft YaHei",sans-serif;font-size:14px;line-height:1.75}
  header{background:var(--primary);color:#fff;padding:18px 28px}
  header h1{margin:0;font-size:20px}
  header p{margin:4px 0 0;font-size:12px;opacity:.85}
  .wrap{max-width:1080px;margin:20px auto;padding:0 16px;display:grid;grid-template-columns:220px 1fr;gap:18px}
  nav{position:sticky;top:16px;align-self:start;background:var(--card);border:1px solid var(--border);border-radius:6px;padding:10px}
  nav a{display:block;padding:6px 10px;color:var(--text);text-decoration:none;border-radius:4px;font-size:13px}
  nav a:hover{background:#E6F4FF;color:var(--primary-dark)}
  main section{background:var(--card);border:1px solid var(--border);border-radius:6px;padding:18px 22px;margin-bottom:16px}
  h2{font-size:17px;color:var(--primary-dark);margin:0 0 4px;border-bottom:1px solid var(--border);padding-bottom:8px}
  h3{font-size:15px;color:var(--primary-dark);margin:18px 0 6px}
  p{margin:6px 0}
  .bullet{margin:4px 0 4px 18px}
  .note{margin:4px 0 4px 18px;color:var(--primary-dark)}
  footer{text-align:center;color:var(--sub);font-size:12px;padding:16px 0 28px}
</style>
</head>
<body>
<header><h1>TaoDBM 操作手册</h1><p>程序内随时按 F1 打开 · 本文件与程序内手册同源 · 支持七库：MySQL · PostgreSQL · SQL Server · SQLite · Oracle · MongoDB · Redis</p></header>
<div class="wrap">
<nav>''')
for i, (title, _) in enumerate(chapters):
    parts.append(f'<a href="#ch{i}">{html.escape(title)}</a>')
parts.append('</nav>\n<main>')
for i, (title, secs) in enumerate(chapters):
    parts.append(f'<section id="ch{i}"><h2>{i+1}. {html.escape(title)}</h2>')
    for head, body in secs:
        parts.append(f'<h3>{html.escape(head)}</h3>')
        for line in body.strip('\n').split('\n'):
            line = line.rstrip()
            if line.startswith('· '):
                parts.append(f'<p class="bullet">· {html.escape(line[2:])}</p>')
            elif line.startswith('！'):
                parts.append(f'<p class="note">{html.escape(line)}</p>')
            elif line:
                parts.append(f'<p>{html.escape(line)}</p>')
    parts.append('</section>')
parts.append('</div>')
parts.append('<footer>TaoDBM · 道生一，一生二，二生三，三生万物。数据即万物。</footer>')
parts.append('</body></html>')

io.open(OUT, 'w', encoding='utf-8').write('\n'.join(parts))
total = sum(len(h) + len(b) for _, secs in chapters for h, b in secs)
print(f'chapters={len(chapters)} sections={sum(len(s) for _, s in chapters)} chars={total} -> {OUT}')
