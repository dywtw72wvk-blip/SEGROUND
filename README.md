# Fleet System (Space Engineers in-game scripts)

A multi-drone fleet for Space Engineers programmable blocks. One hub script runs a mobile **Carrier** or a static **Base**. Drones register with it, fly in formation, dock themselves, and report status.

- `dist/` holds the paste-ready scripts: `HubCore.cs`, `HubUI.cs` and `Drone.cs`.
- `docs/SETUP.md` covers in-game setup, commands and the phase 1 acceptance test.
- `docs/ARCHITECTURE.md` is the design and roadmap. Phase 2 is the Buster defense drone.
- `src/` holds the source fragments. `tools/build.py` assembles them (`--check` compile-checks against `tools/stubs`).

```bash
python3 tools/build.py --check
```
