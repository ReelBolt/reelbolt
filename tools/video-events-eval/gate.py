#!/usr/bin/env python3
"""Regression gate for the layer-1 event detector.

Reads a score.py JSON (typed rows at +/-2 frames) and exits non-zero when any typed precision/recall falls below the
floor in gate.json: overall, per video type, per event type. Also gates the untyped overall F1.

  gate.py [--results results/synthetic/events.json] [--gate gate.json]
  gate.py --record results/synthetic/events.json     # rewrite gate.json: floor = achieved - 0.05 (never below 0)

A floor is only recorded for a number that exists (a type with no labels has no recall, one never predicted no
precision), so a missing number is never a failure. Stdlib only.
"""
import argparse, json, os, sys

MARGIN = 0.05
TOLERANCE = 2  # the gate is defined at +/-2 frames; a result scored at any other tolerance is refused
HERE = os.path.dirname(os.path.abspath(__file__))


def floors(row):
    out = {}
    for key, name in (("precision", "minPrecision"), ("recall", "minRecall")):
        v = row.get(key)
        if v is not None:
            out[name] = round(max(0.0, v - MARGIN), 2)
    return out


def record(results, path):
    if results.get("tolFrames") != TOLERANCE:
        raise SystemExit(f"refusing to record: results were scored at tolFrames={results.get('tolFrames')}, the gate is +/-{TOLERANCE}")
    typed = results["typed"]
    gate = {
        "note": "Floors are the typed precision/recall at +/-2 frames achieved on the 16 synthetic clips (incl. 2 speech, 2 music) minus 0.05; speech-only clips must predict 0 beats. "
                "Regenerate with: gate.py --record results/synthetic/events.json",
        "tolFrames": results["tolFrames"],
        "all": floors(typed["all"]),
        "byVideoType": {k: floors(v) for k, v in typed["byVideoType"].items()},
        "byType": {k: floors(v) for k, v in typed["byType"].items() if k != "(untyped)"},
        "maxBeatsOnNoBeatClips": 0,
        "untypedAllF1": {"minF1": round(max(0.0, (results["untyped"]["all"]["f1"] or 0.0) - MARGIN), 2)},
    }
    with open(path, "w") as f:
        json.dump(gate, f, indent=2)
        f.write("\n")
    print(f"recorded floors from {results['name']} into {path}")


def check(results, gate):
    typed = results["typed"]
    failures = []
    if results.get("tolFrames") != TOLERANCE:
        return [f"results scored at tolFrames={results.get('tolFrames')}, the gate only accepts +/-{TOLERANCE}"]

    def compare(scope, row, wanted):
        for name, key in (("minPrecision", "precision"), ("minRecall", "recall")):
            if name not in wanted:
                continue
            v = row.get(key) if row else None
            if v is None:
                failures.append(f"{scope}: {key} is n/a, floor {wanted[name]:.2f}")
            elif v + 1e-9 < wanted[name]:
                failures.append(f"{scope}: {key} {v:.2f} < floor {wanted[name]:.2f}")

    compare("typed ALL", typed["all"], gate.get("all", {}))
    for k, w in gate.get("byVideoType", {}).items():
        compare(f"typed video {k}", typed["byVideoType"].get(k), w)
    for k, w in gate.get("byType", {}).items():
        compare(f"typed type {k}", typed["byType"].get(k), w)
    f1 = results["untyped"]["all"].get("f1")
    want = gate.get("untypedAllF1", {}).get("minF1")
    if want is not None and (f1 is None or f1 + 1e-9 < want):
        failures.append(f"untyped ALL: f1 {f1} < floor {want:.2f}")
    limit = gate.get("maxBeatsOnNoBeatClips")
    if limit is not None:
        for clip, n in sorted((results.get("noBeatClips") or {}).items()):
            if n > limit:
                failures.append(f"{clip}: {n} beats predicted on a speech-only clip, limit {limit}")
    return failures


def self_test():
    """Tamper check: a result scored at a looser tolerance must be refused, and one at the gate tolerance judged."""
    gate = {"all": {"minPrecision": 0.5}}
    row = {"typed": {"all": {"precision": 0.9}, "byVideoType": {}, "byType": {}}, "untyped": {"all": {"f1": 0.9}}}
    assert check({**row, "tolFrames": 5}, gate), "tolFrames 5 must be refused"
    assert check({**row, "tolFrames": 1}, gate), "tolFrames 1 must be refused"
    assert not check({**row, "tolFrames": TOLERANCE}, gate), "tolFrames 2 must pass"
    print("gate self-test: ok")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", default=os.path.join(HERE, "results/synthetic/events.json"))
    ap.add_argument("--gate", default=os.path.join(HERE, "gate.json"))
    ap.add_argument("--record", metavar="RESULTS_JSON")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if a.record:
        record(json.load(open(a.record)), a.gate)
        return 0
    failures = check(json.load(open(a.results)), json.load(open(a.gate)))
    if failures:
        print("event detector below its floors:")
        for f in failures:
            print("  " + f)
        return 1
    print("event detector gate: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
