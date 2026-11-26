# Fleet System — Setup & Phase 1 Test Guide

Phase 1 covers the fleet foundation: a hub (carrier or base), generic drones that register, escort in formation, hold, go to GPS, dock, undock and report status, plus the Hub UI fleet list.

The defense role (Buster) arrives in phase 2. It reuses everything here.

## 0. World settings

- **In-game scripts** must be enabled in the world settings. Depending on your game version, this may also need **Experimental Mode** turned on in the game options.
- Vanilla is fine. The default drone `MaxSpeed=100` matches the vanilla speed cap.

## 1. Files

Paste-ready scripts are in `dist/`. Each is one file per programmable block (PB):

| File | Goes on | Notes |
|------|---------|-------|
| `dist/HubCore.cs` | Hub PB #1 | The brain. Required. |
| `dist/HubUI.cs` | Hub PB #2 (same ship or station) | Screens and buttons. Required for the UI. |
| `dist/Drone.cs` | One PB on every drone | |

If you edit `src/`, regenerate `dist/` with `python3 tools/build.py`. Add `--check` to compile-check against API stubs, which requires Mono's `mcs`.

**Easiest: automatic install.** Name the programmable blocks in game: `[Fleet Core]` on the hub's main PB, `[Fleet UI]` on the hub's screen PB, and `[Fleet Drone]` on each drone's PB. Save, exit the game, then run `install.bat`. It writes the scripts straight into those blocks in your saved worlds and blueprints, with backups. See the README for details.

To install a script manually:
1. Open the PB and click **Edit**.
2. Select all, paste the file, then click **Check Code**.
3. Click **Remember & Exit**.

## 2. Build the hub (carrier or station)

**Required blocks:**
- **2 programmable blocks**: Hub Core and Hub UI.
- **Cockpit or remote control, facing the ship's forward direction.** Its orientation defines the formation axes. Without one, the hub uses the Hub Core PB's orientation, so mount that PB facing forward. To use a specific controller, put a tag in its name and set `ControllerTag`.
- **Antenna**, turned on with broadcasting enabled. Its range should cover your formation distance plus some margin. 2–5 km is plenty for testing.
- **Connectors for docking**, each with `[Dock]` in its name, for example `Connector [Dock] 1`. Leave clear space in front of each one. Drones approach along the connector's axis from about 25 m out.
- **An LCD** with `[FS:Fleet]` in its name. For a cockpit screen, add the screen index, for example `Cockpit [FS:Fleet:1]`.
- **Optional:** a button panel, or toolbar slots, that run the Hub UI PB with these arguments:

  | Argument | Does |
  |----------|------|
  | `up` / `down` | Select a drone, or ALL |
  | `left` / `right` | Choose an action |
  | `apply` | Run the action |
  | `back` | Select ALL |

**Configuration:** the first time Hub Core runs, it writes all its options to its **Custom Data**:

```ini
[Hub]
NetId=1               ; fleet channel; all your PBs must match
Key=change-me         ; CHANGE THIS. Drones get it automatically when docked.
Mode=Auto             ; Auto | Carrier | Base
CarrierFormation=WEDGE
BaseFormation=RING
Spacing=40            ; metres between slots
KeepOutRadius=0       ; 0 = auto (hub's bounding sphere)
DockTag=[Dock]
ControllerTag=
AutoRTB=true          ; send drones home on low battery / damage
RtbBattery=0.25
RtbIntegrity=0.6
LostAfter=10          ; seconds of silence before a drone is LOST
AutoProvision=true    ; push NetId/Key into docked drones
Perf=Normal           ; High | Normal | Low  (Low = Update10 only, server-friendly)
Waypoint=             ; GPS:... used by "goto" with no GPS
```

Edit it, then click **Recompile**. Hub UI has its own `[HubUI]` section. Its `NetId` must match Hub Core's.

## 3. Build a drone (small grid)

**Required blocks:**
- **Remote control**, facing the drone's forward direction.
- **Thrusters in all 6 directions.** Every thruster must sit on the same grid as the remote control (not on rotor subgrids).
- **Gyroscopes.**
- **Batteries.** Battery only for now; telemetry already supports hydrogen.
- **1 connector.** If the drone has several, tag the docking one and set `ConnectorTag`.
- **Antenna**, turned on with broadcasting enabled.
- **Programmable block** running `Drone.cs`.

**First run:**
1. The drone writes a `[Drone]` section with `NetId=0` and an empty `Key`. It shows **NOT PROVISIONED** and doesn't touch any controls.
2. Fly the drone manually and connect it to one of the hub's `[Dock]` connectors.
3. Within about 10 seconds, Hub Core writes NetId and Key into the drone and restarts its config. You can also run `provision` on Hub Core to do this immediately.
4. The drone registers and appears on the fleet screen with a callsign such as `DRONE-01`.

**Drone options** (Custom Data):

```ini
[Drone]
NetId=0 / Key=        ; filled in by provisioning
Role=Drone            ; phase 2: Buster
Perf=Normal
MaxSpeed=100
DockSpeed=1.5         ; final approach speed m/s
ApproachDistance=25   ; distance out from the dock connector before final approach
Clearance=15          ; extra margin around the hub keep-out sphere
RtbBattery=0.2        ; drone asks to go home below this
LinkTimeout=5
ConnectorTag= / ControllerTag=
DisturbanceComp=true
PositionGain=0.8 / VelocityGain=2.5 / GyroGain=3 / GyroMaxRate=3
GyroInvert=false      ; fallback: set true only if the drone rotates AWAY from its target heading
```

## 4. Commands

You can run these on Hub Core directly, or type them as Hub UI arguments. In Hub UI, the word `sel` is replaced with the selected drone.

| Command | Effect |
|---------|--------|
| `escort all` / `escort DRONE-01` | Take a formation slot. A docked drone undocks automatically first. |
| `hold all` | Hold position in place. |
| `goto all GPS:Spot:1:2:3:` | Fly there and hold. With no GPS, the `Waypoint` is used. |
| `dock all` / `rtb DRONE-02` | Return to a free `[Dock]` connector and dock (charge). |
| `undock all` | Undock and keep station near the hub. |
| `stop all` | Release controls. Dampeners on. |
| `formation next` / `formation ring` | Cycle or set: WEDGE, LINE, COLUMN, RING, SPHERE. This applies to the current mode. |
| `spacing 60` | Change slot spacing. |
| `mode auto` / `mode carrier` / `mode base` / `mode next` | Hub mode setting. |
| `autortb on` / `off` / `toggle` | Automatic return-to-base. |
| `waypoint GPS:...` | Store the default goto target. |
| `provision` | Push NetId and Key into docked drones now. |
| `forget DRONE-03` | Remove a dead drone from the registry. |
| `refresh` | Re-read Custom Data and rescan blocks. |

## 5. Phase 1 acceptance test

Run this in a creative test world. Tick each item, and note anything odd: the PB detail text shows state, warnings and runtime.

**A. Registration**
1. Hub PBs show no errors. Hub Core's detail text says `Mode: CARRIER` for a ship, or `BASE` for a station.
2. Dock a fresh drone. Within about 10 s it's provisioned, then `New drone DRONE-01` appears. The fleet screen lists it as `DOCKED`.

**B. Launch and escort (hub parked in space)**

3. `escort all`. The drone undocks, backs out about 25 m along the connector axis, then flies to its slot. Its order shows `ESCORT` (the `...` disappears once acknowledged).
4. **Gyro check:** the drone should turn smoothly to face the hub's forward direction. If it spins away or oscillates wildly, `stop all`, set `GyroInvert=true` on the drone, recompile, and tell me.
5. `formation next` a few times. The slot moves, and the drone repositions without crossing through the hub.

**C. Moving carrier**

6. Fly the carrier at 20–50 m/s, then turn gently. The drone keeps station with little lag.
7. Note the Drone PB runtime avg (target 0.3 ms or less) and the Hub Core runtime.

**D. Recall and dock while moving**

8. `dock all` while cruising. The drone approaches the connector axis, slows to about 1.5 m/s for the final approach, and connects. Once docked, its batteries switch to Recharge and its thrusters and gyros turn off.
9. Repeat with the carrier stopped.

**E. Base mode**

10. `mode base` on the stopped carrier, or use a static station. The formation becomes `RING`, and drones face outward.
11. `escort all`, then `goto all GPS:...`, `hold all`, then `dock all`.

**F. Resilience**

12. Recompile Hub Core mid-flight. The drones keep flying, show `reconnected`, and resume.
13. Turn the hub antenna off. Within 5 s drones go to `NO LINK` and hold. Turn it back on and they resume.
14. Drain a drone's battery below 25% (or set `RtbBattery=0.9` on the hub to test). You should see `auto-RTB (battery ...)` and the drone docks.
15. Grind some blocks off a drone. The HULL bar drops, and auto-RTB triggers below 60%.

**G. Multiple drones**

16. With 3 or more drones, `escort all` in each formation. Slots don't overlap. The dock queue works when there are more drones than connectors: extra drones show `DOCK (queue)`.

Report back:
- which steps passed,
- anything that looked wrong, such as the gyro direction, docking from the wrong side, or overshoot,
- the runtime numbers.

Those decide the tuning before phase 2 (the Buster).

## 6. Troubleshooting

| Symptom | Fix |
|---------|-----|
| Drone shows `NOT PROVISIONED` | Dock it to a `[Dock]` connector, or run `provision` on Hub Core. Check the drone's Custom Data has a `[Drone]` section. That section appears after the drone's first compile. |
| Drone stuck `searching...` | Check antennas on both sides are on and broadcasting with enough range, and that NetId and Key match. |
| UI says `NO LINK TO HUB CORE` | Hub UI `NetId` must equal Hub Core's. Both PBs must be on the same ship, not connected through a connector. |
| Drone approaches the connector from the wrong side | Report it. The script assumes the connector's forward direction is its connecting face. |
| Drone overshoots or wobbles at its slot | Lower `VelocityGain` to 2.0, or `PositionGain` to 0.6. |
| Drone drifts slowly off its slot under load | Make sure `DisturbanceComp=true`. |
| `OVER BUDGET` in runtime | Set `Perf=Low` on drones and hub. Drones fly a bit looser at Update10. |

---

# Phase 2: Buster defense drones

Phase 2 adds automatic defense on top of phase 1:
- **Alert levels** set from the hub's own turrets and sensors.
- **Screen size per alert:** the hub keeps the right number of Busters out for the current level.
- **Charge rotation:** low-battery drones are swapped with charged ones.
- **Patrols** that orbit the hub.
- **Combat switching:** the hub turns each Busters' vanilla combat blocks on and off.
- **SCRAMBLE, RECALL and SAFE switches.**
- **A tactical map screen.**

The fighting is done by vanilla turrets, weapons and AI combat blocks. The scripts handle fleet coordination.

> **Update every script together.** The message format changed (protocol v2), so phase 1 and phase 2 scripts can't talk to each other. Closing the game and running `install.bat` updates every tagged programmable block at once.

## 7. Building a Buster

Start with a phase 1 drone, then add:
- **Weapons.** Turrets or fixed weapons. Add `[Combat]` to the name of each block the hub should switch on when engaging.
- **Optional, for `EngageMode=AI`:** vanilla **AI Flight (Move)** plus **AI Offensive** (or **AI Defensive**) blocks, set up in their terminal the way you want them to fight. Add `[Combat]` to their names too.
- **Optional timer blocks** named `[Engage]` and `[Disengage]`. They fire when combat starts and stops. Put any toolbar actions on them that a simple on/off doesn't cover, such as switching an AI block's behaviour on or off.

Drone Custom Data (new keys):

```ini
Role=Buster              ; roles in the hub's DefenseRoles are managed automatically
EngageMode=Station       ; Station = keep flying the slot, weapons on
                         ; AI      = hand flight to the vanilla AI blocks inside the leash
CombatTag=[Combat]
EngageTimerTag=[Engage]
DisengageTimerTag=[Disengage]
CombatOffWhenIdle=true   ; [Combat] blocks are off unless engaged (keeps AI blocks from fighting the script)
```

- **Station mode:** the drone keeps its formation or patrol slot, and its `[Combat]` blocks (turrets) are switched on.
- **AI mode:** the script lets go of the thrusters and gyros, and the vanilla AI blocks take over inside the **leash** radius around the drone's slot. If the drone strays beyond the leash, the script takes control back, turns combat off and flies home. It re-engages once it's within 30% of the leash.
- Either way, a drone whose own turrets have a target reports that to the hub, which raises the alert.

## 8. Hub defense setup

**Alert sources:**

| Source | Raises alert to |
|--------|-----------------|
| A hub turret has a target | **RED** |
| A drone's turret has a target | **RED** |
| A sensor named `[Alert]` detects an enemy | **YELLOW** |
| RED within the last `AlertCooldown` seconds | **YELLOW** |
| None of the above | **GREEN** |

For an `[Alert]` sensor, turn on enemy detection in its terminal and set its range.

Hub Custom Data (new keys):

```ini
Defense=true            ; automatic defense manager
DefenseRoles=Buster,Drone
ScreenGreen=2           ; drones kept out at GREEN
ScreenYellow=4
ScreenRed=-1            ; -1 = all
EngageAlert=RED         ; RED | YELLOW | NEVER  - when screen drones switch combat on
Leash=600               ; metres around each slot (AI mode)
LaunchBattery=0.8       ; only launch drones charged at least this much
RotateBattery=0.4       ; swap an out drone below this for a charged one
LaunchInterval=1        ; seconds between launches/recalls (0.5 during SCRAMBLE)
AlertCooldown=60
AlertSensorTag=[Alert]
BasePatrolSpeed=8       ; m/s orbit of the station ring in BASE mode (0 = static stations)
CarrierPatrolSpeed=0
```

**Manual control.** Any manual order (`escort`, `hold`, `goto`, `dock`, `undock`, `stop`) takes that drone out of automatic control. The fleet screen shows **A** next to drones under automatic control. To hand a drone back, use `auto <drone|all>`.

## 9. Defense commands

| Command | Effect |
|---------|--------|
| `scramble` | Launch every defense drone now and force alert RED. `alert auto` ends it. |
| `recall` | Pause the defense manager and dock everything. `defense on` resumes. |
| `alert auto` / `green` / `yellow` / `red` / `next` | Force an alert level, or return to automatic. |
| `safe on` / `off` / `toggle` | **SAFE:** combat blocks on all drones stand down, whatever the alert. Drones keep flying their slots. |
| `defense on` / `off` / `toggle` | Turn the automatic manager on or off. |
| `auto <target>` / `manual <target>` | Hand drones to the manager, or take them back. |

Hub UI button rows:
- **Row 1** acts on the selected drone: ESCORT, HOLD, GOTO, DOCK, UNDOCK, STOP, AUTO.
- **Row 2** is fleet-wide: SCRAMBLE, RECALL, ALERT, SAFE, FORM, MODE.

Left and right move through both rows.

**Defense menu:** put `[FS:Defense]` in an LCD's or cockpit's name (add `:3` etc. for a cockpit screen). It lists ALERT, DEFENSE, SAFE, AUTO-RTB, FORMATION, SCRAMBLE and RECALL. Run the Hub UI PB with `def up` / `def down` to pick a setting, `def left` / `def right` to change its value, and `def apply` to send it. Moving onto a setting starts at its current value.

The generated carrier's Industrial Cockpit has this on screen 3 and comes with its toolbar filled in:

| Slot | Page 1: Defense menu | Page 2: Fleet screen |
|------|----------------------|----------------------|
| 1 | `def up` | `up` |
| 2 | `def down` | `down` |
| 3 | `def left` | `left` |
| 4 | `def right` | `right` |
| 5 | `def apply` | `apply` |

On cockpit screens, the Hub UI uses a compact layout with larger text. `CockpitZoom` (default 1) scales it, and `CockpitCompact=false` turns it off.

**Strike (spotter, designation, death ring):**

| Command | Effect |
|---|---|
| `search` / `search stop` | Spotters (drones with `Role=Spotter`) sweep forward `SearchDepth` (10 km) at `SearchAltitude` (800 m) above the hub, line abreast `SearchSpacing` apart if there are several. What their sensor turret tracks (WeaponCore threats, or the vanilla turret lock) comes back as contacts. |
| `designate` | Raycast `DesignateRange` (10 km) from the hub camera tagged `[Designator]` (look through it first: hotbar slot 6). A grid hit becomes the target; terrain becomes a GPS target. The GPS is logged. |
| `target GPS:name:x:y:z:` / `target x y z` / `target clear` | Designate a point / clear all contacts. |
| `loiter` | Every Buster (docked ones launch one per `LaunchInterval`) to the death ring: `RingDistance` (2800 m) up a `RingCone` (20 deg) cone over the target, evenly spaced by azimuth, rotating at `RingRate` (2 deg/s). |
| `attack [n]` | n Busters (default 1) leave the ring and dive. |
| `attackall` | Every Buster on the ring dives, `AttackStagger` (1.5 s) apart, each aimed at its own side of the target. |
| `abort` | Strike Busters back to escort. `recall` also aborts. |

Contacts come from spotters, `designate`, `target`, the IGC designation channel `FS<NetId>/des`, and the hub's own sensors: in **WeaponCore** worlds the target you lock with WeaponCore's targeting HUD (treated as a designation) and WeaponCore's threat list for the carrier; in vanilla worlds any turret or turret controller on the hub with a lock. The Hub Core status shows `WeaponCore: linked` when the API is up. The largest contact is the target (designations always win); contacts nobody has seen for `ContactTimeout` (20 s) are dropped, designations are kept until `target clear`.

Dive: from the ring at the target (plus an offset toward the Buster's side of it), full speed through a lead point with a lateral `Weave` that fades out before `ArmDistance` (300 m), where the warheads arm; proximity `Fuse` or impact.

Intercept: while the Busters loiter, any small enemy grid seen moving within `InterceptRange` (4 km) of the target gets the nearest Buster on the ring, updated in real time from the designators and the Buster's camera. Inside `AiHandoff` (600 m) the Buster arms its warheads and hands control to its **AI Flight** and **AI Offensive** (Intercept pattern, closest target) blocks; if its drone disappears it returns to the ring.

Mods: none required. Don't use the Laser Designator / MA Designator mods together with WeaponCore: WeaponCore takes over turrets, so their locks can't be read and the mods conflict.

**Lidar budget:** every camera raycast into terrain is a costly physics query, so each Buster is capped at `LidarRate` (10) rays per second across its cameras (ground grid `LidarSwath` 1100 m wide, `LidarStep` 50 m apart, while searching over a planet). That keeps sim speed intact but makes Buster lidar opportunistic: a small target can slip between rays. Designations (hub turrets / turret controllers with long-range AI or designator mods, `target GPS:...`, or an AWACS drone on the designation channel) are the reliable way to find targets.

**Formation and the hull:** drones only detour around the hub when their path crosses its hull box (plus `Clearance`), and always on their own side of it. When the carrier stops, the formation keeps its shape, drifts past and creeps back.

**Launch spacing:** drones leave the docks one per `LaunchInterval` (0.5 s at RED override), whatever ordered the launch, and each escort slot goes to the nearest drone so their paths don't cross.

**Tactical map:** put `[FS:Map]` in an LCD's name. It's a top-down radar in the hub's frame, with hub forward pointing up. It shows range rings, an elevation stem under each drone, state colours, a red ring around engaged drones and an alert-coloured border.

## 10. Phase 2 acceptance test

**H. Standby (GREEN)**

17. Dock 4 or more Busters and let them charge to 80% or more. Run `defense on` and `alert auto`. Exactly `ScreenGreen` (2) launch, one every `LaunchInterval`, and take slots. In BASE mode they orbit slowly. They show `A` and `PATROL` or `ESCORT`.

**I. Alert levels**

18. Run `alert yellow`. Busters launch until 4 are out.
19. Run `alert red`. All launch. With `EngageAlert=RED`, their `[Combat]` blocks switch on: the map shows red rings and the fleet screen shows `+W` or `ENGAGED`.
20. Run `alert green`. Surplus drones stand down and dock, and combat switches off.

**J. Real contact**

21. Spawn a hostile target (for example, an enemy-owned grid) inside a hub turret's range. The alert goes RED on its own, the screen grows, and the drones engage. Once it's destroyed, the alert drops to YELLOW, then to GREEN after `AlertCooldown`.

**K. AI mode and leash**

22. Set `EngageMode=AI` on one Buster, with AI Flight and AI Offensive blocks tagged `[Combat]`. At RED, its state shows `ENGAGED` and the vanilla AI flies it. Lure it beyond `Leash`: it should disengage, fly back to its slot, and re-engage once close.

**L. Rotation and safety**

23. Set `RotateBattery=0.95` on the hub. An out drone is swapped for a charged one (`Rotate: ... out, ... in`). Set it back afterwards.
24. Run `safe on` during RED. All combat blocks switch off and drones hold their slots. Run `safe off` to resume.
25. Run `scramble`. Everything launches at 0.5 s intervals. Then `alert auto`.
26. Run `recall`. Everything docks and the manager pauses. Then `defense on`.

Report which steps passed, plus any oddities: AI hand-over behaviour, leash distance feel, patrol speed and runtime numbers.

---

# Phase 3: Logistics (base fabrication, repair, rearm, power, jump lock)

New script: **`Factory.cs`**. Put it on a programmable block named `[Fleet Factory]` and the installer fills it in automatically. Protocol v3 means all scripts update together; `install.bat` does this.

## 11. Factory (base fabrication line)

> **Merge blocks only join grids of the same size.** A large-grid base can't print small-grid Busters directly. Put the `[Factory]` merge block on a small-grid plate attached to the base, for example on a rotor or hinge head. Rotor-attached parts count as the same construct, so the Factory script still finds the merge block.

**Build the bay.** Name these blocks with `[Factory]`; all must be on the hub or its rotor-attached plate:
- **1 projector:** load your Buster blueprint, for example `FS Buster Atmo`. Align it so the drone's merge block meets the factory merge block, and tick **Keep projection**.
- **Welders** covering the whole projection.
- **1 merge block:** holds the drone while it's welded.
- **1 connector:** lines up with the drone's connector, face to face.

Leave 25 m or more of clear space in front of the bay. A finished drone backs out along the connector axis when it launches.

**How a cycle runs:**
1. **Weld.** The factory welds the drone. If welding stalls for 60 s, it shows `WAITING PARTS`, usually because components ran out.
2. **Release.** It turns the welders and projector off and releases the merge block. The bay connector locks onto the drone within a few ticks.
3. **Hand-over.** Hub Core gives the new drone the network key, the drone registers as docked, charges in the bay, and the defense manager launches it.
4. **Repeat.** Once the bay is free, the next build starts.

**Control it** from Hub Core:
- `stock <n>`, or `TargetStock` in Custom Data, sets how many defense drones to keep. The factory builds whenever the live count is below that. `0` turns automatic builds off.
- `build <n>` queues builds by hand.

Builds run in BASE mode only, unless `FactoryInCarrier=true`.

Factory PB arguments: `build <n>`, `on`, `off`, `reset`, `rescan`.

> **Check once in game:** a programmable block welded from a projection must keep its script. Because the installer patches your blueprints, the printed drone's PB carries the Drone script. If a printed drone shows a blank PB, tell me and I'll add a fallback.

## 12. Repair, rearm and launch readiness

- **Blocks named `[Repair]`**, typically welders aimed at your docks, switch on while any docked drone has hull below 100%.
- **Blocks named `[Rearm]`**, typically sorters pushing ammo into docked drones, switch on while any drone is docked.
- **Launch readiness:** a drone launches only when battery is at least `LaunchBattery`, hull at least `LaunchIntegrity` (0.95) and ammo at least `LaunchAmmo` (0.5). The hull and ammo checks only apply if repair or rearm blocks exist. After `RepairTimeout` (180 s) the drone goes anyway, so a drone missing destroyed blocks is never stuck.
- **Auto-RTB** also triggers on low ammo (`RtbAmmo=0.1`).

## 13. Power-aware charging

- `CarrierMaxCharging=2` lets at most 2 docked drones charge at once in carrier mode, fullest first, so they're ready soonest. `BaseMaxCharging=0` means unlimited at a base.
- `PowerReserve=0.3`: when the hub's own batteries fall below 30%, drone charging pauses entirely.
- A held drone has its batteries switched off and runs on hub power. It turns them back on itself before undocking.

  > Don't disconnect a held drone by hand, because its batteries are off. Use `undock`.

## 14. Carrier jump interlock

| Command | Effect |
|---------|--------|
| `jump prep` | Recalls every drone and **disables the jump drives** until all are docked. Then `JUMP READY` appears and the drives unlock. |
| `jump force` | Unlocks the drives now. Undocked drones are left behind. |
| `jump cancel` | Abort. |

- The UI **JUMP** button toggles prep and cancel.
- When the hub detects the jump (a position change of more than 1 km), prep ends and the defense manager resumes 5 s later.

## 15. Hangar screen

Put `[FS:Hangar]` in an LCD's name. It shows:
- the jump lock banner,
- factory state with a progress bar and stock against target,
- every `[Dock]` with its occupant's battery and hull,
- repair and rearm status,
- charging limits and the hub's battery level.

## 16. Phase 3 acceptance test

**M. Factory**

27. Build the bay, load the Buster blueprint, and set `stock 3` with fewer than 3 Busters alive. You should see `BUILDING`, then `SECURING`, then `RELEASING`. Then `built drone #1`, then `New drone BUSTER-xx`, and the drone appears as DOCKED in the bay.
28. Run out of steel plates mid-build. `WAITING PARTS` appears, and welding resumes when components return.
29. Once charged, the new drone launches by itself. Then the next build starts.

**N. Repair and rearm**

30. Damage a drone and let it auto-RTB. `Repair bay ON` appears, and it stays docked until hull is at least 95% or the timeout passes.
31. Empty a drone's turret ammo. It RTBs on ammo, `[Rearm]` sorters run, and it launches when it's at least 50% full.

**O. Carrier power and jump**

32. In carrier mode with 4 drones docked and low, only 2 charge at a time. Drain the hub batteries below 30%: charging pauses.
33. Run `jump prep`. Everything docks, then `JUMP READY` appears. Jump: `Jump detected`, then defense resumes.
