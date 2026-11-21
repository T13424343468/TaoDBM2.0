#!/usr/bin/env python3
# gen-testdb.py -- generate a real SQLite test DB (or MySQL/PostgreSQL/SQL
# Server SQL script with the SAME data contract) with dirty data + expected
# values for the UI real-db walk (v3.13 real-db smoke). Deterministic output.
import argparse
import json
import os
import sqlite3

PRODUCTS = 1200
ORDERS = 5000


def rows():
    prods = []
    for i in range(1, PRODUCTS + 1):
        name = "prod-%04d" % i
        if i % 97 == 0:
            name += " \u00b7\u9ad8\u6807\u9898"  # special char in name
        email = None if i % 13 == 0 else "user%04d@example.com" % i
        prods.append((i, name, email, i * 7))
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
    return prods, orders


def esc_sql(s):
    return s.replace("'", "''")


def write_server_sql(engine, out_path, db_name):
    # Same data contract as SQLite: identical rows + identical expected values.
    prods, orders = rows()
    sum_amount = sum(o[2] for o in orders)
    null_email = sum(1 for p in prods if p[2] is None)
    null_note = sum(1 for o in orders if o[3] is None)
    expected = {
        "orders_count": ORDERS,
        "products_count": PRODUCTS,
        "sum_amount": sum_amount,
        "null_email_count": null_email,
        "null_note_count": null_note,
        "product7_name": prods[6][1],
        "tables": ["orders", "products"],
        "conn_name": "ui-auto-%s" % engine,
        "db_name": db_name,
    }
    with open(out_path, "w", encoding="utf-8") as f:
        if engine == "mysql":
            f.write("CREATE DATABASE IF NOT EXISTS `%s`;\nUSE `%s`;\n" % (db_name, db_name))
            f.write("DROP TABLE IF EXISTS orders;\nDROP TABLE IF EXISTS products;\n")
            f.write("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR(200) NOT NULL,"
                    " email VARCHAR(200), price INT);\n")
            f.write("CREATE TABLE orders (id INT PRIMARY KEY, product_id INT,"
                    " amount INT NOT NULL, note TEXT);\n")
        elif engine == "postgres":
            # psql 脚本：默认连 postgres 库执行，建库后 \connect 切入目标库（PG 无 USE 语句）
            f.write("DROP DATABASE IF EXISTS %s;\nCREATE DATABASE %s;\n" % (db_name, db_name))
            f.write("\\connect %s\n" % db_name)
            f.write("DROP TABLE IF EXISTS orders;\nDROP TABLE IF EXISTS products;\n")
            f.write("CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR(200) NOT NULL,"
                    " email VARCHAR(200), price INT);\n")
            f.write("CREATE TABLE orders (id INT PRIMARY KEY, product_id INT,"
                    " amount INT NOT NULL, note TEXT);\n")
        else:  # mssql
            f.write("IF DB_ID(N'%s') IS NULL CREATE DATABASE [%s];\nGO\nUSE [%s];\nGO\n" % (db_name, db_name, db_name))
            f.write("IF OBJECT_ID(N'orders','U') IS NOT NULL DROP TABLE orders;\nIF OBJECT_ID(N'products','U') IS NOT NULL DROP TABLE products;\nGO\n")
            f.write("CREATE TABLE products (id INT PRIMARY KEY, name NVARCHAR(200) NOT NULL,"
                    " email NVARCHAR(200), price INT);\n")
            f.write("CREATE TABLE orders (id INT PRIMARY KEY, product_id INT,"
                    " amount INT NOT NULL, note NVARCHAR(MAX));\n")
        for r in prods:
            f.write("INSERT INTO products VALUES (%d,'%s',%s,%d);\n" % (
                r[0], esc_sql(r[1]), "NULL" if r[2] is None else "'%s'" % esc_sql(r[2]), r[3]))
        for r in orders:
            f.write("INSERT INTO orders VALUES (%d,%d,%d,%s);\n" % (
                r[0], r[1], r[2], "NULL" if r[3] is None else "'%s'" % esc_sql(r[3])))
    return expected


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default="testdb")
    ap.add_argument("--engine", default="sqlite", choices=["sqlite", "mysql", "mssql", "postgres"],
                    help="sqlite = real db file; mysql/mssql/postgres = SQL script for server load")
    ap.add_argument("--db-name", default="taodbmui",
                    help="schema/database name for server engines")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)
    exp_path = os.path.abspath(os.path.join(args.out_dir, "expected.json"))

    if args.engine != "sqlite":
        sql_path = os.path.abspath(os.path.join(args.out_dir, "taodbm-ui.%s.sql" % args.engine))
        expected = write_server_sql(args.engine, sql_path, args.db_name)
        with open(exp_path, "w", encoding="utf-8") as f:
            json.dump(expected, f, ensure_ascii=False, indent=2)
        print("[GEN] sql=%s" % sql_path)
        print("[GEN] expected=%s" % exp_path)
        print("[GEN] expected: %s" % json.dumps(expected, ensure_ascii=False))
        return

    db_path = os.path.abspath(os.path.join(args.out_dir, "taodbm-ui.db"))
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

    prods, orders = rows()
    cur.executemany("INSERT INTO products VALUES (?,?,?,?)", prods)
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
        "db_name": "main",
    }
    con.close()
    with open(exp_path, "w", encoding="utf-8") as f:
        json.dump(expected, f, ensure_ascii=False, indent=2)
    print("[GEN] db=%s" % db_path)
    print("[GEN] expected=%s" % exp_path)
    print("[GEN] expected: %s" % json.dumps(expected, ensure_ascii=False))


if __name__ == "__main__":
    main()
