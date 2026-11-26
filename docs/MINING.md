# FS Mining: design (draft)

A mining carrier that carries 4 vertical atmospheric mining drones to an ore field, sends each drone to its own
deposit, collects their ore, and keeps track of how much it can still lift. Built on the Fleet System's existing
pieces: the IGC registry and reliable orders, docking to a moving carrier, the flight controller, the cockpit UI,
and the blueprint generator and checkers.

Status: design agreed (miner: SCAM Min3r 69R; software: our own Miner script). Built: the carrier blueprint (step 1), the MineCore carrier script (step 2) and the Miner role (step 3). None of it is tested in game yet; that is step 4.

## 1. How a mining run works

1. **Fly the carrier to the field.** You fly it, or use `goto GPS:...`. Over the field it parks: it lands on its
   landing gear, or hovers if the ground is too steep.
2. **Survey.** The carrier's ore detector, read through the OreNet mod, lists the deposits within range:
   ore type, position and volume. The Mining screen shows them on a map and as a list.
3. **Assign.** `mine Iron` (or `mine Iron,Nickel`, `mine any`, or `mine GPS:...` for one spot) hands out the
   deposits:
   - **One per miner:** each miner gets its own deposit, largest first.
   - **Sharing:** when there are fewer deposits than miners, the big ones are shared, each miner taking its own
     shafts in the deposit's shaft grid (multi-mining).
   - **Manual:** you can also pick a deposit and a miner on the screen.
4. **Mine.** Each miner flies to a point above its deposit and bores a vertical shaft with its drills down,
   then hops to the next shaft in its grid. It keeps going until its cargo is full, the deposit runs out, or its
   battery reserve says turn back.
5. **Unload.** It docks on its deck pad, and the carrier's script pulls the ore into the cargo hold (conveyor
   transfer through the connector). The miner tops up its battery and goes back out.
6. **Full.** The carrier stops accepting ore when either of these is true:
   - **No room:** the hold is full.
   - **Lift margin:** the next miner's load would push its lift-to-weight below `MinTWR` (default 1.4) for the
     current gravity and air density.

   Then it recalls the miners: **CARRIER FULL**. It measures this live, from its thrusters' current effective
   thrust and its own mass, so the safety margin holds on any planet and at any altitude.
7. **Home.** You fly it home and unload.

## 2. The mining drone (decided: SCAM Min3r 69R)

You asked for vertical atmospheric drills from the workshop. These are the vertical, atmospheric, dockable miners
among your subscriptions, from their blueprints, with masses estimated from the game's block data:

| Miner | Workshop | Dry mass | Size | Drills | Lift (dry) | DLC | Docks |
|---|---|---|---|---|---|---|---|
| **[SCAM] - Min3r 69R Atmospheric** (recommended) | 2572218085 | 40 t | 5.5 x 7 x 5.5 m | 4, down | 5.3 g | none | connector on its belly |
| Small Square Miner PAM rdy | 3009896867 | 94 t | about 8 x 10 x 8 m | 9, down | 4.5 g | none | connector on its back (hangs) |
| [PAM]-Auto Miner - Example Ship (Atmo) | 1606630169 | 108 t | about 10 x 6 x 8 m | 12, down | 4.4 g | none | connector |
| Archworm Vertical Miner Drone Lodestar | 3790530892 | 129 t | about 6 x 16 x 6 m | 6, down | 1.6 g (also H2 and ion) | 7 DLCs | connector |

**Recommended: SCAM Min3r 69R.** It's light (4 of them weigh 161 t), all-vanilla, and atmospheric only. Its
connector points straight down between its 4 drills, so it lands upright on a pad. Loaded with its 31 m³ of
ore (about 84 t) it still lifts 1.7 g. The connector face and the drill tips are level, so docked on a large
connector its drills hang 2.5 m above the deck surface. Atmospheric exhaust doesn't damage blocks.

The miners stay the workshop author's work, so they aren't copied into this repo. The one-paste
"carrier + 4 miners" blueprint is assembled on your PC at install time from your subscribed copy and written
only to your local blueprint folder.

## 3. The carrier: FS Mining Carrier Atmo

Large grid, atmospheric, built by `tools/shipgen.py`, checked by FleetPatch and `tools/connect.py` like the
others.

**Lift against cargo.** Ore weighs about 2.7 t per m³, so a full Large Cargo Container (422 m³) holds about
1,140 t. This world uses 1x block inventories. The table shows sea-level lift-to-weight with 4 SCAM miners
docked. "Full" means the hold full and every miner full. The base hull is estimated at 640 t.

| Hold | Large atmo lift thrusters | Empty + miners | Full | TWR empty | TWR full | Thrust power |
|---|---|---|---|---|---|---|
| 1 container, 422 m³ | 8 | 1,067 t | 2,544 t | 4.5 | 1.87 | 134 MW |
| **2 containers, 844 m³** | **12** | **1,202 t** | **3,818 t** | **5.9** | **1.87** | **202 MW** |
| 2 containers | 10 | 1,136 t | 3,752 t | 5.2 | 1.58 | 168 MW |
| 3 containers, 1,266 m³ | 12 | 1,205 t | 4,959 t | 5.9 | 1.44 | 202 MW |
| 3 containers | 14 | 1,271 t | 5,025 t | 6.6 | 1.66 | 235 MW |

**Proposed: 2 Large Cargo Containers and 12 large atmospheric lift thrusters.** That gives 1.87 fully loaded at
sea level. Atmospheric thrusters lose thrust as the air thins, so that leaves room for a few hundred metres of
altitude, a hill, or a heavier ore. The `MinTWR` guard in section 1 enforces the limit live. A third container
drops a full carrier to 1.44, too thin once you climb. The container count is one setting in the generator,
which sizes the lift to match.

**Layout:**
- **Deck:** 4 landing pads in a 2 x 2 square, 10 m apart. Each is an upward-facing large connector tagged
  `[Dock]` with a clear 6 m footprint around it.
- **Hull:** a keel under the deck holding the cargo hold (2 Large Cargo Containers), 12 large atmospheric lift
  thrusters on the underside, and forward, reverse and lateral thrusters.
- **Power:** 1 large reactor (300 MW) plus 4 large batteries as buffer.
- **Control:** gyros, and an Industrial Cockpit with Mining, Map, Miners and Cargo screens.
- **Ground:** a large ore detector on the belly and 4 landing gears.
- **Comms:** an antenna at 10 km.
- **Fuel:** you load uranium by hand; inventory can't travel in a blueprint.

## 4. Finding ore (mods already in your world)

| Mod | What it gives a script | Use |
|---|---|---|
| **OreNet Detector PB Integration V10** (3722504614) | `OreNet.Deposits`: each deposit's ore, world position and volume in m³, nearest first, up to 32 per ore; `OreNet.Nearest`. Works on planets and asteroids, scans in the background. | **Primary.** |
| OreDetectorBridge (3745705757) | `DetectedOres` text: `Ore|x|y|z;...` rounded to 10 m | Fallback. It only scans asteroids (`IMyVoxelMap`), so it returns nothing on a planet, and it reads voxels on the game thread (lag). |
| Ore Detector Raycast (2200909982) | Raycast to a point and get the ore there | Optional: confirming a GPS target. |

Range is the detector's own slider: 150 m for a vanilla large detector. The carrier therefore parks over the
field, and miners re-survey from their shaft position as they go. When no detector mod is loaded, `mine GPS:...`
still works.

## 5. Software (decided: our own Miner script)

### Option A (chosen): our own `Miner` script

The Fleet System's Drone script already flies, docks to a moving carrier with velocity feed-forward, registers
over IGC and takes reliable orders. The miner script keeps that and swaps combat for mining:

- **States:** DOCKED, UNDOCK, TRANSIT, SURVEY, BORE, HOP, ASCEND, RETURN, APPROACH, FINAL, UNLOAD, CHARGE.
- **Boring:** drills on, a slow controlled descent along gravity, held centred on the shaft by the flight
  controller. Its depth limits:
  - an adaptive depth that stops deepening when the share of ore in what it digs drops below a threshold;
  - `MaxDepth` as a hard cap;
  - a stall that won't clear: back up and retry, then move to the next shaft.
- **Shaft grid:** N x N shafts per deposit, spaced by the drill-head width. Miners that share a deposit get
  different cells of the grid.
- **Stone:** ejected through connectors tagged `[Eject]`, or hauled back as cargo (`KeepStone`).
- **Energy reserve:** it turns back when the battery left wouldn't cover the trip home times a safety factor.
- **Talks to the carrier:** cargo fill, ore share and depth go up the link; new jobs, recalls and "carrier
  moving" come down.

**Scripts and size.** The carrier runs `MineCore`. The miner runs the Fleet System Drone script in its Miner role
(section 9), which reuses its docking, registration and flight code as they are. A `MineUI` reusing the Hub UI's
drawing and arrow-key menu can come later. Everything stays C# 6 and under the 100k limit.

**Why recommended.** We own all of it, everything runs through one protocol, and it can be shared freely.

### Option B: run LODESTAR on the miners

LODESTAR (workshop 3778984469, v1.2.3) is a point-and-mine autopilot that already does the hard parts: adaptive
depth, shaft grids, stone ejection, stall handling, and docking to a moving carrier. Its interfaces, read from
the script:

- **Carrier beacon:** an IGC broadcast on tag `LSDK` with
  `(position, forward, up, velocity, angular velocity, name)`, from a carrier running LODESTAR with
  `Role=carrier`. Miners dock to it after one `dock learn` while docked.
- **Telemetry:** broadcasts on `LSTW`: `(grid name, status, position, velocity, (battery, H2, cargo, ...),
  warning)`.
- **Commands** (`LSCC`, which STARWATCH uses): `halt`, `stop`, `recall`, `resume`. There's no remote "mine
  here".
- **Running it directly:** a docked miner's PB is reachable from the carrier, which can `TryRun` it with
  `mine GPS:...` and `dock learn`. So the carrier would assign jobs at launch.

In this option, our carrier script sends the `LSDK` beacon itself, assigns jobs through `TryRun` while the miner
is docked, and reads `LSTW` for the screens.

**Downsides:**
- **Less control:** jobs can only be changed while a miner is docked, and we depend on LODESTAR's protocol
  staying the same between versions.
- **Can't reuse its code:** the script is minified (one-letter names) and has no license, so its code can't go
  into this repo. You'd install it on the miners from your own workshop copy. What's reusable is its design,
  which we can reimplement in Option A.

## 6. Build order

1. **Carrier blueprint (done):** see section 7.
2. **MineCore (written, not yet tested in game):** see section 8.
3. **Miner (written, not yet tested in game):** see section 9. First milestone in game: one miner mines one GPS
   shaft and unloads.
4. **Dispatch:** 4 miners, one deposit each or shared shaft grids, depletion and re-survey, aborts.
5. **Acceptance tests** in your world: a full run to CARRIER FULL, the lift guard, and recall.

## 7. Built so far: the carrier blueprint

**FS Mining Carrier Atmo** comes from `tools/shipgen.py` and goes in `blueprints/`. It passes the block check and
the connectivity check, with no loose blocks.

**Size and lift:**

| | Mass | Lift-to-weight (sea level) |
|---|---|---|
| Dry | 1,117 t | 6.4 |
| With 4 SCAM miners docked | 1,278 t | 5.6 |
| Hold full and 4 full miners | 3,893 t | **1.83** |

**Thrust and dimensions:**
- **Forward and reverse:** 2 large atmospheric thrusters each way, giving 10.4 m/s² empty.
- **Sideways:** 3 small atmospheric thrusters each side, giving only 1.6 m/s² empty. That's weak; strafing a
  full carrier will be slow.
- **Size:** 42.5 x 35 x 70 m.

**Blocks:**
- **Lift:** 12 large atmospheric thrusters in two rows of 6 along the keel sides.
- **Hold:** 2 Large Cargo Containers, `MINE_CONTAINERS` in the generator.
- **Power and control:** a large reactor, 4 batteries, 12 gyros, and two programmable blocks,
  `[Mine Core]` and `[Mine UI]`, with no scripts yet.
- **Pads:** 4 upward-facing deck connectors, `Connector [Dock] Pad 1-4`. The generator checks that nothing
  above the deck sits within 3 m of a pad.
- **Ground:** a belly ore detector and 4 landing gears, all clear of the lift exhaust.
- **Cockpit and comms:** an Industrial Cockpit tagged for the future Mining, Map, Miners and Cargo screens, and
  an antenna at 10 km.

**One paste with the miners:** `python3 tools/minefleet.py` writes **FS Mining Carrier Atmo + 4 SCAM Miners**
to your local F10 folder only. It does this:
- reads your subscribed copy of workshop item 2572218085;
- gives each miner fresh entity ids and makes you the owner of its terminal blocks;
- stands each miner upright on a pad, with its belly connector recorded as connected;
- checks each miner's bounding box against the carrier and the other miners.

The miners keep their own SCAM scripts until the Miner script exists.

## 8. MineCore (the carrier script)

`src/MineCore/Program.cs`, built to `dist/MineCore.cs`. It is 47.5% of the PB character limit. It's already
inside the `Programmable Block [Mine Core]` in both carrier blueprints. It reuses the Fleet hub's registry,
docks and reliable orders (`src/HubCore/Drone.cs`, `Docks.cs`) and the shared network layer.

**Its own network.** `NetId` defaults to **2**; the Fleet hub uses 1. That keeps a miner from registering with
the combat carrier, and a Buster from registering with the mining carrier. A miner docked on a pad is
provisioned with NetId 2 and the key automatically, the same way the Busters are.

**What it does:**

- **Survey (every 4 s):** reads `OreNet.Deposits` from every ore detector on the carrier. If that mod isn't
  loaded, it reads OreDetectorBridge's `DetectedOres` instead.
  - Reports of the same ore within `MergeDistance` (16 m) are one deposit.
  - The list persists, up to 48 deposits.
  - A deposit well inside detector range that stops being reported for 5 surveys in a row is marked
    mined out.
- **Dispatch (2 Hz):** each miner gets one shaft at a time, with an `O_MINE` order.
  - **Deposit choice:** it picks deposits of the chosen ores, taking one nobody is mining before a shared one,
    then the biggest, then the nearest.
  - **Shaft grid:** each deposit has a `ShaftGrid` x `ShaftGrid` (3 x 3) grid of shafts, `ShaftSpacing`
    (5 m) apart. Shafts are handed out centre first, so no two miners share one.
  - **Full:** a miner that reports `F_FULL` is sent to a free pad.
  - **Shaft done:** one that reports `F_DONE` gets the next shaft, or a pad when nothing is left.
  - **Launching:** a docked miner leaves for its next trip once it's unloaded and charged above
    `LaunchBattery` (70%). Launches are `LaunchInterval` (3 s) apart.
- **Unload (2 Hz):** moves ore from whatever is docked on a pad (its cargo containers, drills and connectors)
  into the carrier's cargo containers, a few stacks per run. This works with any ship on a pad, including
  today's SCAM miners running their own script.
- **Lift and hold guard:** before each launch it checks the carrier could still carry everyone coming back
  full. If not, it stops launching and logs **CARRIER FULL (LIFT / HOLD FULL)**.
  - **Lift needed:** the carrier's mass now, plus every miner out at `MinerMass` + `MinerLoad`
    (40 t + 85 t), plus this miner's load.
  - **Lift available:** the effective thrust of the thrusters pointing up, in the current air and gravity,
    divided by `MinTWR` (1.4).
  - **Hold:** free volume must cover every load out plus this one (`MinerCargo`, 31 m³ each).
- **Pages:** plain text on the cockpit screens. The Industrial Cockpit is already tagged for them.

  | Screen | Tag | Shows |
  |---|---|---|
  | 0 | `[FS:Mine]` | Overview and recent events |
  | 1 | `[FS:Map]` | Deposits: ore, volume, distance, shafts done, miners on it |
  | 2 | `[FS:Miners]` | Each miner's state, cargo, battery and shaft |
  | 3 | `[FS:Cargo]` | Hold fill, lift-to-weight against the guard, mass against the lift limit, loads left, ore by type |

**Commands** (run the Mine Core PB with an argument, or bind it to the toolbar):

| Command | Effect |
|---|---|
| `mine <ore[,ore...]>` / `mine any` | Start mining those ores (`Iron`, `Nickel`, `Gold`...). `any` means everything but stone and ice. |
| `mine GPS:name:x:y:z:` | Add a deposit by hand (paste a GPS) and mine it |
| `mine off` | No more trips; miners already out finish their load |
| `recall [miner\|all]` | Everyone home now; `all` also stops mining |
| `survey` | Read the detectors now and log the nearest deposits with their GPS |
| `deposits clear` | Forget all deposits |
| `mintwr <x>` | Set the lift guard |
| `provision` / `forget <callsign>` / `reload` | As on the Fleet hub |

**Miner side of the protocol.** These definitions are in `src/Shared/Proto.cs`. The wire formats are unchanged,
so the protocol version stays 4.

- **`O_MINE` (order 12):**
  - `a`: the shaft's axis point at the deposit's depth.
  - `b`: up (unit vector) times the metres to dig past that point.
  - `c`: (shaft index, shafts in the grid, `MaxDepth` below the surface).
  - `id`: the deposit.
  - All in world coordinates.
- **Flags:** `F_FULL` (128) means cargo full, send me home. `F_DONE` (256) means the shaft is finished with room
  left. The miner clears both when it starts a new order.
- **States:** `S_TRANSIT` (19), `S_BORE` (20), `S_ASCEND` (21).

**What can be tested before the Miner script exists:**
- the survey, the deposit list and the map page;
- the lift and hold numbers on the cargo page;
- unloading the SCAM miners' ore;
- `mine GPS:...` adding a deposit.

**What can't:** dispatch needs miners that register as role `Miner`.

**Mass reading still to confirm.** The guard uses the cockpit's `CalculateShipMass().PhysicalMass`, which should
include the miners docked on the pads. The cargo page shows the mass it reads, so this can be checked in game.

## 9. The Miner role (in the Drone script)

Mining is a role in `src/Drone/Program.cs`, not a separate script. A miner reuses the drone's registration,
reliable orders, undocking and docking to a moving carrier, and its flight controller.

**When a drone is a miner.** Any drone whose grid has drills, or whose PB name contains "Miner", defaults to
`Role=Miner`. It registers with MineCore as `MINER-01` and so on. Miner defaults differ from a Buster's:
`MaxSpeed` 40 and `MinAltitude` 25.

**One shaft (`O_MINE`):**

1. **TRANSIT:** flies to a point over the shaft axis, at its own height or at least 20 m over the deposit,
   raised to `MinAltitude` over the ground.
   - **Around the carrier:** it routes around the hull, using the carrier's box from the frame broadcast.
   - **Attitude:** throughout the shaft, gyros hold the drills straight down whichever way the remote control
     faces.
2. **BORE:** drops at 3 m/s to just above the ground, turns the drills on, then lowers at `BoreSpeed`
   (0.8 m/s) while holding the axis. The commanded depth never runs more than 1.5 m ahead of the drill tips,
   so the ship leans on the drills rather than ramming the rock.
3. **The shaft ends when one of these happens:**
   - **Full:** cargo reaches `FullAt` (92%). It sets **F_FULL**, and the hub sends it to a pad.
   - **Deep enough:** it reaches the depth the hub asked for (`DigBelow` past the deposit's centre), or
     `MaxDepth` below the surface. It sets **F_DONE**, and the hub sends the next shaft.
   - **Out of ore:** a `NoOreRun` (6 m) stretch below the deposit's centre gives no new ore. It sets F_DONE.
   - **Low battery:** the battery drops below `MineReserve` (35%). It asks to go home, and the hub sends it to
     a pad.
4. **ASCEND:** climbs straight up the shaft with the drills still turning, to 10 m over its mouth, then holds
   for the hub's next order.

**Orders that arrive mid-shaft.** A recall or dock order that arrives while it's down a shaft waits until it
has climbed out. A resent order for the same shaft just continues.

**Stone.** It goes out through connectors tagged `[Eject]`, which are set to throw out; the script moves up to
2 t a second into each. The SCAM miner has none, so it hauls its stone back. That fills its cargo sooner, and
the carrier's hold too.

**Settings** (miner PB Custom Data, `[Drone]` section): `BoreSpeed`, `FullAt`, `MineReserve`, `NoOreRun`, plus the
usual drone settings.

**The paste.** `tools/minefleet.py` puts the Drone script in each SCAM miner's main PB (renamed
`Programmable Block [Fleet Drone] [Miner]`) and leaves its second PB empty. The SCAM blueprint's PBs carried
no code anyway: its automation is a separate workshop script.

**What happens when the fleet comes up:**
1. MineCore locks the pads.
2. It provisions each miner with NetId 2 and the key.
3. The miners register.

**To test, in game:**
1. Paste **FS Mining Carrier Atmo + 4 SCAM Miners** over or near ore.
2. Load uranium into the reactor.
3. Wait for `MINER-01` to `MINER-04` to show on screen 2.
4. Run `survey`, then `mine any`, or `mine GPS:...` for a known spot.
5. Watch screen 2 for each miner's state (TRANSIT, BORE, ASCEND) and screen 3 for the hold filling.

**Things to watch on the first runs:**
- **Bore speed:** whether 0.8 m/s suits 4 small drills. If the ship sits on the rock, the speed doesn't matter,
  since the drills set the pace.
- **Height reading:** whether the planet-elevation reading puts the start of boring near the ground.
- **Mass reading:** whether the carrier's mass includes the docked miners. The cargo page shows the mass it
  reads.
