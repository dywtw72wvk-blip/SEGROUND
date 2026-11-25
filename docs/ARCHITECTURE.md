# Fleet System — Architecture Plan (v2)

In-game Programmable Block (PB) scripts for Space Engineers. They're built in two parts:

- **Hub**: one script that runs either a mobile **Carrier** or a static **Base**, switchable at runtime. Each mode has its own strengths.
- **Drones**: a shared drone core plus role modules. The first role is the **Buster**, a small, cheap defense drone built in numbers. It's inspired by the Bobiverse busters: lots of small drones screening and protecting the mothership.

Status: **Phase 1 implemented** (`src/`, `dist/`, `docs/SETUP.md`), awaiting the in-game acceptance test. Phases 2–4 are planned.

---

## 1. Design principles

1. **Drones don't care which mode the hub is in.** The hub always broadcasts a *reference frame*: position, velocity, orientation, angular velocity and a keep-out sphere. A base's frame just never moves. Drone orders are relative to that frame. Switching Carrier⇄Base changes *what tasks* the hub hands out, not *how* drones fly.
2. **Leave combat to the game, coordinate with scripts.** In-fight behaviour comes from vanilla blocks: AI Offensive/Defensive combat blocks, turrets and custom turret controllers on the drone. Our scripts handle the fleet side: launch, positioning, rotation, recall, docking, recharge, rearm, fabrication, UI. Built-in combat is server-friendly, survives updates, and keeps PB cost low.
3. **Drones are typed by capability.** Each drone advertises its role and capability flags. The hub assigns tasks by capability, so new drone types plug in without changing the hub.
4. **Shared core, thin roles.** Comms, flight control, docking and telemetry are written once and shared by every drone type.
5. **Stay within SE's limits, and multiplayer-safe from day one.** Each PB has a 100k-character limit and a per-run instruction cap, and servers throttle PBs that average roughly 0.3–0.5 ms. Work is spread across ticks by a scheduler, and the hub is split across several PBs. See §10.
6. **Predictable failure.** Every drone has a defined fallback for losing its link, running low on power or taking damage. There's a global RECALL and a global SAFE (stand down) switch.

---

## 2. System overview

```
┌──────────────────────────── HUB GRID (Carrier or Base) ─────────────────────────────┐
│                                                                                     │
│   [PB] HUB CORE  ◄──── local IGC ────►  [PB] HUB UI ──► LCDs / cockpit screens       │
│    • Drone registry                       • Fleet list   • Tactical map             │
│    • Mode: CARRIER | BASE | AUTO          • Hangar       • Mode / alert panel       │
│    • Tasking (escort, stations, patrol)   • Menu input (buttons / toolbar)          │
│    • Dock manager & logistics                                                       │
│    • Alert level (from vanilla turrets/sensors)                                     │
│    • Net: frames, orders, acks                                                      │
└───────────────┬───────────────────────────────────────────────▲─────────────────────┘
   broadcast: NAV│   unicast: ORDERS                              │ broadcast: HELLO, STATUS
                 ▼                                                │
     ┌──────── DRONE (small grid) ────────┐     ┌──────── DRONE ...
     │ [PB] DRONE CORE + ROLE (Buster)     │     │
     │  • Net client    • Flight control   │     │
     │  • Docking       • Telemetry        │     │
     │  • Role tasks: screen / patrol      │     │
     │ Vanilla AI combat blocks (engage)   │     │
     └─────────────────────────────────────┘     └──────
```

### Hub PB split

| PB | Update rate | Job |
|----|-------------|-----|
| **Hub Core** (required) | Update1 with the scheduler | Comms, registry, modes, tasking, docking, logistics |
| **Hub UI** (required) | Update10 | All sprite rendering and menu input. Rendering eats characters and instructions, so it lives on its own PB. It talks to Core over `TransmissionDistance.CurrentConstruct` IGC. |
| **Factory** (phase 3, base only, optional) | Update100 | Fabrication line control: projector, welders, merge release |

Button panels and toolbars send arguments to **Hub UI**. Hub UI forwards commands to Core. Core is the single source of truth.

---

## 3. Hub modes: Carrier vs Base

You switch modes with the `mode carrier`, `mode base` or `mode auto` argument.

- **AUTO** picks BASE when the grid `IsStatic`, or when it's *anchored*: speed below 0.1 m/s for at least 30 s, with landing gear locked or a connector locked to a static grid. Otherwise AUTO picks CARRIER.
- **Switching to BASE:** the hub keeps broadcasting its *live* frame, which for a static or anchored grid simply doesn't move. Escort slots become perimeter stations facing outward (base formation, default RING), base modules come online, and NAV drops to 1 Hz while the grid is stationary. It goes back up automatically if the grid moves. Forcing `mode base` on a moving ship therefore gives you base tasking with stations that still move with the ship. *(Implemented in phase 1.)*
- **Switching to CARRIER:** this is refused if the grid is static. Patrols are recalled to escort slots, NAV goes up to 20 Hz and carrier modules come online.

### What each mode gains

| Area | **CARRIER** (mobile) | **BASE** (static) |
|------|----------------------|-------------------|
| Positioning | **Moving screen.** Escort shell around the carrier, biased toward the direction of travel, with forward pickets on the route. Feed-forward of velocity and rotation keeps drones on station at speed. | **Fixed perimeter.** Stations on a ring or sphere at a configured radius, patrol loops from a GPS list, and distant pickets for early warning. |
| Launch / recovery | **Hot recovery** (velocity-matched docking while moving) and **scramble** (release all docked drones at once). | Slow, precise docking with no velocity matching, so it's cheaper on battery and fuel. |
| Logistics | **Jump interlock.** `jump prep` recalls everything and keeps the jump drives disabled until every drone is docked and locked, or you force it. Power-aware charging staggers recharges when the carrier's power is tight. | **Fabrication.** Projector, welders and a merge block build replacement Busters to hold a target stock. **Repair bay:** welders at the docks. **Rearm and refuel:** sorters push ammo, uranium and ice. |
| Capacity | Limited by hangar slots and power. | Large reserve. **Alert levels** (GREEN, YELLOW, RED) launch more of the reserve as the hub's own turrets and sensors report hostiles. |
| Script cost | Higher: 20 Hz frames and moving-frame maths. | Lower: the frame is static and cached by drones. |
| Drawback | Smaller fleet and a shorter time on station. | Can't relocate, so it relies on fabrication to stay stocked. |

Both modes share the registry, UI, docking, the alert system and provisioning.

**Provisioning:** while a drone is connected through a connector or merge block, it's in the same terminal system as the hub. The hub writes the drone PB's CustomData (callsign, network key, role settings) and calls `TryRun("provision")`. Keys travel physically, never over the air. This also boots freshly fabricated drones.

---

## 4. Network protocol (IGC)

Payloads are `MyTuple`, `Vector3D` and primitives. Avoiding string-encoded numbers avoids locale and decimal-comma bugs. Every message starts with a header: `(int version, int netId, int keyHash, int type, long seq)`.

| Tag | Direction | Transport | Rate | Payload |
|-----|-----------|-----------|------|---------|
| `FS{net}/nav` | Hub → all | broadcast | 20 Hz carrier / 1 Hz base | frame (pos, vel, fwd, up, angVel), keep-out sphere, mode, alert level |
| `FS{net}/hello` | Drone → hub | broadcast | on boot, then every 5 s until registered | role, capability flags, performance (max accel, max speed), grid name |
| `FS{net}/sts` | Drone → hub | broadcast | 2 Hz | state, pos, vel, battery, H2, ammo, integrity, last acked seq, current task |
| `FS{net}/ord` | Hub → drone | unicast | when an order is issued | REGISTER, ESCORT(slot), STATION(local pos), PATROL(route), GOTO, HOLD, DOCK(connector frame), UNDOCK, ENGAGE_ON / ENGAGE_OFF, RTB, SAFE |
| `FS{net}/local` | Core ⇄ UI ⇄ Factory | broadcast, `CurrentConstruct` | as needed | UI snapshot, user commands, factory state |

- **Reliability.** A drone echoes the last `seq` it applied in its STATUS. The hub resends any un-acked order with backoff, and marks the drone *unresponsive* after N tries.
- **Security.** `netId` plus a key hash filter out other fleets and casual snooping. This is obfuscation, not encryption, because broadcasts are audible to anyone in antenna range.
- **Range.** IGC needs antenna coverage both ways. The hub antenna should cover the patrol radius. Relay drones come in phase 4.

---

## 5. Drone core (shared by all roles)

**State machine host:**

```
BOOT → REGISTERING → DOCKED ⇄ (UNDOCKING → ACTIVE[task] → RETURNING → DOCK_APPROACH → DOCK_FINAL) → DOCKED
                                               │
                         link lost ────────────┴──► HOLD_SAFE (station-keep on last frame, then RTB timer)
```

- **Flight controller:**
  - **Thrust:** a six-direction thrust allocator that uses `MaxEffectiveThrust`, so atmosphere and altitude are handled. It compensates for gravity and uses velocity feed-forward from the hub frame.
  - **Gyros:** aims any chosen block's forward and up vectors. The ship controller is used for travel, the connector for docking.
  - **Speed and avoidance:** speed is capped by a braking-distance profile, and paths route around the hub's keep-out sphere.
  - **Disturbance compensation** (lightweight, on by default; turn off with `DisturbanceComp=false`): each tick it compares the measured acceleration with what thrust and gravity should produce, low-pass filters the gap, and feeds it back. In vanilla this soaks up unmodelled effects: cargo or mass changes, damaged or missing thrusters, collisions while docking, and the hub's frame accelerating between NAV packets. It also leaves the door open if you add an aero mod later.
- **Docking:**
  1. Fly to an approach point 20–30 m out along the assigned connector's axis, measured in the hub frame.
  2. Align, then make a slow final approach until the connector reports `Connectable`, then `Connect()`.
  3. Once docked: batteries recharge, H2 tanks stockpile, thrusters idle.
- **Telemetry:** batteries, H2, ammo or inventory, and integrity (the fraction of the drone's initial blocks still working). Status is reported every 30 ticks.
- **Safety:**
  - Low battery or low integrity makes the drone request RTB.
  - On SAFE, it drops combat blocks to passive and holds.
  - All block queries are filtered to `IsSameConstructAs(Me)`, so a docked drone never touches hub blocks.

## 6. First role: Buster (defense drone)

**Concept.** Small-grid, lightweight and cheap, deployed in numbers as a protective screen. Losses are expected, and the base's fabrication line replaces them.

**Build template** (documented as a blueprint checklist):
- programmable block
- remote control, plus vanilla AI blocks (AI Flight / AI Defensive or AI Offensive combat block)
- small antenna
- connector
- batteries (battery only for now)
- gyros and thrusters
- optional fixed or turret weapons, and a camera

**Script responsibilities** (the fleet side):

| Task | Behaviour |
|------|-----------|
| SCREEN | Take an assigned slot in the defensive shell (carrier) or a perimeter station (base). |
| PATROL | Fly a loop of base-frame waypoints. |
| ENGAGE_ON / ENGAGE_OFF | The hub enables or disables the drone's vanilla AI combat block and sets its leash radius around the station. The game's AI does the fighting, and the script takes control back when the hub disengages or the leash is exceeded. |
| ROTATION | The hub cycles drones through charging so the screen strength stays above a minimum. |
| RTB / SCRAMBLE | Mass recall to free docks, or mass launch, with staggered departures to avoid collisions at the hangar. |

**Hub-side defense coordination** stays fleet-level:
- Alert level comes from the hub's own turrets and sensors.
- The alert level sets how many Busters are on screen versus in reserve.
- Rotation is balanced across battery levels.
- Losses are logged and fabrication is triggered (in base mode).

---

## 7. UI plan (Hub UI PB)

Screens are assigned by name tags on LCDs or cockpit surfaces (`:n` picks the surface index):

| Tag | Screen |
|-----|--------|
| `[FS:Fleet]` | Drone list with callsign, role, state (colour-coded), battery and ammo bars, distance and task. It also shows a detail panel for the selected drone, an action bar, and an event log. |
| `[FS:Map]` | Top-down tactical map in the hub frame: range rings, elevation stems, drones coloured by state, screen and perimeter stations, keep-out sphere. |
| `[FS:Hangar]` | Docks (free / reserved / occupied), charge progress, fabrication queue and stock against target. |
| `[FS:Mode]` | Current mode with its active modules, alert level, jump interlock status, and the big RECALL / SAFE indicators. |

**Input** comes as arguments, so button panels and cockpit toolbars both work. Menu navigation uses `up`, `down`, `left`, `right`, `apply` and `back`. Direct commands include `recall all`, `scramble`, `mode base`, `alert red`, `jump prep`, `formation ring`, `spacing 60` and `goto GPS:...`.

**Rendering:** sprites are scaled to `SurfaceSize` with a viewport offset, so the same screen works on square and wide LCDs. Draws alternate a dummy sprite to work around the multiplayer sprite-cache bug.

---

## 8. Code organisation (MDK2)

```
fleet-system/
  docs/ARCHITECTURE.md
  Shared/                    # MDK2 shared project (mixins), compiled into each script
    Protocol.cs              # message types, tuple layouts, version constants
    Net.cs                   # IGC wrapper: channels, key filter, seq/ack/resend
    Config.cs                # MyIni CustomData wrapper with defaults + validation
    Scheduler.cs             # tick budgeting, IEnumerator coroutines
    Frames.cs                # hub-frame <-> world transforms
    Telemetry.cs             # battery / gas / inventory / integrity readers
  Hub/
    Core/Program.cs          # registry, modes, tasking, dock manager, alert, provisioning
    Core/Modes/{IHubMode, CarrierMode, BaseMode}.cs
    UI/Program.cs            # screens + menu
    Factory/Program.cs       # (phase 3)
  Drones/
    Core/                    # flight controller, docking, state host (shared by roles)
    Buster/Program.cs
  tools/stubs/               # minimal API stubs for offline syntax checks (mcs) here;
                             # real builds use MDK2 against the game DLLs on your PC
```

Each PB ships as one minified paste-in script, produced by the MDK2 packager. I'll keep the source modular, and the build step merges it for each PB.

---

## 9. Roadmap

| Phase | Deliverable | Test in game |
|-------|-------------|--------------|
| **1. Foundations** | Shared libs, Hub Core (registry, frame broadcast, Carrier/Base/Auto switch, dock manager), drone core (flight, docking, telemetry), Hub UI fleet list | One drone: register, launch, escort, recall and dock, on both a moving carrier and a static base |
| **2. Buster** | Buster role: screen, patrol, ENGAGE toggling of vanilla AI blocks with a leash, rotation, scramble and RTB; alert levels; map screen | 8–12 Busters cycling through charge while the screen is held |
| **3. Mode extras** | Base: fabrication line, repair and rearm, hangar screen. Carrier: jump interlock, hot recovery, power-aware charging | Base rebuilds lost drones; carrier recovers drones under way and jumps safely |
| **4. Expansion** | New roles via capabilities (scout/relay, miner, hauler, repair), multi-hub handover (a carrier docking at a base) | — |

---

## 10. Multiplayer readiness (decided: test in single-player, migrate to MP later)

Everything below is built in from phase 1, so moving to a server later is just a config change, not a rewrite.

| Concern | Built-in handling |
|---------|-------------------|
| **Runtime budget** | Design target: **≤0.3 ms average per PB**. That's a typical Torch PB-limiter threshold. Every script shows a rolling average on its screen and in Echo. Each phase must pass a budget check in single-player before it's considered done. |
| **Update-rate restrictions** | Some servers restrict or penalise Update1. `Perf=High\|Normal\|Low` in CustomData changes the drone and hub rates: High is Update1, Normal mixes Update1 and Update10, Low is Update10 only. The flight controller gains are scaled to the tick rate, so drones still fly at Low, just more loosely. |
| **Server restarts / reloads** | All persistent state is saved to `Storage`: mode, registry, dock reservations, orders with their seq numbers, and the fabrication target. On reload, the hub rebuilds the fleet state and re-issues current orders. Drones re-announce themselves with HELLO and resume. |
| **Ownership & permissions** | A PB can only control blocks with matching ownership or faction sharing. At boot, each script checks ownership of every block it needs and lists the mismatches. Fabricated drones inherit the welder owner's ownership, and the Factory checks this. |
| **Script lag / sim speed** | Control loops use real elapsed time (`Runtime.TimeSinceLastRun`), not tick counts, so they stay stable when sim speed drops below 1.0. |
| **Sprite sync** | Displays are driven only from the server-side PB, with the dummy-sprite alternation that fixes stale screens for clients who join late. |
| **Other fleets / PvP** | `netId` and the key hash keep multiple players' fleets on one server from cross-talking. Orders go unicast only. Keys are provisioned on dock, never broadcast. |
| **World settings** | The server must allow in-game scripts. The setup guide lists this, plus the settings that affect drones: the speed cap and the antenna broadcast rules. |

---

## 11. Open questions

1. ~~Single-player or multiplayer?~~ **Answered:** single-player for testing, migrating to MP later (see §10).
2. ~~Vanilla or mods?~~ **Answered:** fully vanilla. The aerodynamics mod is being removed, and there are no modded weapons. Busters use vanilla weapons and vanilla AI combat blocks, and the vanilla 100 m/s speed cap is the default `MaxSpeed`.
3. ~~Grid sizes?~~ **Answered:** large-grid hub, small-grid Busters.
4. ~~Base fabrication?~~ **Answered:** yes. Projector, welders and merge-block fabrication line in phase 3.
5. ~~Busters' power source?~~ **Answered:** battery only for now. The drone core still reads H2 in its telemetry, so hydrogen drones can be added later without a protocol change. Buster docking switches batteries to Recharge, and RTB triggers on battery percentage.
