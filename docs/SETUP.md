# FreeSims Setup (WSL2 / Linux arm64)

This fork targets modern .NET 8 and MonoGame 3.8 on Linux arm64 (tested under WSL2 on Windows arm64). The upstream `francot514/FreeSims` codebase targets .NET Framework 4.5 / MonoGame 3.6 / x86 — that version will not build or run here.

> You need a legitimately-owned copy of **The Sims 1 Complete Collection** or **Legacy Collection**. The engine is open source (MPL 2.0); the game assets are not redistributable. See "Assets" below.

## Toolchain

### .NET 8 SDK

Ubuntu 24.04 ships .NET 8 in the default repositories. No Microsoft apt repo required.

```bash
sudo apt install -y dotnet-sdk-8.0
```

Verify:

```bash
dotnet --info
```

Expected output includes:
- `.NET SDK Version: 8.0.x`
- `RID: ubuntu.24.04-arm64` (or `linux-arm64` on non-WSL distros)
- `Host Architecture: arm64`

### Native runtime dependencies

MonoGame DesktopGL links SDL2 / OpenAL / GLU at runtime; `scrot` is needed to capture screenshots for the baseline milestone. On Ubuntu 24.04 arm64:

```bash
sudo apt install -y libsdl2-2.0-0 libopenal1 libglu1-mesa scrot
```

`libfreetype6`, `libgl1`, `libx11-6` are already present on a stock Ubuntu 24.04 desktop/WSLg install. If a freshly minimal image complains about them, add them to the line above.

## Assets

SimsVille is a fork of `francot514/FreeSims` which is itself a fork of `riperiperi/FreeSO` (The Sims Online reimplementation) adapted to load Sims 1 content on top. **It needs two asset trees to boot:**

1. **TSO client** — UI, avatars, objects, globals (the engine substrate).
2. **Sims 1 Complete Collection** — neighborhoods, lots, additional objects (the content the engine runs).

Both are extracted into `./GameAssets/` (gitignored, never committed).

### Prerequisites

```bash
sudo apt install -y p7zip-full unshield
```

### Fetch TSO client (1.2 GB download)

The original TSO client was open-sourced by EA; a donated copy lives at [archive.org item TheSimsOnline_201802](https://archive.org/details/TheSimsOnline_201802) under CC-BY-ND.

```bash
./scripts/fetch-tso-client.sh
```

Downloads `TSO.zip` to `.assets-src/tso.zip`, unpacks the 1114-volume InstallShield CAB set via `7z`, and lays `./GameAssets/TSOClient/` down (~1.6 GB). Idempotent; sentinel: `objectdata/objects/objiff.far`.

### Extract Sims 1 content

You must provide your own Sims 1 install disc. This fork was tested with **The Sims 1 Complete Collection** install iso. Drop the iso at `.assets-src/sims-cc.iso`, then:

```bash
./scripts/extract-assets.sh
```

Or pass an arbitrary iso path:

```bash
./scripts/extract-assets.sh /path/to/your-sims-install.iso
```

The script:

1. Uses `7z` to unpack the `Setup/` directory from the iso.
2. Uses `unshield` to unpack the InstallShield CAB files.
3. Composes `./GameAssets/TheSims/` (merges `GameData_ALL_Recursive` + `GameData_ALL_Ranger` → `GameData/`, `UserData` + `UserData2` → `UserData/`, keeps `ExpansionPack1..7`/`GOLD`, `UIGraphics`, `SoundData`, `Music`, templates, etc.).
4. Writes `GameAssets/manifest.sha256` pinning every extracted file.
5. Validates via sentinel `GameData/Behavior.iff`.

Rerunning with a valid extraction is a no-op. Expected result: ~4900 files, ~2.9 GB at `GameAssets/TheSims/`.

**Never commit `GameAssets/` or `.assets-src/`.** Both are gitignored.

## Build

```bash
dotnet build SimsVille/SimsVille.csproj
```

The three ported projects (`sims.files`, `sims.common`, `SimsVille`) target `net8.0` / `AnyCPU` with MonoGame 3.8.2 (`MonoGame.Framework.DesktopGL`). Output lands in `SimsVille/bin/Debug/net8.0/` — `SimsVille.dll` plus a `SimsVille` launcher binary.

### Notes on what changed vs. upstream

- `sims.debug`, `sims.parser`, `SimsNet` are not built. They were .NET Framework WinForms tools or required a missing `ProtocolAbstractionLibraryD` companion library. The triad above is the full game runtime.
- `SimsVille/Debug/` (WinForms-based VM debug panel) is excluded from compile — same reason.
- 13 orphan `.cs` files using legacy `TSOVille.*`/`tso.simantics.*` namespaces are excluded explicitly in the csproj; they were never compiled in the original either (stale duplicates).
- Multiplayer network drivers (`VMServerDriver`, `VMClientDriver`) are excluded. They depend on GonzoNet.dll which references a .NET Framework `System.ServiceModel` type identity that doesn't resolve on net8.0. `VMLocalDriver` (new, `SimsVille/SimsAntics/NetPlay/Drivers/VMLocalDriver.cs`) runs the VM single-player.
- `GOLDEngine.dll`, `GonzoNet.dll`, `TargaImagePCL.dll` are vendored at `./lib/` and referenced by `HintPath`. All other old bundled DLLs (OpenTK, Tao.Sdl, ScintillaNET, SharpDX, DiscUtils, OpenNat, TargaImage non-PCL) are no longer referenced.

## Shader compilation

The compiled `.xnb` shader effect files under `SimsVille/bin/Debug/net8.0/Content/OGL/Effects/` are **build artifacts committed to the repo**. They are produced by MonoGame's `mgfxc` tool from the `.fx` sources at `.assets-src/freeso-stage/ContentSrc/Effects/` (plus `LightingCommon.fx` fetched from FreeSO) and then wrapped in an XNBd container so `Content.Load<Effect>` can consume them.

You only need to rebuild them if a `.fx` source changes or if MonoGame upgrades the MGFX binary version. The shipped `.xnb` files are MGFX v10 (MonoGame 3.8.x).

Canonical shader list (all rebuilt to MGFX v10): `2DWorldBatch`, `2DWorldBatchiOS`, `GrassShader`, `GrassShaderiOS`, `LightMap2D`, `PixShader`, `SSAA`, `VerShader`, `Vitaboy`, `colorpoly2D`, `gradpoly2D`. `GrassShader.fx` (and the thin `GrassShaderiOS.fx` wrapper with `#define SIMPLE 1`) are pulled from `riperiperi/FreeSO` master at `TSOClient/tso.content/ContentSrc/Effects/` — that path was missing from `francot514/FreeSims`.

### One-time recipe (x86_64 Linux only)

`mgfxc` invokes Windows's `fxc.exe` via `d3dcompiler_47.dll` under a 64-bit wine prefix. This path **requires x86_64**; arm64 (WSL) cannot run fxc.exe natively. Do the shader build on an x86_64 box and copy the `.xnb` back.

```bash
# Toolchain (one-time)
sudo apt install -y dotnet-sdk-8.0 wine winetricks 7zip

# If wine32:i386 is installed, remove it — the Ubuntu wine wrapper prefers
# 32-bit wine when both are present, and mgfxc needs 64-bit wine:
sudo apt remove -y wine32:i386   # safe no-op if not installed

dotnet tool install -g dotnet-mgfxc
export PATH=$PATH:$HOME/.dotnet/tools

# 64-bit wine prefix with Windows dotnet SDK + d3dcompiler_47
export WINEPREFIX=$HOME/.wine-mgfxc
export WINEARCH=win64
wineboot -u
winetricks -q d3dcompiler_47
curl -sL https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.201/dotnet-sdk-8.0.201-win-x64.zip -o /tmp/dotnet-sdk.zip
7z x /tmp/dotnet-sdk.zip -o"$WINEPREFIX/drive_c/windows/system32/" -aoa

export MGFXC_WINE_PATH=$HOME/.wine-mgfxc
```

### Build the effects

```bash
cd .assets-src/freeso-stage/ContentSrc/Effects
for f in *.fx; do
  name="${f%.fx}"
  [ "$name" = "LightingCommon" ] && continue   # header-only include
  mgfxc "$f" "/tmp/$name.xnb" /Profile:OpenGL
done
```

Each output is a raw MGFX blob (magic `MGFX`, version byte at offset 4 = `0x0a`). The MonoGame content pipeline normally wraps this in an XNBd container; we do the same with `scripts/wrap-xnb-effect.py` (included in this repo):

```bash
python3 scripts/wrap-xnb-effect.py /tmp/*.xnb
cp /tmp/*.xnb SimsVille/bin/Debug/net8.0/Content/OGL/Effects/
```

The wrapped files begin with `XNBd\x05` and are loadable by `Content.Load<Effect>` on MonoGame 3.8.

## Run

```bash
cd SimsVille/bin/Debug/net8.0
./SimsVille
```

Under WSLg, `DISPLAY` is set automatically. If the window never paints or you see GL errors, try the software-GL fallback:

```bash
LIBGL_ALWAYS_SOFTWARE=1 ./SimsVille
```

### Headless (Xvfb + VNC)

UI geometry is authored for a 4:3 viewport and anchors to `GlobalSettings.Default.GraphicsWidth`/`Height` (default 1024x768 — see `SimsVille/GlobalSettings.cs:49-50`). On a non-4:3 Xvfb screen MonoGame's client-size-changed handler (`SimsVille/Game.cs:54-73`) rewrites those values to the screen size, which breaks scale math authored against 800x600 (scale = height/600). **Run Xvfb at 1024x768** to preserve the authored aspect:

```bash
Xvfb :99 -screen 0 1024x768x24 &
env -i PATH=$PATH HOME=$HOME DISPLAY=:99 x11vnc -display :99 -forever -nopw -listen localhost -rfbport 5900 -bg -o /tmp/x11vnc.log
cd SimsVille/bin/Debug/net8.0
env -i PATH=$PATH HOME=$HOME DISPLAY=:99 SDL_AUDIODRIVER=dummy ./SimsVille &
```

Connect a VNC client to `localhost:5900`. Do **not** use 1366x768 or other non-4:3 sizes — UI controls (Save House/Simantics buttons, cash panel, lot label, pie menus) will overlap.

Asset resolution (Linux):

- **TS1 content**: `FREESIMS_ASSETS` env var → `GameAssets/TheSims/` relative to the executable → ancestor hops. Resolved by `LinuxLocator.FindTheSimsComplete()`.
- **TSO client**: `FREESIMS_TSO_ASSETS` env var → `GameAssets/TSOClient/` relative to the executable → ancestor hops. Resolved by `LinuxLocator.FindTheSimsOnline()`.

A symlink at `SimsVille/bin/Debug/net8.0/GameAssets` points to the repo-root `GameAssets/` so both resolve relative to the binary directory.
