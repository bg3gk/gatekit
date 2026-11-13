#!/usr/bin/env python3
"""Pairs the functions of two builds of the same source made by different compilers (the macOS
build, which has names, and the Windows executable, which has none) by walking both call graphs
from pairs that are certain.

Constants alone do not carry across compilers well enough: structure layouts differ between the
two platforms and each compiler inlines differently. What does carry is the shape of the program:

1. Seeds. A text constant that exactly one function refers to on each side ties those two
   functions together (kept when no other text contradicts it). Functions whose name is already
   known on the Windows side (from the Toolkit) are tied to the macOS function of the same name.
2. Walk. For a matched pair, the still unmatched functions the two call are candidates for each
   other, and so are the functions that call them. Inside such a small set the constants are
   enough to tell candidates apart: the pair with the best similarity wins if it leads clearly
   (or if it is the only candidate on both sides). A pair is accepted when nothing else claims
   either of its functions. Newly accepted pairs are walked in the next round.

Every pair keeps how it was found. Nothing here is proof; `check` in the driver measures the
method on names known independently.
"""
import collections
import math


def weights(features_a, features_b):
    df = collections.Counter()
    for side in (features_a, features_b):
        for f in side.values():
            df.update(f)
    total = len(features_a) + len(features_b)
    return {t: math.log(total / n) for t, n in df.items()}


def similarity(a, b, weight):
    """Weighted Jaccard of two feature sets; 0 when either is empty."""
    if not a or not b:
        return 0.0
    shared = sum(weight[t] for t in a & b)
    if not shared:
        return 0.0
    return shared / (sum(weight[t] for t in a) + sum(weight[t] for t in b) - shared)


def text_seeds(texts_a, texts_b):
    """{a: b} for functions tied by a text only they refer to. texts_x: {function: [text, ...]}."""
    users_a, users_b = collections.defaultdict(set), collections.defaultdict(set)
    for f, texts in texts_a.items():
        for t in set(texts):
            users_a[t].add(f)
    for f, texts in texts_b.items():
        for t in set(texts):
            users_b[t].add(f)
    votes = collections.Counter()
    for t, fa in users_a.items():
        fb = users_b.get(t)
        if fb and len(fa) == 1 and len(fb) == 1:
            votes[(next(iter(fa)), next(iter(fb)))] += 1
    left, right = collections.Counter(p[0] for p in votes), collections.Counter(p[1] for p in votes)
    return {a: b for (a, b), n in votes.items() if left[a] == 1 and right[b] == 1}, votes


def walk(seeds, calls_a, calls_b, feats_a, feats_b, weight, min_score=0.25, min_lead=0.10, rounds=60, log=lambda s: None):
    """Extends {a: b} along calls and callers. Returns {a: (b, how, score)} for the new pairs."""
    callers_a, callers_b = collections.defaultdict(set), collections.defaultdict(set)
    for f, targets in calls_a.items():
        for t in targets:
            callers_a[t].add(f)
    for f, targets in calls_b.items():
        for t in targets:
            callers_b[t].add(f)
    forward = dict(seeds)
    backward = {b: a for a, b in seeds.items()}
    sets_a = {f: frozenset(v) for f, v in feats_a.items()}
    sets_b = {f: frozenset(v) for f, v in feats_b.items()}
    found = {}
    frontier = list(seeds.items())
    waiting = set()
    for round_ in range(rounds):
        votes = collections.defaultdict(float)
        how, parents = {}, collections.defaultdict(set)
        for a, b in set(frontier) | waiting:
            for label, na, nb in (("called from", calls_a.get(a, ()), calls_b.get(b, ())),
                                  ("caller of", callers_a.get(a, ()), callers_b.get(b, ()))):
                ca = [x for x in set(na) if x not in forward]
                cb = [y for y in set(nb) if y not in backward]
                if not ca or not cb or len(ca) > 60 or len(cb) > 60:
                    continue
                if len(ca) == 1 and len(cb) == 1:
                    x, y = ca[0], cb[0]
                    score = similarity(sets_a.get(x), sets_b.get(y), weight)
                    # the only candidates on both sides: accepted unless the constants disagree
                    if score >= 0.05 or not sets_a.get(x) or not sets_b.get(y):
                        votes[(x, y)] += max(score, 0.05)
                        how.setdefault((x, y), label)
                        parents[(x, y)].add((a, b))
                    continue
                for x in ca:
                    fx = sets_a.get(x)
                    if not fx:
                        continue
                    scored = sorted(((similarity(fx, sets_b.get(y), weight), y) for y in cb), reverse=True)
                    best, y = scored[0]
                    lead = best - (scored[1][0] if len(scored) > 1 else 0.0)
                    if best < min_score or lead < min_lead:
                        continue
                    # and x must be y's best among this side's candidates too
                    back = max(similarity(sets_a.get(x2), sets_b.get(y), weight) for x2 in ca)
                    if back > best + 1e-9:
                        continue
                    votes[(x, y)] += best
                    how.setdefault((x, y), label)
                    parents[(x, y)].add((a, b))
        best_left, best_right = collections.defaultdict(float), collections.defaultdict(float)
        count_left, count_right = collections.Counter(), collections.Counter()
        for (x, y), v in votes.items():
            best_left[x] = max(best_left[x], v)
            best_right[y] = max(best_right[y], v)
        for (x, y), v in votes.items():
            if v >= best_left[x] - 1e-9:
                count_left[x] += 1
            if v >= best_right[y] - 1e-9:
                count_right[y] += 1
        frontier, waiting = [], set()
        for (x, y), v in votes.items():
            if x in forward or y in backward:
                continue
            if v >= best_left[x] - 1e-9 and v >= best_right[y] - 1e-9 and count_left[x] == 1 and count_right[y] == 1:
                forward[x], backward[y] = y, x
                found[x] = (y, how[(x, y)], v)
                frontier.append((x, y))
            else:
                waiting |= parents[(x, y)]
        log(f"  round {round_ + 1}: {len(frontier)} new pairs, {len(found)} so far")
        if not frontier:
            break
    return found
