# Extreme Launcher — .NET / Avalonia port

A C#/.NET 9 port of Extreme Launcher (a Prism/MultiMC-lineage Minecraft launcher), targeting
**Windows, Linux and macOS** through [Avalonia](https://avaloniaui.net/). The original is Qt/C++ at the
repository root; this directory is the port.

## Status — read this before shipping

The port is **feature-rich and heavily tested, but not yet proven end to end by a human.** Be precise
about what that means:

**Done and covered by tests (3,238 passing, 15 skipped):**
- Version/component resolution, including Forge, NeoForge, Fabric and Quilt — confirmed against the
  **live** metadata server.
- The launch pipeline: classpath, JVM and game arguments, natives, pre/post commands, server/world
  join. (Argument construction is tested; see the gap below.)
- Instance management: create, copy (with per-part and link-mode options), delete, rename, group,
  icon, shortcut, export.
- Every instance page: mods / resource / texture / shader packs, worlds, servers, screenshots, logs,
  other logs, version, game options, notes, settings.
- Modpack browsing and import (Modrinth confirmed against the live API; CurseForge parses fixtures).
- Pack export (Modrinth `.mrpack` and CurseForge `.zip`).
- A headless CLI (`extremelauncher`) that lists, inspects and launches instances.

**Not done / not verified — the real ship gates:**
- **Microsoft sign-in is not usable from a source build.** The flow is present, but the MSA client id
  is intentionally empty and must come from a build-time secret (see *Credentials*). Without it, only
  offline sessions work.
- **No test starts a real JVM.** Resolution and the command line are proven; an actual Minecraft
  process booting is not.
- **No window has been rendered or clicked by a human.** Tests run headless (Avalonia's headless
  backend fakes the renderer), so the UI is logic-verified but never visually verified.

In short: the **code** is at parity for the subsystems above; a real release still needs credentials, a
human at a desktop, and a genuine game launch. See [`PORTING.md`](PORTING.md) for the full
subsystem-by-subsystem log.

## Requirements

- [.NET SDK 9.0](https://dotnet.microsoft.com/download) or newer.
- A JRE/JDK is only needed to *run* Minecraft, not to build; the launcher can locate or download one.

## Build, test, run

```bash
# from this directory (dotnet/)
dotnet build ExtremeLauncher.sln -c Release
dotnet test  ExtremeLauncher.sln -c Release
```

Run the desktop app:

```bash
dotnet run --project src/ExtremeLauncher.App -c Release
```

Run the headless CLI (lists/inspects/launches instances without a window):

```bash
dotnet run --project src/ExtremeLauncher.Cli -c Release -- help
dotnet run --project src/ExtremeLauncher.Cli -c Release -- --dir <data-dir> list
```

Point `--dir` at an existing Prism or MultiMC data folder to use its instances.

## Publish (per platform)

Framework-dependent (needs .NET installed on the target):

```bash
dotnet publish src/ExtremeLauncher.App -c Release -o out
```

Self-contained, single file, per runtime (no .NET needed on the target):

```bash
dotnet publish src/ExtremeLauncher.App -c Release -r win-x64   --self-contained \
  -p:PublishSingleFile=true -o out/win-x64
dotnet publish src/ExtremeLauncher.App -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o out/linux-x64
dotnet publish src/ExtremeLauncher.App -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -o out/osx-arm64
```

(Swap the RID for `osx-x64`, `linux-arm64`, etc. as needed.)

## Credentials

API keys are **never compiled in** and none are committed. They are supplied at runtime, in this order
(later sources win):

1. A `credentials.json` beside the executable or in the data directory.
2. Environment variables: `EXTREMELAUNCHER_MSA_CLIENT_ID`, `EXTREMELAUNCHER_FLAME_API_KEY`,
   `EXTREMELAUNCHER_IMGUR_CLIENT_ID`.

```json
{
  "msaClientId": "your-microsoft-oauth-client-id",
  "flameApiKey": "your-curseforge-api-key",
  "imgurClientId": "your-imgur-client-id"
}
```

`credentials.json` is git-ignored. With the MSA client id empty, sign-in is disabled and the launcher
runs offline-only; with the CurseForge key empty, CurseForge browsing/import is disabled; with the imgur
id empty, screenshot upload is disabled. The MSA client id identifies a *public* OAuth client and is not
itself a secret; the CurseForge key **is** a secret — treat it as one.

## Project layout

| Project | What it holds |
|---|---|
| `ExtremeLauncher.Core` | Filesystem, zip, NBT-free primitives, `BuildConfig`, tasks base |
| `ExtremeLauncher.Tasks` | The task/runnable framework |
| `ExtremeLauncher.Net` | HTTP download jobs, imgur upload |
| `ExtremeLauncher.Settings` | Typed, override-aware settings |
| `ExtremeLauncher.Meta` | Component/version resolution (`PackProfile`, `ComponentUpdateTask`) |
| `ExtremeLauncher.Minecraft` | Version files, mods, worlds, servers, NBT, auth session |
| `ExtremeLauncher.Java` | Java discovery and download |
| `ExtremeLauncher.ModPlatform` | Modrinth / CurseForge (Flame) APIs and pack formats |
| `ExtremeLauncher.Launch` | Launch pipeline, instance list, copy/import tasks |
| `ExtremeLauncher.ViewModels` | All UI logic, framework-free and unit-tested |
| `ExtremeLauncher.App` | The Avalonia desktop application (windows and wiring) |
| `ExtremeLauncher.Cli` | The headless command-line launcher |

Every `tests/ExtremeLauncher.*.Tests` project holds the tests for the matching source project.

## License

GPL-3.0-only, as the upstream launcher. See the headers on every source file.
