#!/usr/bin/env python3
"""Pairs functions of the Toolkit's DLLs with functions of the game by their pseudo-code.

The Toolkit's DLLs carry names; the game executable does not. Matching by calls and virtual tables
(symbols.py) stops where a chain of calls is broken. This module compares the functions
themselves: the numbers a function uses (field offsets, masks, sizes, magic values) and the text
constants it refers to survive a rebuild even when registers, types and variable names do not.

A function's features are those numbers, pairs of consecutive numbers (so order counts) and its
text constants. Rare features weigh more. For every Toolkit function the game functions that
share a rare feature are candidates; the score of a candidate is the weight of the shared
features over the weight of all features of both. A pair is kept when each side is the other's
best candidate, the score is high enough and the runner-up is clearly behind.

Nothing here proves a pair. The thresholds are chosen on pairs already known from symbols.py
(text constants and virtual tables, found without pseudo-code): `--check` hides those and counts
how many this method finds and how many of them it gets right.
"""
import array
import collections
import hashlib
import math
import multiprocessing
import pickle
import re
import sqlite3
import sys
import time
import zlib

COMMENT = re.compile(r"/\*.*?\*/", re.S)
TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|(?<![\w.])(-\s*)?(0x[0-9a-fA-F]+|\d+)(?![\w.])')
MASK = (1 << 63) - 1


def features(text):
    """Sorted array of 63-bit feature hashes of one function's pseudo-code."""
    text = COMMENT.sub(" ", text)
    brace = text.find("{")                       # skip the declaration: its types differ by build
    body = text[brace:] if brace >= 0 else text
    out, last = set(), None
    for m in TOKEN.finditer(body):
        if m.group(1) is not None:
            if len(m.group(1)) >= 4:
                out.add(int.from_bytes(hashlib.blake2b(m.group(1).encode("utf-8", "replace"), digest_size=8).digest(), "little") & MASK)
            continue
        literal = m.group(3)
        value = int(literal, 16) if literal[:2] in ("0x", "0X") else int(literal)
        if value < 2:
            continue
        if value >= 1 << 63:                     # the same negative number is printed both ways
            value = (1 << 64) - value
        elif 1 << 31 <= value < 1 << 32:
            value = (1 << 32) - value
        if 0x140000000 <= value < 0x190000000:   # an address of the image, not a constant
            continue
        # plain arithmetic, not hash(): hash() of a tuple holding a str differs between processes
        out.add((value * 0x9E3779B97F4A7C15 + 1) & MASK)
        if last is not None:
            out.add(((last * 0x9E3779B97F4A7C15) ^ (value * 0xC2B2AE3D27D4EB4F) + 2) & MASK)
        last = value
    return array.array("q", sorted(out))


def _extract(job):
    path, low, high = job
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    rows = []
    for fid, blob in db.execute("SELECT function_id, text FROM code WHERE function_id >= ? AND function_id < ?", (low, high)):
        f = features(zlib.decompress(blob).decode("utf-8", "replace"))
        if len(f) >= 4:
            rows.append((fid, f.tobytes()))
    return rows


def extract(path, cache, jobs, log):
    """{function id: array of feature hashes}, computed in parallel and kept in a cache file
    next to the database (rebuilt when the database is newer)."""
    import os
    if cache.exists() and cache.stat().st_mtime >= os.path.getmtime(path):
        with open(cache, "rb") as f:
            return pickle.load(f)
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    top = db.execute("SELECT max(function_id) FROM code").fetchone()[0] or 0
    step = 4000
    work = [(path, low, low + step) for low in range(0, top + 1, step)]
    out, started = {}, time.time()
    with multiprocessing.Pool(jobs) as pool:
        for n, rows in enumerate(pool.imap_unordered(_extract, work), 1):
            for fid, raw in rows:
                a = array.array("q")
                a.frombytes(raw)
                out[fid] = a
            if n % 20 == 0:
                log(f"  features: {n}/{len(work)} blocks, {time.time() - started:.0f} s")
    with open(cache, "wb") as f:
        pickle.dump(out, f, protocol=pickle.HIGHEST_PROTOCOL)
    return out


def match(feats, toolkit_ids, game_ids, min_score, min_margin, min_weight, rare=40, log=lambda s: None):
    """[(toolkit id, game id, score, margin)] for the pairs that pass. See the module text."""
    df = collections.Counter()
    for fid in toolkit_ids:
        df.update(feats[fid])
    game_df = collections.Counter()
    for fid in game_ids:
        game_df.update(feats[fid])
    total = len(toolkit_ids) + len(game_ids)
    weight = {}
    for t in set(df) | set(game_df):
        weight[t] = math.log(total / (df.get(t, 0) + game_df.get(t, 0)))
    index = collections.defaultdict(list)
    for fid in game_ids:
        for t in feats[fid]:
            if game_df[t] <= rare:
                index[t].append(fid)
    mass = {fid: sum(weight[t] for t in feats[fid]) for fid in list(toolkit_ids) + list(game_ids)}
    sets = {fid: frozenset(feats[fid]) for fid in game_ids}
    best_for_game = {}                            # game id -> (score, toolkit id)
    proposals = []
    started = time.time()
    for n, tid in enumerate(toolkit_ids, 1):
        if mass[tid] < min_weight:
            continue
        mine = feats[tid]
        seen = collections.Counter()
        for t in mine:
            if t in index and df[t] <= rare:
                for gid in index[t]:
                    seen[gid] += weight[t]
        if not seen:
            continue
        own = set(mine)
        scored = []
        for gid, _ in seen.most_common(12):
            shared = sum(weight[t] for t in own & sets[gid])
            scored.append((shared / (mass[tid] + mass[gid] - shared), gid))
        scored.sort(reverse=True)
        score, gid = scored[0]
        margin = score - (scored[1][0] if len(scored) > 1 else 0.0)
        proposals.append((tid, gid, score, margin))
        if gid not in best_for_game or score > best_for_game[gid][0]:
            best_for_game[gid] = (score, tid)
        if n % 50000 == 0:
            log(f"  matching: {n}/{len(toolkit_ids)} Toolkit functions, {time.time() - started:.0f} s")
    out = []
    for tid, gid, score, margin in proposals:
        if score >= min_score and margin >= min_margin and best_for_game[gid][0] <= score + 1e-9:
            out.append((tid, gid, score, margin))
    return out


if __name__ == "__main__":
    sys.exit("run it as:  python decomp.py match")
