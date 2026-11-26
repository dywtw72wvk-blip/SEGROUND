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
    "s_warhead":   ("Warhead", "SmallWarhead", "Small", (1, 1, 1), 104.5, 0, False),
    "s_camera":    ("CameraBlock", "SmallCameraBlock", "Small", (1, 1, 1), 40.6, 0, False),
    "s_ai_off":    ("OffensiveCombatBlock", "SmallOffensiveCombat", "Small", (1, 1, 1), 156.0, 0, False),
    "s_ai_fly":    ("FlightMovementBlock", "SmallFlightMovement", "Small", (1, 1, 1), 156.0, 0, False),
    "s_gatling":   ("LargeGatlingTurret", "SmallGatlingTurret", "Small", (5, 5, 5), 692.0, 0, False),
    # spotter airframe (all vanilla, no DLC): flat atmospheric thrusters, solar-panel wings
    "s_flat":      ("Thrust", "SmallBlockSmallFlatAtmosphericThrust", "Small", (1, 1, 1), 303, 32000, True),
    "s_flat_big":  ("Thrust", "SmallBlockLargeFlatAtmosphericThrust", "Small", (3, 3, 2), 1060, 230000, True),
    "s_sbatt":     ("BatteryBlock", "SmallBlockSmallBatteryBlock", "Small", (1, 1, 1), 146, 0, False),
    "s_solar":     ("SolarPanel", "SmallBlockSolarPanel", "Small", (10, 5, 1), 143, 0, False),
    "s_conveyor":  ("Conveyor", "SmallBlockConveyor", "Small", (1, 1, 1), 68, 0, False),
    # large grid
    "l_armor":     ("CubeBlock", "LargeBlockArmorBlock", "Large", (1, 1, 1), 500, 0, False),
    "l_atmo":      ("Thrust", "LargeBlockSmallAtmosphericThrust", "Large", (1, 1, 3), 4000, 648000, True),
    "l_atmo_big":  ("Thrust", "LargeBlockLargeAtmosphericThrust", "Large", (3, 3, 5), 32970, 6480000, True),
    "l_gyro":      ("Gyro", "LargeBlockGyro", "Large", (1, 1, 1), 12817.0, 0, False),
    "l_battery":   ("BatteryBlock", "LargeBlockBatteryBlock", "Large", (1, 1, 1), 3845.0, 0, False),
    "l_reactor":   ("Reactor", "LargeBlockSmallGenerator", "Large", (1, 1, 1), 4793.0, 0, False),
    "l_pb":        ("MyProgrammableBlock", "LargeProgrammableBlock", "Large", (1, 1, 1), 534.4, 0, False),
    # Industrial Cockpit (Decorative Pack DLC): screens stay in view at normal FOV.
    # Surfaces: 0 Large, 1 TopLeft, 2 TopCenter, 3 TopRight, 4 Center.
    "l_cockpit":   ("Cockpit", "LargeBlockCockpitIndustrial", "Large", (1, 1, 1), 1768.0, 0, False),
    "l_connector": ("ShipConnector", "Connector", "Large", (1, 1, 1), 3564.0, 0, False),
    "l_antenna":   ("RadioAntenna", "LargeBlockRadioAntenna", "Large", (1, 6, 2), 3401.6, 0, False),
    "l_gatling":   ("LargeGatlingTurret", "", "Large", (3, 3, 3), 1428.0, 0, False),
    "l_camera":    ("CameraBlock", "LargeCameraBlock", "Large", (1, 1, 1), 92.0, 0, False),
    # mining carrier
    "l_container": ("CargoContainer", "LargeBlockLargeContainer", "Large", (3, 3, 3), 2593.6, 0, False),
    "l_reactor_big": ("Reactor", "LargeBlockLargeGenerator", "Large", (3, 3, 3), 73795.0, 0, False),
    "l_ore":       ("OreDetector", "LargeOreDetector", "Large", (1, 1, 2), 1545.0, 0, False),
    "l_gear":      ("LandingGear", "LargeBlockLandingGear", "Large", (1, 2, 3), 3304.0, 0, False),
}
ORE_T_PER_M3 = 2.7            # ore and stone: ~0.37 L/kg
LARGE_CONTAINER_M3 = 421.875  # large-grid Large Cargo Container at 1x block inventory


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
                # {EID:block name} -> that block's entity id in this copy of the ship (toolbar targets)
                s.append("              " + re.sub(r"\{EID:([^}]*)\}", lambda m: str(self.eid(inst, self.index_of(m.group(1)))), b["extra"]))
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
{"      <DLC>DecorativeBlocks</DLC>" + chr(10) if any("CockpitIndustrial" in g for g in grids) else ""}      <CubeGrids>
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
#   horizontal control (no down thrust: gravity does that), 4 warheads
#   (kamikaze payload), 2 nose cameras (lidar), nose connector, merge fin for
#   factory printing, 2 batteries, 2 gyros.
# ======================================================================
# 5 NATO_25x184mm magazines fill a SmallGatlingTurret (0.08 m3); same layout the game saves
TURRET_AMMO = (
    "<ComponentContainer><Components><ComponentData><TypeId>MyInventoryBase</TypeId>"
    "<Component xsi:type=\"MyObjectBuilder_Inventory\"><CanPutItems>true</CanPutItems><Items>"
    "<MyObjectBuilder_InventoryItem><Amount>5</Amount>"
    "<PhysicalContent xsi:type=\"MyObjectBuilder_AmmoMagazine\"><SubtypeName>NATO_25x184mm</SubtypeName>"
    "<ProjectilesCount>0</ProjectilesCount></PhysicalContent><ItemId>0</ItemId></MyObjectBuilder_InventoryItem>"
    "</Items><nextItemId>1</nextItemId><Volume>0.08</Volume><Mass>9223372036854.775807</Mass>"
    "<MaxItemCount>2147483647</MaxItemCount><Size xsi:nil=\"true\" /><InventoryFlags>CanReceive</InventoryFlags>"
    "<RemoveEntityOnEmpty>false</RemoveEntityOnEmpty></Component></ComponentData></Components></ComponentContainer>")


def toolbar(slots):
    """Ship-controller toolbar: [(slot index, block name, Run argument | "!Action")], same layout the
    game saves. Slots 0-8 are page 1, 9-17 page 2. The block id is filled in per ship copy."""
    def item(i, name, arg):
        if arg.startswith("!"):
            act = f"<Action>{arg[1:]}</Action>"
        else:
            act = (f"<Action>Run</Action><Parameters><MyObjectBuilder_ToolbarItemActionParameter><TypeCode>String</TypeCode>"
                   f"<Value>{escape(arg)}</Value></MyObjectBuilder_ToolbarItemActionParameter></Parameters>")
        return (f"<Slot><Index>{i}</Index><Item /><Data xsi:type=\"MyObjectBuilder_ToolbarItemTerminalBlock\">"
                f"{act}<BlockEntityId>{{EID:{name}}}</BlockEntityId></Data></Slot>")
    items = "".join(item(i, name, arg) for i, name, arg in slots)
    return (f"<Toolbar><ToolbarType>Character</ToolbarType><SelectedSlot xsi:nil=\"true\" />"
            f"<Slots>{items}</Slots><SlotsGamepad /></Toolbar>")


# AI Offensive: Intercept pattern (id 3), closest target, activated. It only searches until the
# script hands it a launched drone to intercept (then the AI Flight block is switched on).
AI_OFFENSIVE = (
    "<ComponentContainer><Components>"
    "<ComponentData><TypeId>MyOffensiveCombatIntercept</TypeId><Component xsi:type=\"MyObjectBuilder_OffensiveCombatIntercept\">"
    "<Guidance>Basic</Guidance><OverrideCollisionAvoidance>true</OverrideCollisionAvoidance></Component></ComponentData>"
    "<ComponentData><TypeId>MyAiBlockComponent</TypeId><Component xsi:type=\"MyObjectBuilder_AiBlockComponent\">"
    "<IsActivated>true</IsActivated></Component></ComponentData>"
    "</Components></ComponentContainer>"
    "<TargetPriority>Closest</TargetPriority><SelectedAttackPattern>3</SelectedAttackPattern><CanTargetCharacters>false</CanTargetCharacters>")
AI_FLIGHT = (
    "<ComponentContainer><Components><ComponentData><TypeId>MyAiBlockComponent</TypeId>"
    "<Component xsi:type=\"MyObjectBuilder_AiBlockComponent\"><IsActivated>true</IsActivated></Component>"
    "</ComponentData></Components></ComponentContainer><Enabled>false</Enabled>")


def drone_hull(s):
    """The shared small-grid airframe: thrusters, control, power, nose connector and cameras."""
    s.add("s_atmo_big", (3, 3, 6), fwd="Down", up="Forward", name="Lift Thruster")
    s.add("s_camera", (2, 7, 1), fwd="Forward", up="Up", name="Camera L")
    s.add("s_camera", (6, 7, 1), fwd="Forward", up="Up", name="Camera R")
    s.add("s_connector", (3, 6, 0), fwd="Forward", up="Up", name="Connector")
    s.add("s_atmo", (1, 7, 11), fwd="Backward", name="Thruster Forward L")
    s.add("s_atmo", (7, 7, 11), fwd="Backward", name="Thruster Forward R")
    s.add("s_atmo", (1, 7, 2), fwd="Forward", name="Thruster Brake L")
    s.add("s_atmo", (7, 7, 2), fwd="Forward", name="Thruster Brake R")
    s.add("s_atmo", (7, 6, 7), fwd="Right", name="Thruster Left")
    s.add("s_atmo", (-1, 6, 7), fwd="Left", name="Thruster Right")
    s.add("s_rc", (4, 9, 3), name="Remote Control")
    s.add("s_gyro", (4, 5, 3), name="Gyroscope 1")
    s.add("s_gyro", (4, 5, 11), fwd="Forward", up="Down", name="Gyroscope 2")   # mounts on its bottom: face it into the hull
    s.add("s_battery", (3, 8, 4), name="Battery 1")
    s.add("s_battery", (3, 8, 10), name="Battery 2")
    s.add("s_antenna", (2, 10, 12), name="Antenna", extra="<BroadcastRadius>5000</BroadcastRadius>")   # small-grid max
    # (7|8, 7, 7) and (0|1, 7, 7) hold the lateral thrusters by the middle cell (their only mount points)
    for c in [(1, 7, 10), (7, 7, 10), (1, 7, 5), (7, 7, 5), (7, 7, 7), (8, 7, 7), (0, 7, 7), (1, 7, 7)]:
        s.add("s_armor", c)


# ======================================================================
# FS Spotter Atmo (small grid): light recon plane, no armor, no weapons
# (after the 'Butterfly' recon drone, vanilla blocks only). A 1.5 m wide
# fuselage of functional blocks around one battery, solar-panel wings,
# one large flat atmospheric lift thruster under the battery, small flat
# thrusters on the battery flanks, a conveyor boom with the antenna as tail.
# Sensors: 6 down-looking cameras (nose rows scan ahead, tail rows behind)
# and one forward camera. Docks nose-in like a Buster. Every block is held
# by its mount points (tools/connect.py checks this; there is no armor).
# ======================================================================
# The game drops Custom Data from a pasted blueprint, so the role comes from the PB name: the Drone
# script defaults to Role=Spotter (and the spotter's search settings) on a PB or grid named "...Spotter...".


def spotter():
    s = Ship("FS Spotter Atmo", "Small")
    s.add("s_connector", (0, 0, 0), fwd="Forward", up="Up", name="Connector")
    s.add("s_battery", (0, 0, 2), name="Battery")
    s.add("s_flat_big", (0, -2, 2), fwd="Down", up="Forward", name="Lift Thruster")
    # top deck
    s.add("s_rc", (0, 2, 2), name="Remote Control")
    s.add("s_sbatt", (1, 2, 2), name="Small Battery 1")
    s.add("s_pb", (0, 2, 3), name="Programmable Block [Fleet Drone] [Spotter]", program="Drone")
    s.add("s_gyro", (2, 2, 4), name="Gyroscope")
    s.add("s_sbatt", (1, 3, 2), name="Small Battery 2")
    s.add("s_camera", (1, 3, 1), fwd="Forward", up="Up", name="Camera Forward")
    # wings on the battery flanks
    s.add("s_solar", (3, 1, 2), fwd="Up", up="Backward", name="Wing R")
    s.add("s_solar", (-10, 1, 2), fwd="Up", up="Backward", name="Wing L")
    # horizontal thrusters under the wing roots, held by the battery
    for x, side in ((3, "R"), (-1, "L")):
        s.add("s_flat", (x, 0, 2), fwd="Forward", name=f"Thruster Brake {side}")
        s.add("s_flat", (x, 0, 4), fwd="Backward", name=f"Thruster Forward {side}")
    s.add("s_flat", (3, 0, 3), fwd="Right", name="Thruster Left")
    s.add("s_flat", (-1, 0, 3), fwd="Left", name="Thruster Right")
    # tail boom and antenna
    for z in (5, 6, 7):
        s.add("s_conveyor", (1, 1, z))
    s.add("s_antenna", (1, 1, 8), fwd="Backward", name="Antenna", extra="<BroadcastRadius>5000</BroadcastRadius>")
    # down-looking cameras: under the connector (scan ahead) and under the boom (scan behind)
    for x in (0, 1, 2):
        s.add("s_camera", (x, -1, 1), fwd="Down", up="Forward", name=f"Camera Down Nose {x + 1}")
    for z in (5, 6, 7):
        s.add("s_camera", (1, 0, z), fwd="Down", up="Backward", name=f"Camera Down Tail {z - 4}")
    s.check_exhaust(depth=6)
    return s


def buster():
    s = Ship("FS Buster Atmo", "Small")
    drone_hull(s)
    # payload: 4 warheads inside the hull (armed only in the dive or an intercept)
    for i, c in enumerate([(2, 6, 7), (6, 6, 7), (2, 8, 8), (6, 8, 8)]):
        s.add("s_warhead", c, name=f"Warhead {i + 1}")
    # intercept: AI Offensive (Intercept) + AI Flight (off until the script hands over)
    s.add("s_ai_off", (2, 6, 11), name="AI Offensive [Intercept]", extra=AI_OFFENSIVE)
    s.add("s_ai_fly", (6, 6, 11), name="AI Flight [Intercept]", extra=AI_FLIGHT)
    s.add("s_merge", (4, 2, 3), name="Merge Block (factory mount)")
    s.add("s_pb", (2, 9, 3), name="Programmable Block [Fleet Drone]", program="Drone")
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
        for x in (7, 8, 0, 1):              # mounts: atmo thrusters only attach by their middle cell
            s.add("l_armor", (x, 1, z))
    # power & control in the keel
    for i, p in enumerate([(3, 1, 10), (5, 1, 10), (3, 1, 13), (5, 1, 13)]):
        s.add("l_reactor", p, name=f"Small Reactor {i + 1}")
    for i, p in enumerate([(3, 3, 10), (5, 3, 10), (3, 3, 13), (5, 3, 13)]):
        s.add("l_battery", p, name=f"Battery {i + 1}")
    # gyros and PBs only mount on their bottom face: gyros on the keel floor, PBs on the reactors
    for i, p in enumerate([(3, 1, 9), (4, 1, 9), (5, 1, 9), (3, 1, 14), (4, 1, 14), (5, 1, 14)]):
        s.add("l_gyro", p, name=f"Gyroscope {i + 1}")
    s.add("l_pb", (3, 2, 10), name="Programmable Block [Fleet Core]", program="HubCore")
    s.add("l_pb", (5, 2, 10), name="Programmable Block [Fleet UI]", program="HubUI")
    # deck: cockpit (hub reference + UI screens), turrets, antenna
    # toolbar page 1: Defense menu (screen 3), page 2: Fleet screen (screen 0)
    ui = "Programmable Block [Fleet UI]"
    nav = ["up", "down", "left", "right", "apply"]
    strike = [(5, "Camera [Designator]", "!View"), (6, ui, "designate"), (7, ui, "loiter"), (8, ui, "attack")]
    bar = toolbar([(i, ui, "def " + a) for i, a in enumerate(nav)] + strike +
                  [(9 + i, ui, a) for i, a in enumerate(nav)] + [(14, ui, "attackall"), (15, ui, "abort"), (16, ui, "search")])
    s.add("l_cockpit", (4, 6, 1), fwd="Forward", up="Up",
          name="Cockpit [FS:Fleet:0] [FS:Map:1] [FS:Hangar:2] [FS:Defense:3]", extra=bar)
    s.add("l_gatling", (3, 6, 4), name="Gatling Turret Fore")
    s.add("l_gatling", (3, 6, 18), name="Gatling Turret Aft")
    # designation: a camera for `designate`; WeaponCore's HUD lock and threat list feed strikes too
    s.add("l_camera", (6, 6, 0), fwd="Forward", up="Up", name="Camera [Designator]")
    s.add("l_armor", (6, 6, 1))                               # the camera mounts on its back face
    s.add("l_antenna", (7, 6, 21), name="Antenna", extra="<BroadcastRadius>20000</BroadcastRadius>")   # reaches Busters across the S&D area
    # docking connectors on the deck edges, facing outward
    for i, z in enumerate((4, 9, 14, 19)):
        s.add("l_connector", (-1, 5, z), fwd="Left", up="Up", name=f"Connector [Dock] L{i + 1}")
        s.add("l_connector", (9, 5, z), fwd="Right", up="Up", name=f"Connector [Dock] R{i + 1}")
    s.add("l_connector", (4, 5, -1), fwd="Forward", up="Up", name="Connector [Dock] Bow")   # the spotter's
    # structure
    s.fill((0, 5, 0), (8, 5, 23), "l_armor")                 # flight deck
    s.fill((2, 0, 0), (6, 4, 23), "l_armor", shell=True)     # keel shell
    s.check_exhaust(depth=4)
    return s


# ======================================================================
# FS Mining Carrier Atmo (large grid): carries 4 vertical miners (designed
# around the SCAM Min3r 69R Atmospheric: 40 t, 5.5 x 7 x 5.5 m, connector on
# its belly level with its drill tips) on 4 upward-facing deck pads, and a
# hold of MINE_CONTAINERS Large Cargo Containers. Lift: 2 rows of 6 large
# atmospheric thrusters along the keel sides, sized so a full hold with 4
# full miners docked still lifts ~1.9 g at sea level (docs/MINING.md).
#   keel x0..10, y0..4, z0..17: lift rows x0..2 / x8..10, centre box
#   x3..7 (shell) holds the hold, reactor, gyros, batteries, PBs.
#   deck y5; pads at (3|7, 6, 5|12), 10 m x 17.5 m apart, nothing else
#   within 3 m of a pad above the deck.
# ======================================================================
MINE_CONTAINERS = 2
MINE_PADS = [(3, 6, 5), (7, 6, 5), (3, 6, 12), (7, 6, 12)]
MINER_MASS_T, MINER_CARGO_M3 = 40.2, 31.25      # SCAM Min3r 69R: dry mass, 2 small-grid large containers


def mining_carrier():
    s = Ship("FS Mining Carrier Atmo", "Large")
    # lift: 2 rows of 6 large atmo along the keel sides
    for x, side in ((0, "L"), (8, "R")):
        for i, z in enumerate(range(0, 18, 3)):
            s.add("l_atmo_big", (x, 0, z), fwd="Down", up="Forward", name=f"Lift Thruster {side}{i + 1}")
    # centre box: hold, reactor, gyros, batteries, PBs
    for i in range(MINE_CONTAINERS):
        s.add("l_container", (4, 1, 1 + 4 * i), name=f"Cargo Hold {i + 1}")
    s.add("l_reactor_big", (4, 1, 9), name="Large Reactor")
    for i, (x, z) in enumerate([(x, z) for z in range(13, 17) for x in (4, 5, 6)]):
        s.add("l_gyro", (x, 1, z), name=f"Gyroscope {i + 1}")                # gyros mount on their bottom: on the floor
    for i, (x, z) in enumerate([(4, 13), (6, 13), (4, 15), (6, 15)]):
        s.add("l_battery", (x, 2, z), name=f"Battery {i + 1}")
    s.add("l_pb", (4, 3, 13), name="Programmable Block [Mine Core]", program="MineCore")   # PBs mount on their bottom: on batteries
    s.add("l_pb", (6, 3, 13), name="Programmable Block [Mine UI]")
    s.fill((3, 0, 0), (7, 4, 17), "l_armor", shell=True)
    # deck and pads
    s.fill((0, 5, 0), (10, 5, 17), "l_armor")
    for i, p in enumerate(MINE_PADS):
        s.add("l_connector", p, fwd="Up", up="Forward", name=f"Connector [Dock] Pad {i + 1}")
    s.add("l_cockpit", (5, 6, 0), fwd="Forward", up="Up", name="Cockpit [FS:Mine:0] [FS:Map:1] [FS:Miners:2] [FS:Cargo:3]")
    s.add("l_antenna", (10, 6, 16), name="Antenna", extra="<BroadcastRadius>10000</BroadcastRadius>")
    # belly: ore detector and landing gear (clear of the lift exhaust columns)
    s.add("l_ore", (5, -1, 8), fwd="Forward", up="Up", name="Ore Detector")
    for i, (x, z) in enumerate([(4, 1), (6, 1), (4, 14), (6, 14)]):
        s.add("l_gear", (x, -2, z), name=f"Landing Gear {i + 1}")
    # main drive and brakes at the keel ends, held by a roof row over them
    for x in (2, 6):
        s.add("l_atmo_big", (x, 1, 18), fwd="Backward", up="Up", name=f"Main Thruster {'L' if x == 2 else 'R'}")
        s.add("l_atmo_big", (x, 1, -5), fwd="Forward", up="Up", name=f"Brake Thruster {'L' if x == 2 else 'R'}")
    s.fill((2, 4, 18), (8, 4, 20), "l_armor")
    s.fill((2, 4, -3), (8, 4, -1), "l_armor")
    # lateral thrusters on the deck edges; atmo thrusters only mount by their middle cell
    for z in (3, 9, 15):
        s.add("l_atmo", (11, 5, z), fwd="Right", up="Up", name=f"Thruster Left {z}")
        s.add("l_atmo", (-3, 5, z), fwd="Left", up="Up", name=f"Thruster Right {z}")
        for x in (10, 11, 12, -2, -1, 0):
            s.add("l_armor", (x, 6, z))
    s.check_exhaust(depth=4)
    for p in MINE_PADS:     # pad clearance: nothing above the deck within 3 m (1 cell) of a pad but the pad
        for c, who in s.cells.items():
            if c[1] >= 6 and abs(c[0] - p[0]) <= 1 and abs(c[2] - p[2]) <= 1 and c != p:
                raise ValueError(f"{s.name}: {who} at {c} blocks the miner pad at {p}")
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
FLEET_NAME = "FS Carrier Atmo + 8 Busters + Spotter"
LG, SG = 2.5, 0.5


def rot_y(v, deg):
    a = math.radians(deg)
    return (v[0] * math.cos(a) + v[2] * math.sin(a), v[1], -v[0] * math.sin(a) + v[2] * math.cos(a))


def combined(carrier_ship, buster_ship, spotter_ship, code):
    c_conn = [(i, b) for i, b in enumerate(carrier_ship.blocks) if b["key"] == "l_connector"]
    grids, c_extra = [], {}
    world_cells = []   # (centre, half) boxes of every docked drone block, for the clearance check
    for n, (ci, cb) in enumerate(c_conn):
        ship = spotter_ship if cb["name"].endswith("Bow") else buster_ship
        bi = ship.index_of("Connector")
        bconn = ship.blocks[bi]
        # drone connector face centre in drone-local metres (face on the -Z side of its min cell row)
        bc = [sum(c[k] for c in bconn["cells"]) / len(bconn["cells"]) * SG for k in range(3)]
        zmin = min(c[2] for c in bconn["cells"])
        b_face = (bc[0], bc[1], (zmin - 0.5) * SG)
        d = DIRS[cb["fwd"]]                       # carrier connector facing (world, carrier at origin)
        cc = [cb["cells"][0][k] * LG for k in range(3)]
        face = tuple(cc[k] + d[k] * (LG / 2 + DOCK_GAP) for k in range(3))
        # buster forward must point back at the carrier: -d. Rotation about Y mapping (0,0,-1) -> -d
        deg = {(-1, 0, 0): -90, (1, 0, 0): 90, (0, 0, -1): 180}[d]
        fwd = rot_y((0, 0, -1), deg)
        assert all(abs(fwd[k] + d[k]) < 1e-9 for k in range(3))
        off = rot_y(b_face, deg)
        pos = tuple(face[k] - off[k] for k in range(3))
        half = math.radians(deg) / 2
        quat = (0, math.sin(half), 0, math.cos(half))
        inst = f"dock{n}"
        c_extra[ci] = f"<ConnectedEntityId>{ship.eid(inst, bi)}</ConnectedEntityId><IsMaster>true</IsMaster>"
        b_extra = {bi: f"<ConnectedEntityId>{carrier_ship.eid('0', ci)}</ConnectedEntityId><IsMaster>false</IsMaster>"}
        grids.append(ship.grid_xml(code, inst, pos, tuple(round(v, 9) for v in fwd), (0, 1, 0), quat, b_extra))
        for blk in ship.blocks:
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
                raise ValueError(f"Drone at {dock}: {what} intersects carrier block {cname}")
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
                    raise ValueError(f"Drones at {docks[i]} and {docks[j]} intersect")
    name = FLEET_NAME
    return name, blueprint_xml(name, [carrier_ship.grid_xml(code, "0", extra=c_extra)] + grids)


def main():
    code = {k: (DIST / f"{k}.cs").read_text(encoding="utf-8").replace("\r\n", "\n") for k in ("Drone", "HubCore", "HubUI", "MineCore")}
    OUT.mkdir(exist_ok=True)
    summary = []
    b = buster()
    summary += report(b)[0]
    sp = spotter()
    summary += report(sp)[0]
    c = carrier()
    summary += report(c)[0]
    summary += report(c, cargo=8 * b.stats()["mass"], note=" (+8 Busters docked)")[0][1:2]
    m = mining_carrier()
    summary += report(m)[0]
    miners = 4 * MINER_MASS_T * 1000
    full = miners + (4 * MINER_CARGO_M3 + MINE_CONTAINERS * LARGE_CONTAINER_M3) * ORE_T_PER_M3 * 1000
    summary += report(m, cargo=miners, note=" (+4 miners docked)")[0][1:2]
    summary += [f"  hold: {MINE_CONTAINERS} x {LARGE_CONTAINER_M3:g} m3; full hold and 4 full miners: "
                f"{(m.stats()['mass'] + full) / 1000:.0f} t, lift TWR {m.stats(full)['twr']:.2f}"]
    print(summary[-1])
    for s in (b, sp, c, m):
        d = OUT / s.name
        d.mkdir(exist_ok=True)
        (d / "bp.sbc").write_text(s.xml(code), encoding="utf-8")
    cname, cxml = combined(c, b, sp, code)
    d = OUT / cname
    d.mkdir(exist_ok=True)
    (d / "bp.sbc").write_text(cxml, encoding="utf-8")
    summary.append(f"{cname}: carrier + {len(cxml.split('<CubeGrid>')) - 3} docked Busters + spotter on the bow (connector faces {DOCK_GAP} m apart, clearance checked)")
    print(summary[-1])
    print(f"block owner: {OWNER_ID}" if OWNER_ID else "block owner: none found (set FS_OWNER_ID): ships will paste unowned")
    (OUT / "STATS.txt").write_text("\n".join(summary) + "\n", encoding="utf-8")
    print(f"wrote {OUT}/<name>/bp.sbc")


if __name__ == "__main__":
    main()
