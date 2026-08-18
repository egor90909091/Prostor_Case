#!/usr/bin/env python3
"""
Офлайн-эмбеддинги для локального прогона без внешних сервисов.

Реализует ровно тот же алгоритм, что StubEmbedder в Chat Service
(hashing trick, FNV-1a, 1024 измерения — под схему vector(1024)/bge-m3,
L2-нормализация), поэтому векторы, посчитанные здесь и в бэкенде,
совпадают бит в бит. Это офлайн-заглушка, а не настоящий bge-m3 —
для реальных эмбеддингов используйте живой Chat Service (EMBEDDING_BASE_URL).

  python3 etl/embed_offline.py --dsn "host=... user=... dbname=..."
"""
import argparse
import math
import re
import sys

DIM = 1024
FNV_OFFSET = 2166136261
FNV_PRIME = 16777619
MASK = 0xFFFFFFFF


def fnv1a(s: str) -> int:
    h = FNV_OFFSET
    for b in s.encode("utf-8"):
        h ^= b
        h = (h * FNV_PRIME) & MASK
    return h


_norm_re = re.compile(r"[^0-9a-zа-я]+")


def normalize(text: str) -> str:
    t = (text or "").lower().replace("ё", "е")
    return _norm_re.sub(" ", t).strip()


def features(text: str):
    """(признак, вес): слово целиком и его символьные триграммы."""
    for word in normalize(text).split():
        if len(word) < 2:
            continue
        yield "w:" + word, 1.0
        padded = "_" + word + "_"
        if len(padded) >= 3:
            for i in range(len(padded) - 2):
                yield "t:" + padded[i:i + 3], 0.35


def embed(text: str):
    v = [0.0] * DIM
    for feat, w in features(text):
        h = fnv1a(feat)
        idx = h % DIM
        sign = -1.0 if (h >> 31) & 1 else 1.0
        v[idx] += sign * w
    norm = math.sqrt(sum(x * x for x in v))
    if norm > 0:
        v = [x / norm for x in v]
    return v


def to_literal(v):
    return "[" + ",".join(f"{x:.6f}" for x in v) + "]"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dsn", required=True)
    args = ap.parse_args()
    try:
        import psycopg2
    except ImportError:
        sys.exit("нужен psycopg2:  pip install psycopg2-binary")

    conn = psycopg2.connect(args.dsn)
    conn.autocommit = False
    with conn.cursor() as cur:
        for table, key in (("search.product_chunk", "id"), ("search.company_chunk", "id")):
            cur.execute(f"SELECT {key}, chunk_text FROM {table} WHERE embedding IS NULL")
            rows = cur.fetchall()
            for i, (rid, text) in enumerate(rows):
                cur.execute(
                    f"UPDATE {table} SET embedding = %s::vector WHERE {key} = %s",
                    (to_literal(embed(text)), rid),
                )
            print(f"{table}: обновлено {len(rows)}")
        conn.commit()
    conn.close()


if __name__ == "__main__":
    main()
