# Fleet System (Space Engineers in-game scripts)

A multi-drone fleet for Space Engineers programmable blocks. One hub script runs a mobile **Carrier** or a static **Base**. Drones register with it, fly in formation, dock themselves, and report status.

- `dist/` holds the paste-ready scripts: `HubCore.cs`, `HubUI.cs` and `Drone.cs`.
- `docs/SETUP.md` covers in-game setup, commands and the phase 1 acceptance test.
- `docs/ARCHITECTURE.md` is the design and roadmap. Phase 2 is the Buster defense drone.
- `src/` holds the source fragments. `tools/build.py` assembles them (`--check` compile-checks against `tools/stubs`).

## Install (Windows)

**Recommended: install with Git (one file, easy updates)**
1. Install Git for Windows if you don't have it: https://git-scm.com/download/win
2. Download `install.bat` from this repo and double-click it. If Windows SmartScreen appears, click *More info → Run anyway*.
3. The first run clones the repo into `Documents\SEGROUND`. Because the repo is private, a GitHub sign-in window may appear once. It then installs the scripts into Space Engineers' local scripts folder.
4. In game, open a programmable block and click **Edit → Browse Scripts**. Pick *Fleet Hub Core*, *Fleet Hub UI* or *Fleet Drone*, then click **Check Code** and **Remember & Exit**.

**To update:** run `install.bat` again. It pulls the latest version and reinstalls. Then re-select the script in each programmable block.

**Without Git:** click **Code → Download ZIP**, use *Extract All…*, and run `install.bat` from the extracted folder. It falls back to the `dist` folder next to it.

## Build (developers)

```bash
python3 tools/build.py --check
```
