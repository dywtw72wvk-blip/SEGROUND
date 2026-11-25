# Fleet System (Space Engineers in-game scripts)

A multi-drone fleet for Space Engineers programmable blocks. One hub script runs a mobile **Carrier** or a static **Base**. Drones register with it, fly in formation, dock themselves, and report status.

- `dist/` holds the paste-ready scripts: `HubCore.cs`, `HubUI.cs` and `Drone.cs`.
- `docs/SETUP.md` covers in-game setup, commands and the phase 1 acceptance test.
- `docs/ARCHITECTURE.md` is the design and roadmap. Phase 2 is the Buster defense drone.
- `src/` holds the source fragments. `tools/build.py` assembles them (`--check` compile-checks against `tools/stubs`).

## Install (Windows), fully automatic

1. **One-time:** install Git for Windows (https://git-scm.com/download/win).
2. **In game,** name your programmable blocks:
   - the hub's main PB `... [Fleet Core]`
   - the hub's screen PB `... [Fleet UI]`
   - each drone's PB `... [Fleet Drone]`

   Save, then **exit the game**.
3. Double-click **`install.bat`**. If SmartScreen appears, click *More info → Run anyway*. It:
   - clones the repo into `Documents\SEGROUND` (first run; a GitHub sign-in window may appear once) or updates it,
   - installs the scripts for *Browse Scripts*,
   - **writes the scripts straight into those programmable blocks** in your saved worlds and blueprints. It shows the list and asks first, and backs up every file it changes as `*.fleetbak-<time>`.
4. Load the world. The scripts are already in and running.

**To update:** close the game and run `install.bat` again. Every block already running a Fleet script, including ones in blueprints, gets the new version.

**Without Git:** click **Code → Download ZIP**, use *Extract All…*, and run `install.bat` from the extracted folder.

## Build (developers)

```bash
python3 tools/build.py --check
```
