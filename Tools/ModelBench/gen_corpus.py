import json, os

BASE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(BASE, 'questions_gen')
SCHEMA = """CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(50), city VARCHAR(50), created_at DATETIME);
CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR(100), category VARCHAR(50), price DECIMAL(10,2), stock INT);
CREATE TABLE orders (id INT PRIMARY KEY, user_id INT, status VARCHAR(20), created_at DATETIME);
CREATE TABLE order_items (id INT PRIMARY KEY, order_id INT, product_id INT, quantity INT, unit_price DECIMAL(10,2));
CREATE TABLE payments (id INT PRIMARY KEY, order_id INT, amount DECIMAL(10,2), method VARCHAR(20), paid_at DATETIME);"""

os.makedirs(OUT, exist_ok=True)
for old in os.listdir(OUT):
    os.remove(os.path.join(OUT, old))

n = 0
with open(os.path.join(BASE, 'corpus', 'corpus.jsonl'), encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        item = json.loads(line)
        n += 1
        prompt = ("你是一个数据库助手。根据表结构和问题，写出 MySQL 查询语句，只输出一条 SQL，不要任何解释。\n"
                  "表结构：\n" + SCHEMA + "\n问题：" + item['q'] + "\n注意：只输出 SQL。")
        with open(os.path.join(OUT, 'q%03d.txt' % n), 'w', encoding='utf-8') as g:
            g.write(prompt)
print('generated %d questions' % n)
