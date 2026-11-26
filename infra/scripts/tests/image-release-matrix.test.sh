#!/usr/bin/env bash
# Drift guard: image-release.yml must publish the same images, built the same way, as ci.yml's
# docker-publish job. YAML anchors cannot cross files, so the matrix and steps are duplicated;
# this test parses both workflows and fails when they differ.
#
# Compared: every matrix entry (name, context, file, target, platforms, build_args, build_contexts);
# the publish steps by name, in order (uses, if, run, with); the release tags (version and sha only,
# never latest or a branch tag). Allowed to differ: the "Image version" and "Image metadata" steps
# (the release derives the version from the tag) and the `tags` input of the build steps.
set -euo pipefail
cd "$(dirname "$0")/../../.."

python3 -I - <<'PY'
import sys
import yaml

def load(path):
    with open(path) as f:
        return yaml.safe_load(f)

ci = load(".github/workflows/ci.yml")["jobs"]["docker-publish"]
rel = load(".github/workflows/image-release.yml")["jobs"]["publish"]
errors = []

KEYS = ("name", "context", "file", "target", "platforms", "build_args", "build_contexts")

def norm(entry):
    return {k: entry.get(k) for k in KEYS}

ci_m = [norm(e) for e in ci["strategy"]["matrix"]["include"]]
rel_m = [norm(e) for e in rel["strategy"]["matrix"]["include"]]
if ci_m != rel_m:
    ci_names = {e["name"]: e for e in ci_m}
    rel_names = {e["name"]: e for e in rel_m}
    for n in sorted(set(ci_names) ^ set(rel_names)):
        errors.append(f"matrix entry {n!r} exists in only one workflow")
    for n in sorted(set(ci_names) & set(rel_names)):
        for k in KEYS:
            if ci_names[n][k] != rel_names[n][k]:
                errors.append(f"matrix {n}: {k} differs: ci={ci_names[n][k]!r} release={rel_names[n][k]!r}")
    if not errors:
        errors.append("matrix entries are in a different order")

SKIP = {"Image version", "Image metadata"}

def label(s):
    return s.get("name") or s.get("uses")

ci_steps = [s for s in ci["steps"] if s.get("name") not in SKIP]
rel_steps = [s for s in rel["steps"] if s.get("name") not in SKIP]
if [label(s) for s in ci_steps] != [label(s) for s in rel_steps]:
    errors.append("publish step lists differ: ci=%r release=%r" % ([label(s) for s in ci_steps], [label(s) for s in rel_steps]))
else:
    for a, b in zip(ci_steps, rel_steps):
        for k in ("uses", "if", "run", "env", "id"):
            if a.get(k) != b.get(k):
                errors.append(f"step {label(a)!r}: {k} differs: ci={a.get(k)!r} release={b.get(k)!r}")
        wa, wb = dict(a.get("with") or {}), dict(b.get("with") or {})
        for drop in ("tags", "cache-from", "cache-to"):  # release builds use their own cache scope
            wa.pop(drop, None)
            wb.pop(drop, None)
        if wa != wb:
            errors.append(f"step {label(a)!r}: with differs: ci={wa!r} release={wb!r}")

# The release image tags: version and sha, nothing else.
meta = next(s for s in rel["steps"] if s.get("name") == "Image metadata")["run"]
tag_lines = [l.strip() for l in meta.split("__TAGS__")[1].splitlines() if l.strip().startswith('echo "${IMAGE}')]
if tag_lines != ['echo "${IMAGE}:${VERSION_OUT}"', 'echo "${IMAGE}:${GITHUB_SHA}"']:
    errors.append(f"release tags must be exactly :version and :sha, got {tag_lines!r}")
text = open(".github/workflows/image-release.yml").read()
for bad in (":latest", ":master", ":main"):
    if bad in text.replace("# ", "#"):
        errors.append(f"image-release.yml must not contain {bad}")

on = load(".github/workflows/image-release.yml").get(True) or {}
if set(on) != {"push"} or set(on["push"]) != {"tags"}:
    errors.append(f"image-release.yml must trigger on tag pushes only, got {on!r}")

rel_wf = load(".github/workflows/image-release.yml")
tags = (rel_wf.get(True) or rel_wf.get("on") or {}).get("push", {}).get("tags")
if tags != ["v[0-9]+.[0-9]+.[0-9]+*"]:
    errors.append(f"tag trigger must be exactly v[0-9]+.[0-9]+.[0-9]+*, got {tags!r}")
if rel_wf["jobs"]["publish"].get("permissions") != {"contents": "read", "packages": "write", "id-token": "write"}:
    errors.append("publish job permissions drifted")
if rel_wf["jobs"]["publish"].get("environment") != "release":
    errors.append("publish job must use the release environment")

if errors:
    print("image-release matrix drift:")
    for e in errors:
        print("  - " + e)
    sys.exit(1)
print("ok: image-release.yml matches ci.yml docker-publish (%d images, %d steps)" % (len(rel_m), len(rel_steps)))
PY
