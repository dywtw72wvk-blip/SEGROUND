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
