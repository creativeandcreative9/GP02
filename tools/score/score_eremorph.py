"""20261011_EreMorph比較の採点表.md の表を読んで、機能・UI ごとの合計点を出す。

使い方: python3 tools/score/score_eremorph.py
表の行は「| 項目 | 重み | EreMorph | GP02 | ... |」の形。点は 0〜3。
"""
import pathlib
import re
import sys

doc = pathlib.Path(__file__).resolve().parents[2] / "20261011_EreMorph比較の採点表.md"
section = None
totals = {}
for line in doc.read_text(encoding="utf-8").splitlines():
    m = re.match(r"^### (機能|UI)\s*$", line)
    if m:
        section = m.group(1)
        continue
    if section is None or not line.startswith("| ") or line.startswith("| 項目"):
        continue
    cells = [c.strip() for c in line.strip("|").split("|")]
    try:
        weight, ere, gp = int(cells[1]), int(cells[2]), int(cells[3])
    except (ValueError, IndexError):
        continue
    t = totals.setdefault(section, [0, 0, 0])
    t[0] += weight * ere
    t[1] += weight * gp
    t[2] += weight * 3

ok = True
for kind in ("機能", "UI"):
    ere, gp, full = totals[kind]
    mark = "上回る" if gp > ere else "届かない"
    ok &= gp > ere
    print(f"{kind}: EreMorph {ere}/{full} ({100*ere/full:.0f}%)  GP02 {gp}/{full} ({100*gp/full:.0f}%)  → {mark}")
sys.exit(0 if ok else 1)
