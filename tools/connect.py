"""Structural connectivity check for blueprints, from the game's block MountPoints.

  python connect.py <Content/Data dir> bp.sbc [bp.sbc ...]

Two blocks are joined when mount-point rectangles on a shared face overlap (and their
exclusion masks allow it). Every grid should be one connected piece; anything else falls off
the first time the game re-checks the grid (a block destroyed or removed)."""
import sys, os, glob
import xml.etree.ElementTree as ET
from collections import defaultdict, deque

XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
DIRS = {"Forward": (0, 0, -1), "Backward": (0, 0, 1), "Left": (-1, 0, 0),
        "Right": (1, 0, 0), "Up": (0, 1, 0), "Down": (0, -1, 0)}
SIDES = ("Front", "Back", "Left", "Right", "Top", "Bottom")


def load_defs(data):
    defs = {}
    mods = os.path.join(data, "..", "..", "..", "..", "workshop", "content", "244850")
    files = glob.glob(os.path.join(data, "**", "*.sbc"), recursive=True) + sorted(glob.glob(os.path.join(mods, "*", "Data", "**", "*.sbc"), recursive=True))
    for f in files:
        try:
            text = open(f, encoding="utf-8-sig", errors="replace").read()
        except OSError:
            continue
        if "<CubeBlocks" not in text:
            continue
        try:
            root = ET.fromstring(text)
        except ET.ParseError:
            continue
        for d in root.iter("Definition"):
            i, sz = d.find("Id"), d.find("Size")
            if i is None or sz is None:
                continue
            t = (i.findtext("TypeId") or i.get("Type") or "").replace("MyObjectBuilder_", "").strip()
            s = (i.findtext("SubtypeId") or i.get("Subtype") or "").strip()
            k = t + "/" + s
            if "workshop" in f and k in defs:
                continue
            size = tuple(int(sz.get(a, 1)) for a in "xyz")
            mp = d.find("MountPoints")
            mps = None
            if mp is not None and len(mp):
                mps = []
                for m in mp:
                    if m.get("Enabled", "true").lower() == "false":
                        continue
                    mps.append((m.get("Side"), float(m.get("StartX")), float(m.get("StartY")), float(m.get("EndX")), float(m.get("EndY")),
                                int(m.get("ExclusionMask", 0)), int(m.get("PropertiesMask", 0))))
            defs[k] = (size, mps)
    return defs


def face_point(side, X, Y, s):
    """block-local corner coordinates (x right, y up, z back) of face point (X, Y)"""
    sx, sy, sz = s
    return {"Front": (X, Y, 0), "Back": (X, Y, sz), "Left": (0, Y, X), "Right": (sx, Y, sz - X),
            "Top": (X, sy, sz - Y), "Bottom": (X, 0, Y)}[side]


SIDE_NORMAL = {"Front": (0, 0, -1), "Back": (0, 0, 1), "Left": (-1, 0, 0), "Right": (1, 0, 0), "Top": (0, 1, 0), "Bottom": (0, -1, 0)}


def full_mps(s):
    sx, sy, sz = s
    dims = {"Front": (sx, sy), "Back": (sx, sy), "Left": (sz, sy), "Right": (sz, sy), "Top": (sx, sz), "Bottom": (sx, sz)}
    return [(side, 0, 0, dims[side][0], dims[side][1], 0, 0) for side in SIDES]


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def check(bp, defs, verbose=True):
    root = ET.parse(bp).getroot()
    bad_total = 0
    for gi, grid in enumerate(root.iter("CubeGrid")):
        cb = grid.find("CubeBlocks")
        blocks = list(cb) if cb is not None else []
        rects = defaultdict(list)     # (axis, coord) -> [(block, sign, lo(2), hi(2), excl, prop)]
        names, unknown = [], 0
        for bi, b in enumerate(blocks):
            t = (b.get(XSI) or "CubeBlock").replace("MyObjectBuilder_", "")
            st = (b.findtext("SubtypeName") or "").strip()
            names.append(f"{b.findtext('CustomName') or st} [{t}/{st}]")
            d = defs.get(t + "/" + st)
            if d is None:
                unknown += 1
                continue
            size, mps = d
            o = b.find("BlockOrientation")
            f = DIRS[o.get("Forward", "Forward")] if o is not None else DIRS["Forward"]
            u = DIRS[o.get("Up", "Up")] if o is not None else DIRS["Up"]
            r = cross(f, u)
            axes = (r, u, tuple(-c for c in f))           # world direction of local x, y, z
            mn = b.find("Min")
            m0 = tuple(int(mn.get(a, 0)) for a in "xyz") if mn is not None else (0, 0, 0)
            def to_world(p):
                w = [float(m0[i]) for i in range(3)]
                for li in range(3):
                    a = axes[li]
                    for wi in range(3):
                        if a[wi] > 0: w[wi] += p[li]
                        elif a[wi] < 0: w[wi] += size[li] - p[li]
                return w
            for (side, x0, y0, x1, y1, ex, pr) in (mps if mps is not None else full_mps(size)):
                p0, p1 = to_world(face_point(side, x0, y0, size)), to_world(face_point(side, x1, y1, size))
                n = SIDE_NORMAL[side]
                nw = [sum(axes[li][wi] * n[li] for li in range(3)) for wi in range(3)]
                ax = next(i for i in range(3) if nw[i] != 0)
                if abs(p0[ax] - p1[ax]) > 1e-6:
                    continue
                o2 = [i for i in range(3) if i != ax]
                lo = tuple(min(p0[i], p1[i]) for i in o2); hi = tuple(max(p0[i], p1[i]) for i in o2)
                rects[(ax, round(p0[ax], 4))].append((bi, nw[ax], lo, hi, ex, pr))
        adj = defaultdict(set)
        for key, lst in rects.items():
            pos = [x for x in lst if x[1] > 0]; neg = [x for x in lst if x[1] < 0]
            for a in pos:
                for c in neg:
                    if a[0] == c[0]:
                        continue
                    if (a[4] & c[5]) or (c[4] & a[5]):
                        continue
                    w = min(a[3][0], c[3][0]) - max(a[2][0], c[2][0]); h = min(a[3][1], c[3][1]) - max(a[2][1], c[2][1])
                    if w > 1e-3 and h > 1e-3:
                        adj[a[0]].add(c[0]); adj[c[0]].add(a[0])
        seen, comps = set(), []
        for s in range(len(blocks)):
            if s in seen:
                continue
            comp, q = [], deque([s]); seen.add(s)
            while q:
                x = q.popleft(); comp.append(x)
                for y in adj[x]:
                    if y not in seen:
                        seen.add(y); q.append(y)
            comps.append(comp)
        comps.sort(key=len, reverse=True)
        loose = sum(len(c) for c in comps[1:])
        bad_total += loose
        if verbose or loose:
            print(f"  grid {gi} '{grid.findtext('DisplayName')}': {len(blocks)} blocks, {len(comps)} piece(s), {loose} not attached to the main body" + (f", {unknown} unknown" if unknown else ""))
            for c in comps[1:12]:
                print("    - loose:", ", ".join(names[i] for i in c[:6]) + (" ..." if len(c) > 6 else ""))
    return bad_total


if __name__ == "__main__":
    defs = load_defs(sys.argv[1])
    tot = 0
    for bp in sys.argv[2:]:
        print("==", os.path.basename(os.path.dirname(bp)))
        tot += check(bp, defs)
    print("LOOSE BLOCKS TOTAL:", tot)
