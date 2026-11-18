#!/usr/bin/env python3
# gen-testdb.py -- generate a real SQLite test DB with dirty data + expected
# values for the UI real-db walk (v3.13 real-db smoke). Deterministic output.
import argparse
import json
import os
import sqlite3

PRODUCTS = 1200
ORDERS = 5000


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default="testdb")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)
    db_path = os.path.abspath(os.path.join(args.out_dir, "taodbm-ui.db"))
    exp_path = os.path.abspath(os.path.join(args.out_dir, "expected.json"))
    if os.path.exists(db_path):
        os.remove(db_path)

    con = sqlite3.connect(db_path)
    cur = con.cursor()
    cur.execute(
        "CREATE TABLE products (id INTEGER PRIMARY KEY, name TEXT NOT NULL,"
        " email TEXT, price INTEGER)")
    cur.execute(
        "CREATE TABLE orders (id INTEGER PRIMARY KEY, product_id INTEGER"
        " REFERENCES products(id), amount INTEGER NOT NULL, note TEXT)")

    prods = []
    for i in range(1, PRODUCTS + 1):
        name = "prod-%04d" % i
        if i % 97 == 0:
            name += " \u00b7\u9ad8\u6807\u9898"  # special char in name
        email = None if i % 13 == 0 else "user%04d@example.com" % i
        prods.append((i, name, email, i * 7))
    cur.executemany("INSERT INTO products VALUES (?,?,?,?)", prods)

    orders = []
    for i in range(1, ORDERS + 1):
        pid = (i * 3) % PRODUCTS + 1
        amount = 100 + (i * 37) % 9000
        if i % 11 == 0:
            note = None
        elif i % 23 == 0:
            note = "long-" + ("x" * 500) + "-end"
        else:
            note = "order note %d" % i
        orders.append((i, pid, amount, note))
    cur.executemany("INSERT INTO orders VALUES (?,?,?,?)", orders)
    con.commit()

    expected = {
        "orders_count": ORDERS,
        "products_count": PRODUCTS,
        "sum_amount": cur.execute("SELECT SUM(amount) FROM orders").fetchone()[0],
        "null_email_count": cur.execute(
            "SELECT COUNT(*) FROM products WHERE email IS NULL").fetchone()[0],
        "null_note_count": cur.execute(
            "SELECT COUNT(*) FROM orders WHERE note IS NULL").fetchone()[0],
        "product7_name": cur.execute(
            "SELECT name FROM products WHERE id = 7").fetchone()[0],
        "tables": ["orders", "products"],
        "conn_name": "ui-auto-sqlite",
    }
    con.close()
    with open(exp_path, "w", encoding="utf-8") as f:
        json.dump(expected, f, ensure_ascii=False, indent=2)
    print("[GEN] db=%s" % db_path)
    print("[GEN] expected=%s" % exp_path)
    print("[GEN] expected: %s" % json.dumps(expected, ensure_ascii=False))


if __name__ == "__main__":
    main()
