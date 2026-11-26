#!/usr/bin/env python3
"""Fleet System ship generator: writes Space Engineers blueprints (bp.sbc).

  python3 tools/shipgen.py            build blueprints/ and print stats

Block sizes, masses and thrust come from the Space Engineers wiki infoboxes
(spaceengineers.wiki.gg). SubtypeIds are the vanilla IDs; install.bat checks
every block against the game's own CubeBlocks definitions on your PC before
copying a blueprint into your F10 list, so a wrong ID or footprint is caught
there instead of in game.

Conventions (SE): grid +X right, +Y up, +Z back (forward = -Z). A block's
local size (x, y, z) maps through its orientation: local right = Forward x Up,
local up = Up, local +z = Backward. Thrusters push the ship toward their own
Backward (+Z) side, i.e. their Forward points along the exhaust.
"""
import collections
import hashlib
import math
import os
import pathlib
import re
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "blueprints"
DIST = ROOT / "dist"


def _owner_id():
    """Player identity to own the terminal blocks. Without an <Owner> the game pastes them
    owned by nobody. FS_OWNER_ID wins; otherwise the most common owner in the player's own
    saved blueprints (Blueprints/local, skipping FS ones); otherwise 0 (no owner written)."""
    env = os.environ.get("FS_OWNER_ID", "").strip()
    if env.isdigit():
        return int(env)
    local = pathlib.Path(os.environ.get("APPDATA", "")) / "SpaceEngineers" / "Blueprints" / "local"
    seen = collections.Counter()
    for bp in local.glob("*/bp.sbc") if local.is_dir() else []:
        if bp.parent.name.startswith("FS "):
            continue
        try:
            seen.update(int(m) for m in re.findall(r"<Owner>(\d+)</Owner>", bp.read_text(encoding="utf-8", errors="replace")) if m != "0")
        except OSError:
            pass
    return seen.most_common(1)[0][0] if seen else 0


OWNER_ID = _owner_id()
G = 9.81
ATMO_SEA_LEVEL = 0.9          # wiki: ~90 % atmospheric thrust efficiency at sea level

DIRS = {"Forward": (0, 0, -1), "Backward": (0, 0, 1), "Left": (-1, 0, 0),
        "Right": (1, 0, 0), "Up": (0, 1, 0), "Down": (0, -1, 0)}

# key: (TypeId, SubtypeId, grid, size xyz, mass kg, thrust N, atmospheric)
B = {
    # small grid
    "s_armor":     ("CubeBlock", "SmallBlockArmorBlock", "Small", (1, 1, 1), 20, 0, False),
    "s_atmo":      ("Thrust", "SmallBlockSmallAtmosphericThrust", "Small", (1, 1, 3), 699, 96000, True),
    "s_atmo_big":  ("Thrust", "SmallBlockLargeAtmosphericThrust", "Small", (3, 3, 5), 2948, 576000, True),
    "s_gyro":      ("Gyro", "SmallBlockGyro", "Small", (1, 1, 1), 613.6, 0, False),
    "s_battery":   ("BatteryBlock", "SmallBlockBatteryBlock", "Small", (3, 2, 3), 1040.4, 0, False),
    "s_rc":        ("RemoteControl", "SmallBlockRemoteControl", "Small", (1, 1, 1), 38.2, 0, False),
    "s_pb":        ("MyProgrammableBlock", "SmallProgrammableBlock", "Small", (1, 1, 1), 138.4, 0, False),
    "s_connector": ("ShipConnector", "ConnectorMedium", "Small", (3, 3, 2), 685.2, 0, False),
    "s_merge":     ("MergeBlock", "SmallShipMergeBlock", "Small", (1, 3, 3), 152.2, 0, False),
    "s_antenna":   ("RadioAntenna", "SmallBlockRadioAntenna", "Small", (1, 1, 2), 72.2, 0, False),
    "s_gatling":   ("LargeGatlingTurret", "SmallGatlingTurret", "Small", (5, 5, 5), 692.0, 0, False),
    # large grid
    "l_armor":     ("CubeBlock", "LargeBlockArmorBlock", "Large", (1, 1, 1), 500, 0, False),
    "l_atmo":      ("Thrust", "LargeBlockSmallAtmosphericThrust", "Large", (1, 1, 3), 4000, 648000, True),
    "l_atmo_big":  ("Thrust", "LargeBlockLargeAtmosphericThrust", "Large", (3, 3, 5), 32970, 6480000, True),
    "l_gyro":      ("Gyro", "LargeBlockGyro", "Large", (1, 1, 1), 12817.0, 0, False),
    "l_battery":   ("BatteryBlock", "LargeBlockBatteryBlock", "Large", (1, 1, 1), 3845.0, 0, False),
    "l_reactor":   ("Reactor", "LargeBlockSmallGenerator", "Large", (1, 1, 1), 4793.0, 0, False),
    "l_pb":        ("MyProgrammableBlock", "LargeProgrammableBlock", "Large", (1, 1, 1), 534.4, 0, False),
    "l_cockpit":   ("Cockpit", "LargeBlockCockpitSeat", "Large", (1, 1, 1), 1768.0, 0, False),
    "l_connector": ("ShipConnector", "Connector", "Large", (1, 1, 1), 3564.0, 0, False),
    "l_antenna":   ("RadioAntenna", "LargeBlockRadioAntenna", "Large", (1, 6, 2), 3401.6, 0, False),
    "l_gatling":   ("LargeGatlingTurret", "", "Large", (3, 3, 3), 1428.0, 0, False),
}


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def extents(size, fwd, up):
    f, u = DIRS[fwd], DIRS[up]
    assert sum(abs(f[i] * u[i]) for i in range(3)) == 0, f"Forward {fwd} and Up {up} must be perpendicular"
    right = cross(f, u)
    back = tuple(-c for c in f)
    return tuple(abs(right[i]) * size[0] + abs(u[i]) * size[1] + abs(back[i]) * size[2] for i in range(3))


class Ship:
    def __init__(self, name, grid):
        self.name, self.grid = name, grid
        self.blocks, self.cells = [], {}

    def add(self, key, mn, fwd="Forward", up="Up", name=None, program=None, extra=""):
        t, sub, grid, size, mass, thrust, atmo = B[key]
        assert grid == self.grid, f"{key} is {grid} grid, ship is {self.grid}"
        ext = extents(size, fwd, up)
        cells = [(mn[0] + x, mn[1] + y, mn[2] + z) for x in range(ext[0]) for y in range(ext[1]) for z in range(ext[2])]
        for c in cells:
            if c in self.cells:
                raise ValueError(f"{self.name}: {name or key} at {mn} overlaps {self.cells[c]} at cell {c}")
        label = name or key
        for c in cells:
            self.cells[c] = label
        self.blocks.append(dict(key=key, min=mn, fwd=fwd, up=up, name=name, program=program, extra=extra, cells=cells))

    def fill(self, lo, hi, key, shell=False, skip=()):
        for x in range(lo[0], hi[0] + 1):
            for y in range(lo[1], hi[1] + 1):
                for z in range(lo[2], hi[2] + 1):
                    if (x, y, z) in self.cells or (x, y, z) in skip:
                        continue
                    if shell and lo[0] < x < hi[0] and lo[1] < y < hi[1] and lo[2] < z < hi[2]:
                        continue
                    self.add(key, (x, y, z))

    def check_exhaust(self, depth):
        """Every thruster's exhaust path (cells beyond its Forward face) must be clear of the ship."""
        for b in self.blocks:
            if not B[b["key"]][5]:
                continue
            f = DIRS[b["fwd"]]
            own = set(b["cells"])
            face = [c for c in b["cells"] if (c[0] + f[0], c[1] + f[1], c[2] + f[2]) not in own]
            for c in face:
                for k in range(1, depth + 1):
                    q = (c[0] + f[0] * k, c[1] + f[1] * k, c[2] + f[2] * k)
                    if q in self.cells:
                        raise ValueError(f"{self.name}: exhaust of {b['name']} blocked by {self.cells[q]} at {q}")

    # ------------------------------------------------------------ stats
    def stats(self, cargo_kg=0):
        cell = 0.5 if self.grid == "Small" else 2.5
        mass = cargo_kg
        com = [0.0, 0.0, 0.0]
        push = {d: 0.0 for d in DIRS}
        for b in self.blocks:
            _, _, _, _, m, thrust, atmo = B[b["key"]]
            mass += m
            cx = [sum(c[i] for c in b["cells"]) / len(b["cells"]) for i in range(3)]
            for i in range(3):
                com[i] += m * cx[i]
            if thrust:
                f = DIRS[b["fwd"]]
                pushdir = [k for k, v in DIRS.items() if v == tuple(-c for c in f)][0]
                push[pushdir] += thrust * (ATMO_SEA_LEVEL if atmo else 1)
        com = [c / (mass - cargo_kg) for c in com]
        lift = push["Up"]
        return dict(mass=mass, com=com, push=push, cell=cell,
                    twr=lift / (mass * G), accel={d: push[d] / mass for d in push})

    # ------------------------------------------------------------ XML
    def eid(self, inst, i):
        return int(hashlib.sha1(f"{self.name}/{inst}/{i}".encode()).hexdigest()[:15], 16)

    def grid_xml(self, code, inst="0", pos=(0, 0, 0), fwd=(0, 0, -1), up=(0, 1, 0), quat=(0, 0, 0, 1), extra=None):
        """One <CubeGrid>. extra: {block index: xml} appended to that block (e.g. connector links)."""
        extra = extra or {}
        lines = []
        for i, b in enumerate(self.blocks):
            t, sub, *_ = B[b["key"]]
            mn = b["min"]
            sub_xml = f"<SubtypeName>{sub}</SubtypeName>" if sub else "<SubtypeName />"
            s = [f'            <MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{t}">',
                 f"              {sub_xml}",
                 f"              <EntityId>{self.eid(inst, i)}</EntityId>",
                 f'              <Min x="{mn[0]}" y="{mn[1]}" z="{mn[2]}" />',
                 f'              <BlockOrientation Forward="{b["fwd"]}" Up="{b["up"]}" />',
                 '              <ColorMaskHSV x="0.58" y="-0.55" z="-0.1" />']
            if OWNER_ID and t != "CubeBlock":      # terminal blocks only, like a game-saved blueprint
                s.append(f"              <Owner>{OWNER_ID}</Owner>")
                s.append("              <ShareMode>Faction</ShareMode>")
            if b["name"]:
                s.append(f"              <CustomName>{escape(b['name'])}</CustomName>")
            if b["extra"]:
                s.append("              " + b["extra"])
            if i in extra:
                s.append("              " + extra[i])
            if b["program"]:
                s.append(f"              <Program>{escape(code[b['program']])}</Program>")
            s.append("            </MyObjectBuilder_CubeBlock>")
            lines.append("\n".join(s))
        f = lambda v: " ".join(f'{a}="{c:g}"' for a, c in zip("xyz", v))
        return f"""        <CubeGrid>
          <SubtypeName />
          <EntityId>{int(hashlib.sha1(f"{self.name}/{inst}/grid".encode()).hexdigest()[:15], 16)}</EntityId>
          <PersistentFlags>CastShadows InScene</PersistentFlags>
          <PositionAndOrientation>
            <Position {f(pos)} />
            <Forward {f(fwd)} />
            <Up {f(up)} />
            <Orientation>
              <X>{quat[0]:g}</X>
              <Y>{quat[1]:g}</Y>
              <Z>{quat[2]:g}</Z>
              <W>{quat[3]:g}</W>
            </Orientation>
          </PositionAndOrientation>
          <GridSizeEnum>{self.grid}</GridSizeEnum>
          <CubeBlocks>
{chr(10).join(lines)}
          </CubeBlocks>
          <DisplayName>{escape(self.name)}</DisplayName>
          <DestructibleBlocks>true</DestructibleBlocks>
          <IsRespawnGrid>false</IsRespawnGrid>
          <LocalCoordSys>0</LocalCoordSys>
        </CubeGrid>"""

    def xml(self, code):
        return blueprint_xml(self.name, [self.grid_xml(code)])

    def index_of(self, name):
        return next(i for i, b in enumerate(self.blocks) if b["name"] == name)


def blueprint_xml(name, grids):
    return f"""<?xml version="1.0"?>
<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <ShipBlueprints>
    <ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">
      <Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="{escape(name)}" />
      <DisplayName>{escape(name)}</DisplayName>
      <CubeGrids>
{chr(10).join(grids)}
      </CubeGrids>
      <EnvironmentType>None</EnvironmentType>
      <WorkshopId>0</WorkshopId>
      <OwnerSteamId>0</OwnerSteamId>
      <Points>0</Points>
    </ShipBlueprint>
  </ShipBlueprints>
</Definitions>
"""


# ======================================================================
# FS Buster Atmo (small grid): atmospheric defense drone
#   core box x2..6, y5..9, z2..13 (5 x 5 x 12 small blocks)
#   1 large atmo lift thruster on the centre of mass, 6 small atmo for
#   horizontal control (no down thrust: gravity does that), 2 gatling
#   turrets (top + belly), nose connector for docking, merge fin for
#   factory printing, 2 batteries, 2 gyros.
# ======================================================================
# Pre-loaded turret ammo. SmallGatlingTurret inventory is 0.08 m3 and a
# NATO_25x184mm magazine is 16 L / 35 kg, so 5 fill it. Without ammo the hub
# holds a Buster docked (auto-RTB would recall it at 0% ammo). Same
# ComponentContainer layout the game writes for a turret in a save.
AMMO_MAGS = 5
AMMO_MASS = 2 * AMMO_MAGS * 35     # kg, both turrets
TURRET_AMMO = (
    "<ComponentContainer><Components><ComponentData><TypeId>MyInventoryBase</TypeId>"
    "<Component xsi:type=\"MyObjectBuilder_Inventory\"><CanPutItems>true</CanPutItems><Items>"
    "<MyObjectBuilder_InventoryItem><Amount>" + str(AMMO_MAGS) + "</Amount>"
    "<PhysicalContent xsi:type=\"MyObjectBuilder_AmmoMagazine\"><SubtypeName>NATO_25x184mm</SubtypeName>"
    "<ProjectilesCount>0</ProjectilesCount></PhysicalContent><ItemId>0</ItemId></MyObjectBuilder_InventoryItem>"
    "</Items><nextItemId>1</nextItemId><Volume>0.08</Volume><Mass>9223372036854.775807</Mass>"
    "<MaxItemCount>2147483647</MaxItemCount><Size xsi:nil=\"true\" /><InventoryFlags>CanReceive</InventoryFlags>"
    "<RemoveEntityOnEmpty>false</RemoveEntityOnEmpty></Component></ComponentData></Components></ComponentContainer>")


def buster():
    s = Ship("FS Buster Atmo", "Small")
    s.add("s_atmo_big", (3, 3, 6), fwd="Down", up="Forward", name="Lift Thruster")
    s.add("s_gatling", (2, 10, 6), fwd="Forward", up="Up", name="Gatling Turret Top", extra=TURRET_AMMO)
    s.add("s_gatling", (2, 0, 9), fwd="Forward", up="Down", name="Gatling Turret Belly", extra=TURRET_AMMO)
    s.add("s_merge", (4, 2, 3), name="Merge Block (factory mount)")
    s.add("s_connector", (3, 6, 0), fwd="Forward", up="Up", name="Connector")
    # horizontal thrusters (Forward = exhaust direction; pushes the opposite way)
    s.add("s_atmo", (1, 7, 11), fwd="Backward", name="Thruster Forward L")
    s.add("s_atmo", (7, 7, 11), fwd="Backward", name="Thruster Forward R")
    s.add("s_atmo", (1, 7, 2), fwd="Forward", name="Thruster Brake L")
    s.add("s_atmo", (7, 7, 2), fwd="Forward", name="Thruster Brake R")
    s.add("s_atmo", (7, 6, 7), fwd="Right", name="Thruster Left")     # exhaust right -> pushes left
    s.add("s_atmo", (-1, 6, 7), fwd="Left", name="Thruster Right")    # exhaust left -> pushes right
    # control & power
    s.add("s_rc", (4, 9, 3), name="Remote Control")
    s.add("s_pb", (2, 9, 3), name="Programmable Block [Fleet Drone]", program="Drone")
    s.add("s_gyro", (4, 5, 3), name="Gyroscope 1")
    s.add("s_gyro", (4, 5, 11), name="Gyroscope 2")
    s.add("s_battery", (3, 8, 4), name="Battery 1")
    s.add("s_battery", (3, 8, 10), name="Battery 2")
    s.add("s_antenna", (2, 10, 12), name="Antenna", extra="<BroadcastRadius>3000</BroadcastRadius>")
    # structure: thruster intake mounts, then fill the core
    for c in [(1, 7, 10), (7, 7, 10), (1, 7, 5), (7, 7, 5)]:
        s.add("s_armor", c)
    s.fill((2, 5, 2), (6, 9, 13), "s_armor")
    s.check_exhaust(depth=6)
    return s


# ======================================================================
# FS Carrier Atmo (large grid): atmospheric drone carrier
#   keel x2..6, y0..4, z0..23 (hollow armor shell) houses lift thrusters,
#   reactors, batteries, gyros and the Fleet PBs; flight deck x0..8, y5.
#   8 [Dock] connectors on the deck edges (4 per side) face outward so
#   Busters dock nose-in from the flanks with open air for the approach.
# ======================================================================
def carrier():
    s = Ship("FS Carrier Atmo", "Large")
    # lift: 2 large atmo on the centreline + 8 small atmo at the keel edges
    s.add("l_atmo_big", (3, 0, 5), fwd="Down", up="Forward", name="Lift Thruster Fore")
    s.add("l_atmo_big", (3, 0, 16), fwd="Down", up="Forward", name="Lift Thruster Aft")
    for x in (2, 6):
        for z in (1, 10, 13, 22):
            s.add("l_atmo", (x, 0, z), fwd="Down", up="Forward", name=f"Lift Thruster {'L' if x == 2 else 'R'}{z}")
    # main drive / brake
    s.add("l_atmo_big", (3, 1, 21), fwd="Backward", up="Up", name="Main Thruster")
    s.add("l_atmo_big", (3, 1, -2), fwd="Forward", up="Up", name="Brake Thruster")
    # lateral
    for z in (6, 12, 18):
        s.add("l_atmo", (7, 2, z), fwd="Right", up="Up", name=f"Thruster Left {z}")
        s.add("l_atmo", (-1, 2, z), fwd="Left", up="Up", name=f"Thruster Right {z}")
    # power & control in the keel
    for i, p in enumerate([(3, 1, 10), (5, 1, 10), (3, 1, 13), (5, 1, 13)]):
        s.add("l_reactor", p, name=f"Small Reactor {i + 1}")
    for i, p in enumerate([(3, 3, 10), (5, 3, 10), (3, 3, 13), (5, 3, 13)]):
        s.add("l_battery", p, name=f"Battery {i + 1}")
    for i, p in enumerate([(4, 1, 9), (4, 2, 9), (4, 3, 9), (4, 1, 14), (4, 2, 14), (4, 3, 14)]):
        s.add("l_gyro", p, name=f"Gyroscope {i + 1}")
    s.add("l_pb", (3, 2, 11), name="Programmable Block [Fleet Core]", program="HubCore")
    s.add("l_pb", (5, 2, 11), name="Programmable Block [Fleet UI]", program="HubUI")
    # deck: cockpit (hub reference + UI screens), turrets, antenna
    s.add("l_cockpit", (4, 6, 1), fwd="Forward", up="Up", name="Cockpit [FS:Fleet:0] [FS:Map:1] [FS:Hangar:2]")
    s.add("l_gatling", (3, 6, 4), name="Gatling Turret Fore")
    s.add("l_gatling", (3, 6, 18), name="Gatling Turret Aft")
    s.add("l_antenna", (7, 6, 21), name="Antenna", extra="<BroadcastRadius>5000</BroadcastRadius>")
    # docking connectors on the deck edges, facing outward
    for i, z in enumerate((4, 9, 14, 19)):
        s.add("l_connector", (-1, 5, z), fwd="Left", up="Up", name=f"Connector [Dock] L{i + 1}")
        s.add("l_connector", (9, 5, z), fwd="Right", up="Up", name=f"Connector [Dock] R{i + 1}")
    # structure
    s.fill((0, 5, 0), (8, 5, 23), "l_armor")                 # flight deck
    s.fill((2, 0, 0), (6, 4, 23), "l_armor", shell=True)     # keel shell
    s.check_exhaust(depth=4)
    return s


def report(s, cargo=0, note=""):
    st = s.stats(cargo)
    cell = st["cell"]
    xs = [c[0] for c in s.cells]; ys = [c[1] for c in s.cells]; zs = [c[2] for c in s.cells]
    dims = ((max(xs) - min(xs) + 1) * cell, (max(ys) - min(ys) + 1) * cell, (max(zs) - min(zs) + 1) * cell)
    lines = [f"{s.name}{note}: {len(s.blocks)} blocks, {st['mass'] / 1000:.1f} t, "
             f"{dims[0]:.1f} x {dims[1]:.1f} x {dims[2]:.1f} m (W x H x L)",
             f"  lift TWR at 1 g, sea level: {st['twr']:.2f}",
             "  accel m/s^2: " + ", ".join(f"{d} {st['accel'][d]:.1f}" for d in ("Up", "Forward", "Backward", "Left", "Right", "Down")),
             f"  centre of mass (cells): x {st['com'][0]:.2f}  y {st['com'][1]:.2f}  z {st['com'][2]:.2f}"]
    print("\n".join(lines))
    return lines, st


# ======================================================================
# FS Carrier Atmo + 8 Busters: the carrier with a Buster docked at every
# [Dock] connector. Each Buster is rotated nose-in (left docks: nose +X,
# right docks: nose -X, upright) and placed so its connector face sits
# DOCK_GAP metres from the carrier connector face; the connector pairs are
# written as connected, and Hub Core also locks occupied docks at start-up.
# ======================================================================
DOCK_GAP = 0.05
LG, SG = 2.5, 0.5


def rot_y(v, deg):
    a = math.radians(deg)
    return (v[0] * math.cos(a) + v[2] * math.sin(a), v[1], -v[0] * math.sin(a) + v[2] * math.cos(a))


def combined(carrier_ship, buster_ship, code):
    c_conn = [(i, b) for i, b in enumerate(carrier_ship.blocks) if b["key"] == "l_connector"]
    bi = buster_ship.index_of("Connector")
    bconn = buster_ship.blocks[bi]
    # buster connector face centre in buster-local metres (face on the -Z side of its min cell row)
    bc = [sum(c[k] for c in bconn["cells"]) / len(bconn["cells"]) * SG for k in range(3)]
    zmin = min(c[2] for c in bconn["cells"])
    b_face = (bc[0], bc[1], (zmin - 0.5) * SG)
    grids, c_extra = [], {}
    world_cells = []   # (centre, half) boxes of every docked buster block, for the clearance check
    for n, (ci, cb) in enumerate(c_conn):
        d = DIRS[cb["fwd"]]                       # carrier connector facing (world, carrier at origin)
        cc = [cb["cells"][0][k] * LG for k in range(3)]
        face = tuple(cc[k] + d[k] * (LG / 2 + DOCK_GAP) for k in range(3))
        # buster forward must point back at the carrier: -d. Rotation about Y mapping (0,0,-1) -> -d
        deg = {(-1, 0, 0): -90, (1, 0, 0): 90}[d]
        fwd = rot_y((0, 0, -1), deg)
        assert all(abs(fwd[k] + d[k]) < 1e-9 for k in range(3))
        off = rot_y(b_face, deg)
        pos = tuple(face[k] - off[k] for k in range(3))
        half = math.radians(deg) / 2
        quat = (0, math.sin(half), 0, math.cos(half))
        inst = f"dock{n}"
        c_extra[ci] = f"<ConnectedEntityId>{buster_ship.eid(inst, bi)}</ConnectedEntityId><IsMaster>true</IsMaster>"
        b_extra = {bi: f"<ConnectedEntityId>{carrier_ship.eid('0', ci)}</ConnectedEntityId><IsMaster>false</IsMaster>"}
        grids.append(buster_ship.grid_xml(code, inst, pos, tuple(round(v, 9) for v in fwd), (0, 1, 0), quat, b_extra))
        for blk in buster_ship.blocks:
            for cell in blk["cells"]:
                w = rot_y(tuple(cell[k] * SG for k in range(3)), deg)
                world_cells.append((tuple(pos[k] + w[k] for k in range(3)), SG / 2, cb["name"], blk["name"] or blk["key"]))
        # docking geometry self-check: connector faces DOCK_GAP apart, facing each other
        bf = rot_y(b_face, deg)
        gap = math.dist(tuple(pos[k] + bf[k] for k in range(3)), tuple(cc[k] + d[k] * LG / 2 for k in range(3)))
        assert abs(gap - DOCK_GAP) < 1e-6, gap
    # clearance: no docked buster block may intersect a carrier block or another buster
    carrier_boxes = [(tuple(c[k] * LG for k in range(3)), LG / 2, blk["name"] or blk["key"]) for blk in carrier_ship.blocks for c in blk["cells"]]
    eps = 1e-6
    def hit(a, ha, b, hb):
        return all(abs(a[k] - b[k]) < ha + hb - eps for k in range(3))
    for (wc, h, dock, what) in world_cells:
        for (cc2, hc, cname) in carrier_boxes:
            if hit(wc, h, cc2, hc):
                raise ValueError(f"Buster at {dock}: {what} intersects carrier block {cname}")
    by_dock = {}
    for (wc, h, dock, what) in world_cells:
        by_dock.setdefault(dock, []).append(wc)
    docks = list(by_dock)
    def aabb(cells):
        return [min(c[k] for c in cells) - SG / 2 for k in range(3)], [max(c[k] for c in cells) + SG / 2 for k in range(3)]
    boxes = {dk: aabb(by_dock[dk]) for dk in docks}
    for i in range(len(docks)):
        for j in range(i + 1, len(docks)):
            (alo, ahi), (blo, bhi) = boxes[docks[i]], boxes[docks[j]]
            if all(alo[k] < bhi[k] - eps and blo[k] < ahi[k] - eps for k in range(3)):
                if any(hit(p, SG / 2, q, SG / 2) for p in by_dock[docks[i]] for q in by_dock[docks[j]]):
                    raise ValueError(f"Busters at {docks[i]} and {docks[j]} intersect")
    name = "FS Carrier Atmo + 8 Busters"
    return name, blueprint_xml(name, [carrier_ship.grid_xml(code, "0", extra=c_extra)] + grids)


def main():
    code = {k: (DIST / f"{k}.cs").read_text(encoding="utf-8").replace("\r\n", "\n") for k in ("Drone", "HubCore", "HubUI")}
    OUT.mkdir(exist_ok=True)
    summary = []
    b = buster()
    summary += report(b)[0]
    summary += report(b, cargo=AMMO_MASS, note=f" (+{AMMO_MASS} kg pre-loaded ammo)")[0][1:2]
    c = carrier()
    summary += report(c)[0]
    summary += report(c, cargo=8 * (b.stats()["mass"] + AMMO_MASS), note=" (+8 Busters docked)")[0][1:2]
    for s in (b, c):
        d = OUT / s.name
        d.mkdir(exist_ok=True)
        (d / "bp.sbc").write_text(s.xml(code), encoding="utf-8")
    cname, cxml = combined(c, b, code)
    d = OUT / cname
    d.mkdir(exist_ok=True)
    (d / "bp.sbc").write_text(cxml, encoding="utf-8")
    summary.append(f"{cname}: carrier + {len(cxml.split('<CubeGrid>')) - 2} docked Busters (connector faces {DOCK_GAP} m apart, clearance checked)")
    print(summary[-1])
    print(f"block owner: {OWNER_ID}" if OWNER_ID else "block owner: none found (set FS_OWNER_ID): ships will paste unowned")
    (OUT / "STATS.txt").write_text("\n".join(summary) + "\n", encoding="utf-8")
    print(f"wrote {OUT}/<name>/bp.sbc")


if __name__ == "__main__":
    main()
