"""Fit the solver's linear weights from logged player decisions.

Each decision gives one inequality: the row the player picked must outrank the others.
We minimise a hinge loss over those constraints with an L2 pull towards the current
weights, so the result stays close to the hand-tuned scoring unless the data insists.

Usage:
    python fit-weights.py <log-dir> [--holdout 0.2] [--iters 4000] [--lr 0.2] [--margin 0.3]
"""

import argparse
import os
import re
import sys

import numpy as np

DECIDE_RE = re.compile(r"S DECIDE round=(?P<round>\d+) turn=(?P<turn>\d+) actor=(?P<actor>.+?)(?: choices=|$)")
FEAT_RE = re.compile(
    r"S FEAT (?P<skill>[^|]+)\|(?P<target>[^|]+)\|rank=(?P<rank>\d+)"
    r"\|dmg=(?P<dmg>-?[\d.]+)\|hp=(?P<hp>-?[\d.]+)\|kill=(?P<kill>-?[\d.]+)"
    r"\|eff=(?P<eff>-?[\d.]+)\|pos=(?P<pos>-?[\d.]+)\|retal=(?P<retal>-?[\d.]+)"
    r"\|score=(?P<score>-?[\d.]+)"
)
ACTUAL_RE = re.compile(r"S ACTUAL skill=(?P<skill>\S+)")

FEATURES = ["hp", "kill", "eff", "pos", "retal", "tgtHp", "threat", "allyDd"]
PRIOR = np.array([1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0])


def parse_feat(line):
    """Parses "S FEAT skill|target|k=v|..." into a row; unknown keys are ignored."""
    try:
        body = line.split("S FEAT ", 1)[1].strip()
        parts = body.split("|")
        if len(parts) < 3:
            return None
        skill, target = parts[0], parts[1]
        kv = {}
        for token in parts[2:]:
            if "=" in token:
                k, v = token.split("=", 1)
                kv[k] = v
        feat = [float(kv.get(name, 0.0)) for name in FEATURES]
        return {
            "skill": skill,
            "target": target,
            "rank": int(float(kv.get("rank", 0))),
            "feat": feat,
        }
    except Exception:
        return None

def parse_file(path):
    """Returns a list of decisions: (actor, chosen_skill, rows) with rows as dicts."""
    decisions = []
    block = None
    for line in open(path, encoding="utf-8", errors="ignore"):
        if "S DECIDE" in line:
            m = DECIDE_RE.search(line)
            block = {
                "actor": m.group("actor").strip() if m else "?",
                "round": int(m.group("round")) if m else -1,
                "turn": int(m.group("turn")) if m else -1,
                "excluded": "excluded=1" in line,
                "file": path,
                "rows": [],
            }
            continue
        if "S FEAT " in line and block is not None:
            parsed = parse_feat(line)
            if parsed is not None:
                block["rows"].append(parsed)
            continue
        if "S ACTUAL" in line and block is not None:
            m = ACTUAL_RE.search(line)
            if m:
                block["chosen"] = m.group("skill")
                decisions.append(block)
            block = None
    return decisions


def dedupe(decisions):
    """The same (turn, actor) can be logged repeatedly: keep the richest block."""
    best = {}
    for d in decisions:
        key = (d.get("file"), d.get("round"), d.get("turn"), d.get("actor"))
        cur = best.get(key)
        if cur is None or len(d["rows"]) > len(cur["rows"]):
            best[key] = d
    return list(best.values())


def usable(decision):
    if decision.get("excluded") or len(decision["rows"]) < 2:
        return False
    if "chosen" not in decision:
        return False
    return any(r["skill"] == decision["chosen"] for r in decision["rows"])


def to_arrays(decision, drop_retal=True):
    rows = decision["rows"]
    X = np.array([r["feat"] for r in rows], dtype=float)
    if drop_retal:
        # retal is only logged for the row that got the lookahead, so it is not comparable
        # across rows; keeping it would bias the fit against the top row.
        X[:, 4] = 0.0
    chosen = [i for i, r in enumerate(rows) if r["skill"] == decision["chosen"]]
    if not chosen:
        return None
    return X, chosen[0]


def ranks(decisions, w, drop_retal=True, scale=None):
    top1 = top3 = covered = 0
    for d in decisions:
        arr = to_arrays(d, drop_retal)
        if arr is None:
            continue
        X, chosen = arr
        if scale is not None:
            X = (X - scale[0]) / scale[1]
        scores = X @ w
        order = np.argsort(-scores)
        rank = int(np.where(order == chosen)[0][0]) + 1
        covered += 1
        top1 += rank == 1
        top3 += rank <= 3
    if covered == 0:
        return 0.0, 0.0, 0
    return top1 / covered, top3 / covered, covered


def fit(decisions, iters, lr, margin, l2, drop_retal=True, scale=None):
    w = PRIOR.copy()
    hist = []
    for it in range(iters):
        grad = 2.0 * l2 * (w - PRIOR)
        loss = 0.0
        for d in decisions:
            arr = to_arrays(d, drop_retal)
            if arr is None:
                continue
            X, chosen = arr
            if scale is not None:
                X = (X - scale[0]) / scale[1]
            scores = X @ w
            # every other row must sit at least `margin` below the chosen one
            gaps = scores[chosen] - scores
            viol = margin - gaps
            viol[chosen] = -1.0
            bad = np.where(viol > 0)[0]
            for j in bad:
                loss += viol[j]
                grad -= X[chosen] - X[j]
        w -= lr * grad / max(1, len(decisions))
        if it % 200 == 0:
            hist.append((it, loss, tuple(np.round(w, 3))))
    return w, hist


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("logdir")
    ap.add_argument("--holdout", type=float, default=0.2)
    ap.add_argument("--iters", type=int, default=3000)
    ap.add_argument("--lr", type=float, default=0.2)
    ap.add_argument("--margin", type=float, default=0.3)
    ap.add_argument("--l2", type=float, default=0.05)
    args = ap.parse_args()

    files = sorted(
        os.path.join(args.logdir, f)
        for f in os.listdir(args.logdir)
        if f.startswith("journey-") and f.endswith(".log")
    )
    all_decisions = []
    for path in files:
        all_decisions.extend(parse_file(path))

    before = len(all_decisions)
    all_decisions = dedupe(all_decisions)
    print(f"after dedupe: {len(all_decisions)} (removed {before - len(all_decisions)} duplicate blocks)")

    usable_decisions = [d for d in all_decisions if usable(d)]
    print(f"files        : {len(files)}")
    print(f"decisions    : {len(all_decisions)}")
    print(f"usable       : {len(usable_decisions)} "
          f"({len(usable_decisions) / max(1, len(all_decisions)):.1%})")

    if len(usable_decisions) < 50:
        print("not enough usable decisions to fit; collect more data first")
        return 2

    # split by battle file so near-duplicate decisions cannot leak into the holdout
    files_all = sorted({d.get("file") for d in usable_decisions})
    rng = np.random.default_rng(0)
    shuffled = list(files_all)
    rng.shuffle(shuffled)
    cut_files = int(len(shuffled) * (1.0 - args.holdout))
    train_files = set(shuffled[:cut_files])
    train = [d for d in usable_decisions if d.get("file") in train_files]
    test = [d for d in usable_decisions if d.get("file") not in train_files]

    t1, t3, n = ranks(usable_decisions, PRIOR, scale=None)
    print(f"\nprior weights {PRIOR.tolist()}")
    print(f"  all data   top1={t1:.1%} top3={t3:.1%} n={n}")

    # standardise so features on different scales do not drown each other out
    stacked = np.vstack([to_arrays(d)[0] for d in train if to_arrays(d) is not None])
    mean = stacked.mean(axis=0)
    std = stacked.std(axis=0)
    std[std < 1e-6] = 1.0
    scale = (mean, std)

    w_scaled, hist = fit(train, args.iters, args.lr, args.margin, args.l2, scale=scale)
    w = np.zeros_like(w_scaled)
    nz = std > 1e-6
    w[nz] = w_scaled[nz] / std[nz]
    print(f"\nfitted weights {np.round(w, 3).tolist()}")

    t1, t3, n = ranks(train, w, scale=scale)
    print(f"  train      top1={t1:.1%} top3={t3:.1%} n={n}")
    t1, t3, n = ranks(test, w, scale=scale)
    print(f"  holdout    top1={t1:.1%} top3={t3:.1%} n={n}")
    t1, t3, n = ranks(test, PRIOR, scale=scale)
    print(f"  holdout(prior) top1={t1:.1%} top3={t3:.1%} n={n}")
    return 0


if __name__ == "__main__":
    sys.exit(main())