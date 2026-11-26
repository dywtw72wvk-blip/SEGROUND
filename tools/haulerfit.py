#!/usr/bin/env python3
"""Local-only FS hauler: the ALZ Hezen Cargo Transporter fitted for the FS Mining hauler role.

  python3 tools/haulerfit.py [path/to/hezen/bp.sbc]

The Hezen is someone else's workshop blueprint (workshop item 3506660765), so it is never copied into
this repo: this tool reads your own saved copy (Steam Cloud "ALZ - Hezen Cargo Transporter", else the
workshop download) and writes the fitted ship straight to your local F10 folder as "FS Hauler (ALZ Hezen)".

What it changes (everything else is left exactly as the author built it):
  - belly connector: the magnetic plate under the centre of Cargo 2 (main grid cell 0, 4, -7) becomes a
    large connector facing down, "Connector [Dock] Belly". Its conveyor port meets the bottom of Cargo 2
    (the containers are already linked through their face centres), and its Up points at the nose, so
    the hauler docks on the carrier's flank port parallel to the carrier, nose to its bow
  - script: the spare, switched-off "Programmable Block" on the bridge gets the Fleet System Drone
    script, renamed "Programmable Block [Fleet Drone] [Hauler]" (the name picks Role=Hauler)
  - flight scripts that would fight ours are switched off: [FSD], Subgrid Gyros, Cruise Control
    (their code is left in place; the display, radar and airlock scripts keep running)
  - "Remote Control Main" is tagged [Fleet], so the script flies by it, not by the docking remote
"""
import gzip
import hashlib
import os
import pathlib
import re
import sys
from xml.sax.saxutils import escape

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import shipgen  # noqa: E402

NAME = "FS Hauler (ALZ Hezen)"
SAVED = "ALZ - Hezen Cargo Transporter"
WORKSHOP_ITEM = "3506660765"
PLATE_MIN = '<Min x="0" y="4" z="-7" />'
OFF = ("[FSD]", "Subgrid Gyros", "Cruise Control")


def find_source():
    roots = [pathlib.Path(r"C:\Program Files (x86)\Steam"), pathlib.Path(r"C:\Program Files\Steam")]
    for r in roots:
        for f in (r / "userdata").glob(f"*/244850/remote/Blueprints/cloud/{SAVED}/bp.sbc"):
            return f
    local = pathlib.Path(os.environ.get("APPDATA", "")) / "SpaceEngineers" / "Blueprints" / "local" / SAVED / "bp.sbc"
    if local.is_file():
        return local
    import minefleet
    for lib in minefleet.steam_libraries():
        f = lib / "steamapps" / "workshop" / "content" / "244850" / WORKSHOP_ITEM / "bp.sbc"
        if f.is_file():
            return f
    return None


def read(path):
    data = path.read_bytes()
    return (gzip.decompress(data) if data[:2] == b"\x1f\x8b" else data).decode("utf-8")


def blocks(text):
    return re.finditer(r"<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_(\w+)\">.*?</MyObjectBuilder_CubeBlock>", text, re.S)


def main():
    src = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else find_source()
    if src is None or not src.is_file():
        sys.exit(f"'{SAVED}' not found (save it from the game, or pass its bp.sbc)")
    text = read(src)
    drone = (pathlib.Path(__file__).resolve().parent.parent / "dist" / "Drone.cs").read_text(encoding="utf-8").replace(chr(13) + chr(10), chr(10))
    first = text.index("<CubeGrid>"); end0 = text.index("</CubeGrid>", first)
    grid0 = text[first:end0]

    # 1. belly connector in place of the magnetic plate under Cargo 2 (main grid only)
    plate = None
    for m in blocks(grid0):
        if PLATE_MIN in m.group(0) and m.group(1) == "LandingGear":
            plate = m
    if plate is None:
        sys.exit("magnetic plate under Cargo 2 not found: is this the ALZ Hezen Cargo Transporter?")
    pb = plate.group(0)
    color = re.search(r"<ColorMaskHSV[^>]*/>", pb)
    owner = re.search(r"<Owner>\d+</Owner>", pb)
    eid = int(hashlib.sha1(f"{NAME}/belly".encode()).hexdigest()[:15], 16)
    conn = ('<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_ShipConnector">'
            f"<SubtypeName>Connector</SubtypeName><EntityId>{eid}</EntityId>{PLATE_MIN}"
            '<BlockOrientation Forward="Down" Up="Backward" />'
            + (color.group(0) if color else "")
            + (owner.group(0) if owner else (f"<Owner>{shipgen.OWNER_ID}</Owner>" if shipgen.OWNER_ID else ""))
            + "<ShareMode>Faction</ShareMode><CustomName>Connector [Dock] Belly</CustomName>"
            "<ShowOnHUD>false</ShowOnHUD><Enabled>true</Enabled></MyObjectBuilder_CubeBlock>")
    grid0 = grid0[:plate.start()] + conn + grid0[plate.end():]
    # 2. fly by the main remote
    assert grid0.count("<CustomName>Remote Control Main</CustomName>") == 1, "Remote Control Main not found"
    grid0 = grid0.replace("<CustomName>Remote Control Main</CustomName>", "<CustomName>Remote Control Main [Fleet]</CustomName>")
    text = text[:first] + grid0 + text[end0:]

    # 3. programmable blocks: our script in the spare one, the flight scripts off
    done = []
    def pbfix(m):
        b = m.group(0)
        if m.group(1) != "MyProgrammableBlock":
            return b
        name = re.search(r"<CustomName>(.*?)</CustomName>", b)
        name = name.group(1) if name else ""
        if any(o in name for o in OFF):
            done.append("off: " + name)
            return re.sub(r"<Enabled>true</Enabled>", "<Enabled>false</Enabled>", b)
        if name == "Programmable Block" and "<Program>" not in b and not any(d.startswith("drone") for d in done):
            done.append("drone: " + name)
            b = b.replace("<CustomName>Programmable Block</CustomName>", "<CustomName>Programmable Block [Fleet Drone] [Hauler]</CustomName>")
            b = re.sub(r"<Enabled>false</Enabled>", "<Enabled>true</Enabled>", b)
            return b.replace("</MyObjectBuilder_CubeBlock>", "<Program>" + escape(drone) + "</Program></MyObjectBuilder_CubeBlock>")
        return b
    text = re.sub(r"<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_(\w+)\">.*?</MyObjectBuilder_CubeBlock>", pbfix, text, flags=re.S)
    if not any(d.startswith("drone") for d in done):
        sys.exit("spare programmable block not found")

    # 4. name it
    text = re.sub(r'(<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype=")[^"]*(")', r"\g<1>" + escape(NAME) + r"\g<2>", text, count=1)
    text = re.sub(r"(<ShipBlueprint[^>]*>.*?<DisplayName>)[^<]*(</DisplayName>)", r"\g<1>" + escape(NAME) + r"\g<2>", text, count=1, flags=re.S)

    out = pathlib.Path(os.environ.get("APPDATA", "")) / "SpaceEngineers" / "Blueprints" / "local" / NAME
    out.mkdir(parents=True, exist_ok=True)
    (out / "bp.sbc").write_text(text, encoding="utf-8")
    b5 = out / "bp.sbcB5"
    if b5.exists():
        b5.unlink()
    print(f"wrote {out / 'bp.sbc'} from {src}")
    for d in done:
        print("  PB", d)


if __name__ == "__main__":
    main()
