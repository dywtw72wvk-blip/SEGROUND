# Fleet System (Space Engineers in-game scripts)

A multi-drone fleet for Space Engineers programmable blocks. One hub script runs a mobile **Carrier** or a static **Base**. Drones register with it, fly in formation, dock themselves, and report status.

- `dist/` holds the paste-ready scripts: `HubCore.cs`, `HubUI.cs` and `Drone.cs`.
- `docs/SETUP.md` covers in-game setup, commands and the phase 1 acceptance test.
- `docs/ARCHITECTURE.md` is the design and roadmap. Phase 2 is the Buster defense drone.
- `src/` holds the source fragments. `tools/build.py` assembles them (`--check` compile-checks against `tools/stubs`).

## Install (Windows)

1. On GitHub, click **Code → Download ZIP** and extract it.
2. Double-click **`install.bat`** inside the extracted folder. If Windows SmartScreen appears, click *More info → Run anyway*.
3. In game, open a programmable block and click **Edit → Browse Scripts**. Pick *Fleet Hub Core*, *Fleet Hub UI* or *Fleet Drone*, then click **Check Code** and **Remember & Exit**.

To update, download the ZIP again, re-run `install.bat`, then re-select the script in each programmable block.

## Build (developers)

```bash
python3 tools/build.py --check
```
