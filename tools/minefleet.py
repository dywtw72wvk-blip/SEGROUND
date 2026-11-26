#!/usr/bin/env python3
"""Local-only one-paste mining fleet: FS Mining Carrier Atmo with 4 workshop miners docked on its pads.

  python3 tools/minefleet.py [path/to/miner/bp.sbc]

The miner is someone else's workshop blueprint, so it is never copied into this repo: this tool reads your
own subscribed copy (default: workshop item 2572218085, "[SCAM] - Min3r 69R Atmospheric", found through your
Steam library folders) and writes the combined blueprint straight to your local F10 folder
(%APPDATA%/SpaceEngineers/Blueprints/local/<name>/bp.sbc).

Each miner copy gets the Fleet System Drone script in its main PB (Role=Miner, picked because it has drills;
its other PB is left empty, so its original automation stays off), fresh entity ids, the player as owner of its terminal blocks (as tools/shipgen.py does:
blocks without an owner paste unowned), and its belly connector written as connected to its pad. The miner is
placed upright with its connector face DOCK_GAP above the pad's face; its bounding box (from the game's block
sizes) is checked against the carrier's blocks and the other miners.
"""
import hashlib
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import shipgen  # noqa: E402

MINER_ITEM = "2572218085"
MINER_CONNECTOR = "docka"            # the SCAM miner's belly connector
MINER_PB = "a-min3r"                 # the SCAM miner's main PB: gets the Drone script
NAME = "FS Mining Carrier Atmo + 4 SCAM Miners"
X = "{http://www.w3.org/2001/XMLSchema-instance}type"


def steam_libraries():
    roots = [pathlib.Path(r"C:\Program Files (x86)\Steam"), pathlib.Path(r"C:\Program Files\Steam")]
    libs = []
    for r in roots:
        vdf = r / "steamapps" / "libraryfolders.vdf"
        if vdf.is_file():
            libs += [pathlib.Path(p.replace("\\\\", "\\")) for p in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(errors="replace"))]
        libs.append(r)
    return [l for l in libs if l.is_dir()]


def find_game_and_miner():
    for lib in steam_libraries():
        data = lib / "steamapps" / "common" / "SpaceEngineers" / "Content" / "Data"
        miner = lib / "steamapps" / "workshop" / "content" / "244850" / MINER_ITEM / "bp.sbc"
        if data.is_dir():
            return data, (miner if miner.is_file() else None)
    return None, None


def block_sizes(data):
    sizes = {}
    for f in (data / "CubeBlocks").glob("*.sbc"):
        try:
            root = ET.parse(f).getroot()
        except ET.ParseError:
            continue
        for d in root.iter("Definition"):
            i, sz = d.find("Id"), d.find("Size")
            if i is None or sz is None:
                continue
            k = (i.findtext("TypeId") or "").replace("MyObjectBuilder_", "") + "/" + (i.findtext("SubtypeId") or "")
            sizes[k] = tuple(int(sz.get(a, 1)) for a in "xyz")
    return sizes


def miner_geometry(grid, sizes):
    """(connector face centre in grid-local metres, AABB lo/hi in metres, connector EntityId)"""
    lo, hi, face, cid = [1e9] * 3, [-1e9] * 3, None, None
    for b in grid.find("CubeBlocks"):
        t = (b.get(X) or "").replace("MyObjectBuilder_", ""); st = b.findtext("SubtypeName") or ""
        size = sizes.get(t + "/" + st, (1, 1, 1))
        o = b.find("BlockOrientation")
        fwd, up = (o.get("Forward", "Forward"), o.get("Up", "Up")) if o is not None else ("Forward", "Up")
        ext = shipgen.extents(size, fwd, up)
        mn = b.find("Min"); m0 = [int(mn.get(a, 0)) for a in "xyz"] if mn is not None else [0, 0, 0]
        for k in range(3):
            lo[k] = min(lo[k], (m0[k] - 0.5) * shipgen.SG); hi[k] = max(hi[k], (m0[k] + ext[k] - 0.5) * shipgen.SG)
        if b.findtext("CustomName") == MINER_CONNECTOR:
            f = shipgen.DIRS[fwd]
            centre = [(m0[k] + (ext[k] - 1) / 2) * shipgen.SG for k in range(3)]
            face = tuple(centre[k] + f[k] * ext[k] * shipgen.SG / 2 for k in range(3))
            cid = b.findtext("EntityId")
            assert f == (0, -1, 0), "the miner's connector must face down"
    assert face is not None, f"no connector named {MINER_CONNECTOR!r} in the miner"
    return face, lo, hi, cid


def miner_copy(raw, n, pos, pad_eid, conn_old_id, code):
    """One miner <CubeGrid> as text: fresh ids, owner, placement, docked connector."""
    ids = set(re.findall(r"<EntityId>(\d+)</EntityId>", raw))
    remap = {i: str(int(hashlib.sha1(f"{NAME}/{n}/{i}".encode()).hexdigest()[:15], 16)) for i in ids}
    g = re.sub(r"\b(\d{9,20})\b", lambda m: remap.get(m.group(1), m.group(1)), raw)
    f = lambda v: " ".join(f'{a}="{c:g}"' for a, c in zip("xyz", v))
    g = re.sub(r"<PositionAndOrientation>.*?</PositionAndOrientation>",
               f"<PositionAndOrientation><Position {f(pos)} /><Forward x=\"0\" y=\"0\" z=\"-1\" /><Up x=\"0\" y=\"1\" z=\"0\" />"
               "<Orientation><X>0</X><Y>0</Y><Z>0</Z><W>1</W></Orientation></PositionAndOrientation>", g, count=1, flags=re.S)
    g = re.sub(r"<DisplayName>.*?</DisplayName>", f"<DisplayName>SCAM Miner {n + 1}</DisplayName>", g, count=1, flags=re.S)
    # terminal blocks: owned by the player, like a game-saved blueprint
    if shipgen.OWNER_ID:
        def own(m):
            blk = m.group(0)
            if 'xsi:type="MyObjectBuilder_CubeBlock"' in blk or "<Owner>" in blk:
                return blk
            return re.sub(r"(<ColorMaskHSV[^>]*/>)", rf"\1<Owner>{shipgen.OWNER_ID}</Owner>", blk, count=1)
        g = re.sub(r"<MyObjectBuilder_CubeBlock .*?</MyObjectBuilder_CubeBlock>", own, g, flags=re.S)
    # the miner's PB runs the Fleet System Drone script (Role=Miner: it has drills); any other PB is
    # left empty, so the miner's original automation stays off
    def pb(m):
        blk = m.group(0)
        if 'xsi:type="MyObjectBuilder_MyProgrammableBlock"' not in blk:
            return blk
        blk = re.sub(r"<Program>.*?</Program>", "", blk, flags=re.S)
        if "<CustomName>" + MINER_PB + "</CustomName>" in blk:
            blk = blk.replace("<CustomName>" + MINER_PB + "</CustomName>", "<CustomName>Programmable Block [Fleet Drone] [Miner]</CustomName>")
            blk = blk.replace("</MyObjectBuilder_CubeBlock>", "<Program>" + escape(code) + "</Program></MyObjectBuilder_CubeBlock>")
        return blk
    g = re.sub(r"<MyObjectBuilder_CubeBlock .*?</MyObjectBuilder_CubeBlock>", pb, g, flags=re.S)
    assert "[Fleet Drone] [Miner]" in g, "miner PB not found"
    # belly connector: connected to its pad, slave side
    new_cid = remap[conn_old_id]
    def dock(m):
        blk = m.group(0)
        if f"<EntityId>{new_cid}</EntityId>" not in blk:
            return blk
        blk = re.sub(r"<ConnectedEntityId>\d+</ConnectedEntityId>", "", blk)
        return blk.replace("<IsMaster>true</IsMaster>", f"<ConnectedEntityId>{pad_eid}</ConnectedEntityId><IsMaster>false</IsMaster>")
    g = re.sub(r"<MyObjectBuilder_CubeBlock .*?</MyObjectBuilder_CubeBlock>", dock, g, flags=re.S)
    assert f"<ConnectedEntityId>{pad_eid}</ConnectedEntityId>" in g
    return g, new_cid


def main():
    data, miner_bp = find_game_and_miner()
    if len(sys.argv) > 1:
        miner_bp = pathlib.Path(sys.argv[1])
    if data is None or miner_bp is None or not miner_bp.is_file():
        sys.exit(f"Space Engineers or workshop item {MINER_ITEM} not found (subscribe to it, or pass the miner's bp.sbc)")
    sizes = block_sizes(data)
    text = miner_bp.read_text(encoding="utf-8")
    raw = re.search(r"<CubeGrid>.*?</CubeGrid>", text, re.S).group(0)
    grid = ET.fromstring(raw.replace("xsi:", "xsi_"))   # geometry only; namespace prefix made harmless
    for b in grid.iter("MyObjectBuilder_CubeBlock"):
        b.set(X, b.get("xsi_type", ""))
    face, lo, hi, cid = miner_geometry(grid, sizes)

    drone_code = (pathlib.Path(__file__).resolve().parent.parent / "dist" / "Drone.cs").read_text(encoding="utf-8").replace(chr(13) + chr(10), chr(10))
    m = shipgen.mining_carrier()
    pads = [(i, b) for i, b in enumerate(m.blocks) if b["key"] == "l_connector"]
    carrier_boxes = [(tuple(c[k] * shipgen.LG for k in range(3)), b["name"] or b["key"]) for b in m.blocks for c in b["cells"]]
    grids, c_extra, boxes = [], {}, []
    for n, (pi, pb) in enumerate(pads):
        cc = [pb["cells"][0][k] * shipgen.LG for k in range(3)]
        pad_face = (cc[0], cc[1] + shipgen.LG / 2, cc[2])
        pos = tuple(pad_face[k] - face[k] + (shipgen.DOCK_GAP if k == 1 else 0) for k in range(3))
        g, new_cid = miner_copy(raw, n, pos, m.eid("0", pi), cid, drone_code)
        c_extra[pi] = f"<ConnectedEntityId>{new_cid}</ConnectedEntityId><IsMaster>true</IsMaster>"
        grids.append(g)
        box = ([pos[k] + lo[k] for k in range(3)], [pos[k] + hi[k] for k in range(3)])
        for (c, who) in carrier_boxes:
            if who == pb["name"]:
                continue
            if all(box[0][k] < c[k] + shipgen.LG / 2 - 1e-6 and c[k] - shipgen.LG / 2 < box[1][k] - 1e-6 for k in range(3)):
                sys.exit(f"miner on {pb['name']} would intersect carrier block {who}")
        for (ob, oname) in boxes:
            if all(box[0][k] < ob[1][k] and ob[0][k] < box[1][k] for k in range(3)):
                sys.exit(f"miners on {pb['name']} and {oname} would intersect")
        boxes.append((box, pb["name"]))
    dist = pathlib.Path(__file__).resolve().parent.parent / "dist"
    code = {"MineCore": (dist / "MineCore.cs").read_text(encoding="utf-8").replace(chr(13) + chr(10), chr(10))}
    xml = shipgen.blueprint_xml(NAME, [m.grid_xml(code, "0", extra=c_extra)] + grids)
    out = pathlib.Path(os.environ.get("APPDATA", "")) / "SpaceEngineers" / "Blueprints" / "local" / NAME
    out.mkdir(parents=True, exist_ok=True)
    (out / "bp.sbc").write_text(xml, encoding="utf-8")
    b5 = out / "bp.sbcB5"
    if b5.exists():
        b5.unlink()
    print(f"wrote {out / 'bp.sbc'}: carrier + {len(grids)} miners from {miner_bp} (clearance checked)")


if __name__ == "__main__":
    main()
