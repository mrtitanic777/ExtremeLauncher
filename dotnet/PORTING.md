# Porting Extreme Launcher to C#

Full-parity port of the Qt/C++ launcher in `launcher/` to .NET 9 + Avalonia.

**Scale:** 758 `.cpp`/`.h` files, 65 `.ui` forms, ~107,400 lines of C++.

## Ground rules

1. **The Qt test suite is the contract.** `tests/` has 21 data-driven test files. Port the test
   *first*, then write C# until it passes. Where a data file exists (`tests/testdata/`), reference it
   from the C# test project rather than copying, so both implementations answer to one source of truth.
2. **GPL-3.0-only carries over.** A port is a derivative work. Keep the upstream copyright headers on
   every ported file and add a `Ported from launcher/<path>` line.
3. **Port behavior, not structure.** Qt idioms with no C# analogue get redesigned (see below).
   Bug-for-bug fidelity is required only where a test pins it — and when it is, say so in a comment.
4. **One subsystem per branch.** Nothing merges without its ported tests passing.

## Order of work

Driven by the dependency graph, not by file count. Each wave only depends on waves above it.

| # | Wave | Source | Files | Notes |
|---|------|--------|------:|-------|
| 0 | Core utilities | `launcher/*.cpp,*.h` | 91 | `Version`, `StringUtils`, `FileSystem`, `Json`, `GZip`, `MMCZip`, `Untar`, `Commandline`. Pure logic, no deps. Start here. |
| 1 | Settings | `launcher/settings/` | 12 | INI format must stay byte-compatible — existing installs depend on it. |
| 2 | Tasks | `launcher/tasks/` | 8 | **Architectural rewrite, not a port.** See below. |
| 3 | Net | `launcher/net/` | 31 | `HttpClient` + the wave-2 model. Big net deletion. |
| 4 | Java | `launcher/java/` | 18 | JVM discovery, version parsing. Self-contained. |
| 5 | Meta | `launcher/meta/` | 10 | Metadata index fetch/parse. |
| 6 | Minecraft | `launcher/minecraft/` | 162 | **The brain.** `Rule`, `Library`, `PackProfile`, `ComponentUpdateTask`, plus `auth/`, `mod/`, `update/`, `gameoptions/`, `skins/`. Split into its own sub-waves. |
| 7 | Launch | `launcher/launch/` | 22 | Command-line construction, process supervision, log capture. |
| 8 | Mod platforms | `launcher/modplatform/` | 71 | CurseForge, Modrinth, FTB, ATLauncher, Technic, packwiz. Additive — can lag. |
| 9 | Support | `tools/` `updater/` `icons/` `screenshots/` `pathmatcher/` `news/` `translations/` `filelink/` | 50 | `filelink` is an elevated Win32 symlink helper; rewrite against Win32 directly. |
| 10 | UI | `launcher/ui/` | 283 + 65 forms | Avalonia. Rebuild, not port. |

**Wave 6 is the milestone that matters.** After wave 7 you have a launcher that can start Minecraft
headlessly. That proves the port before a month goes into UI.

## Qt idioms with no direct analogue

These are the four places a mechanical translation will produce bad C#.

**`Task` (`launcher/tasks/Task.h`) → `System.Threading.Tasks` + `IProgress<T>` + `CancellationToken`.**
Nearly everything in the codebase inherits from `Task`, so this decision propagates everywhere. The Qt
class is `QObject` + `QRunnable` with `started`/`progress`/`succeeded`/`failed`/`aborted` signals and a
`TaskStepProgress` tree. In C#: return `Task<T>`, report through `IProgress<TaskStepProgress>`, cancel
through `CancellationToken`. `SequentialTask`/`ConcurrentTask`/`MultipleOptionsTask` become `await`
in sequence, `Task.WhenAll`, and a first-success loop respectively — those three files mostly vanish.

**`QAbstractItemModel` → `ObservableCollection<T>` + Avalonia bindings.** `InstanceList`,
`VersionProxyModel`, `FileIgnoreProxy`, `ResourceFolderModel` are all built on it. Row/column/`QModelIndex`
plumbing does not survive; restructure to typed collections with sort/filter views. Expect these to be
redesigned rather than translated.

**Signals/slots → events, or `IObservable<T>` where the fan-out is real.** Don't reimplement the
signal/slot machinery.

**`QNetworkAccessManager` + the `Sink`/`Validator` chain → `HttpClient` + `Stream` pipeline.**
`ChecksumValidator`, `FileSink`, `MetaCacheSink` map cleanly onto stream wrappers.

## Carry-over items that are easy to miss

- **CurseForge API key** lives in `CMakeLists.txt`. It has ToS strings attached (see `README.md`) —
  carry over the key handling *and* the "set it to empty string to disable" escape hatch.
- **`Launcher_BUILD_PLATFORM`** must not be `official` for non-upstream builds.
- **Instance directory format** must stay compatible or you strand every existing user's installs.
- **Name collision:** `ExtremeLauncher.Core.Version` shadows `System.Version`. Resolves correctly inside
  the namespace; fully qualify in the rare file needing both.

## What .NET buys you

Self-contained `dotnet publish` replaces the entire Qt DLL/plugin deployment step — the thing that
currently requires `cmake --install` plus bundling `platforms/`, `tls/`, `iconengines/`, `imageformats/`.

## Status

**382 of 758 files. 3,432 passing, 15 skipped.** (12 source projects, 12 test projects.)

> **Modpack install provider guard (wave 92).** A correctness fix closing a gap wave 90 opened: the
> browser now lists CurseForge modpacks, but `ModpackInstallTask` hands whatever it downloads to
> `ModrinthImportTask`, which reads a .mrpack — a different format from a CurseForge pack's
> manifest.json + overrides zip. Installing a CurseForge pack would therefore download it and then fail
> confusingly deep in the Modrinth importer. `ModpackInstallTask.ExecuteAsync` now checks
> `pack.Provider` first and refuses anything but Modrinth with a clear message, before any download.
> CurseForge packs stay browsable (discovery has value); installing one waits on a Flame import path —
> its manifest parser (`FlamePackManifest`) and file resolver (`FlameFileResolver`) are already ported,
> the orchestration task is not. 2 tests: a CurseForge pack is refused before downloading, and a
> Modrinth pack passes the guard to the next check.

> **Mod dependency filtering (wave 91).** Ported the pure heart of
> `GetModDependenciesTask::getDependenciesForVersion` to `ModPlatform/ModDependencies.cs`. When a mod
> version is about to be installed, `NewRequiredDependencies` decides which of its dependencies still
> need fetching: only the REQUIRED ones, with the Fabric/Quilt override applied (wave 89's
> `ModIndex.ApplyLoaderOverride`), and only those not already accounted for — not a duplicate within the
> version, and not already in the caller's set of mods-being-installed and mods-already-present. The
> Modrinth version-only path (a dependency named by file version rather than project id) is matched by
> version. The task around it does the network fetch and recursion; this is the filter. Upstream keeps
> three separate "already have" lists checked with slightly different (and, on one path, unreachable)
> comparisons; nothing tests that, so the port unifies them into one `KnownDependency` set with one
> rule — documented in the source. 7 tests. Builds directly on wave 89.

> **CurseForge in the modpack browser (wave 90).** Wired wave 88's `FlamePackIndex` into the UI. The
> pack browser was Modrinth-only; it now has a provider picker (Modrinth / CurseForge).
> `PackBrowserViewModel` gained a `Provider` (searches use it; switching clears the old provider's
> results and, when CurseForge is unavailable for want of an API key, reports why through `CanSearch`
> and `Status`) and an `AvailableProviders` list. `ResourceSearchSource` now routes a Flame **modpack**
> listing through `FlamePackIndex` rather than the mod parser (`FlameModIndex`), so CurseForge modpacks
> get the slug-based logo name and the no-good-file rejection; versions stay on `FlameModIndex`, whose
> file parsing is a working superset. The window's provider ComboBox follows the mod browser's
> static-items + code-behind-mapping pattern (shown as "CurseForge", the enum's `Flame`). 5 new view
> model tests (provider drives the search, switching clears results, unavailable provider is explained,
> both providers offered).

> **Fabric/Quilt dependency override (wave 89).** Ported `GetModDependenciesTask::getOverride` to
> `ModIndex.ApplyLoaderOverride`. The override *data* (`GetOverrideDependencies`) was already ported;
> this is the loader-aware substitution that uses it. Quilt runs Fabric mods, so a Fabric mod installed
> on Quilt asks for Fabric API — the wrong package there, which breaks the pack alongside QSL. On Quilt
> a Fabric-API dependency is redirected to the Quilt one; on Fabric, a Quilt-package request is
> redirected back; Quilt wins when both loader bits are set. A dependency that matches no override, a
> provider mismatch, or a loader set that is neither Fabric nor Quilt is returned unchanged; a
> substituted one keeps its dependency type but drops its version, exactly as upstream builds it. 8
> tests over both providers' tables, both directions, the precedence rule, and every pass-through case.
> (No new file — the method joins the existing `ModIndex`.)

> **CurseForge modpack index (wave 88).** Ported `flame/FlamePackIndex.cpp` to
> `ModPlatform/FlamePackIndex.cs` — the CurseForge *modpack* listing parser, distinct from the already
> ported `FlameModIndex` (individual mods) and `FlamePackManifest` (a pack's manifest.json). It fills
> the shared `IndexedPack`/`IndexedVersion`, like the other providers' parsers. `LoadIndexedPack` reads
> a search hit and rejects any pack whose default file (mainFileId) is missing or targets no Minecraft
> version — upstream will not list something that cannot be installed; the logo filename is built from
> the slug plus the logo URL's extension. `LoadIndexedInfo` reads the links (website onto the pack,
> issues/source/wiki into ExtraData, each trailing slash trimmed). `LoadIndexedPackVersions` reads the
> file list newest-first by file id, splitting each file's gameVersions into Minecraft versions (the
> dotted ones) and loader flags, and dropping files with no download URL (third-party distribution off)
> or no Minecraft version. 8 tests over the listing, logo-name derivation, link trimming, the two
> rejection paths, and version sorting/filtering.

> **JVM argument validation (wave 87).** Ported the one piece of logic in `JavaCommon.cpp` —
> `checkJVMArgs` — to `Java/JavaArguments.cs`; the rest of that file is dialog boxes and a Qt task. The
> settings pages run this before accepting a user's extra JVM arguments, and it refuses two things:
> memory options that duplicate the launcher's own Memory boxes (`-Xms`, `-Xmx`, `-XX:PermSize`,
> `-XX:InitialHeapSize`, and upstream's own typo `-XX-MaxHeapSize`, kept as written), and pinning a
> required Java version with `-version:`. `CheckJvmArgs` returns which problem tripped (memory checked
> first, as upstream), `AreJvmArgsSafe` is the boolean form, and `WarningFor` carries the message the UI
> shows. 14 tests over every refused flag, the memory-before-version ordering, ordinary arguments that
> pass, and the warning text.

> **Local skin entry (wave 86).** Ported `minecraft/skins/SkinModel` to `Minecraft/Skins/LocalSkin.cs`
> — one entry in the user's local skin library: a PNG on disk, its arm model (classic/slim), an optional
> cape, and the source URL. It is named `LocalSkin` because `SkinModel` is already the arm-model enum
> (Auth/SkinApi); upstream overloaded the one name for both. The entry carries the behaviour: `Name`
> (the filename without extension), the JSON round-trip through skins.json (a bare name in, a
> directory-relative ".png" path out), `Rename` (moves the file, updates the path), and `IsValid` — the
> Minecraft skin size rule (64 wide, 32 or 64 tall) read straight from the PNG IHDR header rather than
> by decoding the image, since skins are always PNG. 12 tests over name/model-string, JSON round-trip
> and the classic default, rename, and the size rule (including non-PNG, missing, and truncated files).
> The list manager around it (`SkinList`) is a Qt list-model with a file watcher and drag-and-drop, and
> stays with the UI.

> **ATLauncher install decision logic (wave 85).** The ATLauncher install task is ~1,075 lines of Qt
> download/extract/staging orchestration, but two of its private helpers are pure and carry the real
> rules, so they are ported and tested ahead of the rest, into `Launch/AtlInstall.cs`.
> `AtlInstall.GetDirForModType` is the mod-type placement table — a delivery instruction, not a
> category: "mods" → the mods folder, "jar"/"forge" → jarmods, "dependency" → a per-Minecraft-version
> mods subfolder, several types (extract/decomp/root/…) resolve to no plain destination because they
> are handled at another stage, Millenaire is recognised-but-unsupported, and Unknown is a fatal error.
> `AtlInstall.DetectLibrary` turns a library's server path or filename into a Gradle coordinate so a
> library ATLauncher ships lines up with the metadata index — server path preferred, two known
> filenames (guava, commons-lang3) recognised, and an md5-keyed synthetic coordinate as the fallback.
> 25 tests over every mod-type row and all three detection branches. The remaining install orchestration
> (download, extract, keeps/deletes, staging) is a later wave; its parsing (wave 83) and now its
> decision helpers are in place.

> **External tools — MCEdit and profilers (wave 84).** Ported the pure core of
> `tools/{MCEditTool,JProfiler,JVisualVM,GenericProfiler}` to `Launch/ExternalTools.cs`. Upstream wraps
> each tool in a QObject owning a QProcess and talking over signals; that is glue. What is worth porting
> and can be tested without launching a profiler is pure: `McEditTool.Check` (does a directory look like
> an MCEdit install — the OS-independent marker set) and `GetProgramPath` (the runnable file for the OS:
> the .app bundle on macOS, mcedit.sh/.py on Linux, mcedit.exe/mcedit2.exe on Windows);
> `JProfilerTool.Check` (bin + jprofiler[.exe] + agent.jar), `ProgramPath` (bin/jpenable[.exe]), and
> `BuildArguments` (`-d {pid} --gui -p {port}`); `JVisualVmTool.Check` (an executable whose name contains
> "visualvm", with QFileInfo::isExecutable reproduced as extension-on-Windows / execute-bit-on-Unix) and
> `BuildArguments` (`--openpid {pid}`). The OS resolvers take a `ToolPlatform` (defaulting to the host)
> so a Windows test can check the Linux resolution and vice versa. GenericProfiler carries no logic
> beyond a status string and was not given a class. 24 tests. The process launching and settings
> plumbing stay with the runtime that calls these.

> **ATLauncher parsing layer completed (wave 83).** A prior wave ported the ATLauncher *mod chooser*
> (`AtlPackManifest`: mod/loader/library parsing, `GetDefaultSelection`, `ResolveDependencies`); this
> wave finishes the pure parsing layer. `AtlPackManifest.LoadVersion` assembles a whole pack version —
> the top-level fields plus every nested section (main class, extra arguments, loader, libraries, mods,
> configs, the colour/warning tables mods reference by name, install/update messages, and the
> keep/delete file rules an update applies), each read only when present. New file `AtlPackIndex.cs`
> ports the two readers that surround it: the pack index (`AtlPackIndex.LoadIndexedPack` — the browser's
> list, deriving a `SafeName` icon filename by stripping the name to letters and digits) and the share
> code (`AtlShareCodeReader.LoadResponse` — a saved optional-mod selection wrapped in the API's error
> envelope, with data present only on success). 9 tests over hand-built JSON (whole-version assembly,
> minimal version, index fields + safe-name derivation + private type, and success/error/null-message
> share codes). ATLauncher's parsing is now complete; the ~1,075-line install task (`ATLPackInstallTask`)
> is the remaining piece, a later wave.

> **Technic install tasks (wave 82).** The download/extract front end for the wave-81 processor, ported
> from `technic/SingleZipPackInstallTask` and `technic/SolderPackInstallTask`. `TechnicPackStager`
> (`Launch/TechnicPackInstall.cs`) extracts one or more archives into `staging/minecraft` and calls the
> builder — one archive for a single-zip pack, one per mod layered in order for a Solder pack (a later
> archive winning where two collide, as upstream extracts them). `TechnicSingleZipInstallTask`
> downloads one zip; `TechnicSolderInstallTask` resolves the Solder build document
> (`TechnicSolder.BuildUrl` → `{solder}/modpack/{pack}/{version}`, now a pure helper), downloads every
> mod md5-checked, and takes the build's own Minecraft version when it names one. Upstream's post-unzip
> Unix `chmod` loop is dropped: .NET's extractor already writes user-readable/writable files. 5 tests —
> the stager over hand-built archives (single, layered, missing) and the build-URL formatting (with and
> without a trailing slash). This closes the Technic install path end to end: manifest → download →
> extract → instance.

> **Technic pack processor (wave 81).** Ported `technic/TechnicPackProcessor` to
> `Launch/TechnicPackInstall.cs` as `TechnicPackBuilder.BuildFromStaging`: given a Technic pack already
> extracted into `staging/minecraft`, it works out the components and writes the pack profile plus
> instance.cfg. It picks between the four shapes upstream handles — a `bin/modpack.jar` carrying a
> `version.json` (→ the already-ported `TechnicVersionJson.DetectComponents`, with the Minecraft version
> read from `fmlversion.properties` when `inheritsFrom` is absent), a pre-Forge `modpack.jar` that is
> itself a jar mod (over a search-supplied Minecraft version, Forge coordinates from
> `forgeversion.properties`), a Solder `bin/version.json` on disk, and the "Vanilla" pack with no bin at
> all. 6 tests over hand-built staging folders drive each branch, including the jar-mod-with-no-known-MC
> failure. This completes the Technic install path: the parsing (`TechnicVersionJson`, `TechnicSolder`)
> was already ported; this is the orchestration that turns a downloaded pack into an instance.

> **Legacy FTB browser UI (wave 80).** The last piece: `LegacyFtbBrowserViewModel` (fetch the catalogue
> once, filter it in memory, pick a pack and version — 6 tests over a stub source), the
> `LegacyFtbBrowserWindow` (dark title bar, live filter, pack list + details/version, mirroring the
> Modrinth browser), and the app wiring — `ILegacyFtbBrowser`/`AppLegacyFtbBrowser` staging a
> `LegacyFtbPackInstallTask` behind the progress window, a `BrowseLegacyFtb` command, and a "Classic
> FTB" toolbar button. **Verified on the real .exe: the window opened and listed real packs from the
> live FTB CDN** (FTB Academy, Revelation, Direwolf20 1.12, …). Legacy FTB is now a complete,
> user-facing feature (waves 76–80).

> **Legacy FTB install task (wave 79).** Ported `legacy_ftb/PackInstallTask` to
> `Launch/LegacyFtbPackInstall.cs`: `LegacyFtbPackInstallTask` (an `IInstanceTask` that downloads the
> archive and stages it) and the testable `LegacyFtbPackBuilder.BuildFromArchive` (extract → move the
> game folder up → pick the install method: a Forge `pack.json`, an `instMods/` jar-mod folder, or
> neither, which fails as upstream's does → write mmc-pack.json + instance.cfg with the FTB logo icon).
> 3 tests against hand-built archives (no network) for the Forge, jar-mod and no-method outcomes. With
> waves 76–78 this makes legacy FTB installable end to end; only the browser UI remains.

> **Jar-mod install (wave 78).** Ported `PackProfile::installJarMods_internal` to
> `Launch/JarModInstaller.cs` — a Launch-level operation, since PackProfile is decoupled from instance
> I/O. Each jar is copied into `jarmods/` under a fresh id, a one-off component patch naming it as a
> `local` jar-mod library is written to `patches/`, and the component is appended to the profile and
> mmc-pack.json. 3 tests, including a round-trip of the written patch back through
> `OneSixVersionFormat.VersionFileFromJson`. This also unblocks the jarmod-fallback path of the legacy
> FTB installer (wave 77).

> **Legacy FTB install helpers + private packs (wave 77).** Ported the pure, bug-prone half of
> `legacy_ftb/PackInstallTask` and all of `PrivatePackManager` into `ModPlatform/LegacyFtb.cs`:
> `LegacyFtbInstall.ArchiveUrl` (the `{dir}/{version→underscores}/{file}` CDN layout under
> `modpacks/` vs `privatepacks/`), `LegacyFtbInstall.ForgeComponentVersion` (reads the Forge coordinate
> out of an old `pack.json` and strips the MC version and dashes), and `LegacyFtbPrivatePacks`
> (line-per-code persistence, save-only-when-dirty). 9 more tests. The stateful remainder — download +
> unzip into a staging instance, and the jarmod fallback — waits on `PackProfile.InstallJarMods`, which
> is unported (MinecraftInstance I/O); the Forge path needs only the ported `SetComponentVersion`.

> **Legacy FTB fetch/parse (wave 76).** Ported `modplatform/legacy_ftb/PackFetchTask` +
> `PackHelpers.h` to `ModPlatform/LegacyFtb.cs`: the models, the pack-list XML parser (with upstream's
> quirks — the ";"-separated version list, the "bugged" flag an empty entry raises, the current-version
> fallback and the "broken" flag), and a `LegacyFtbPackSource` that fetches the public/third-party/
> private lists from the FTB CDN. 8 tests (no upstream unit test exists), including a live probe that
> **passed against the real `dist.creeper.host/FTB2` CDN**. This is the discoverability half; installing
> a legacy pack (download + unpack the archive into an instance) and the browser UI are a follow-up.

> **CatPack (wave 75).** Ported `ui/themes/CatPack.cpp`'s `JsonCatPack` date-selection to
> `Core/CatPack.cs`, test-first against the shared `tests/testdata/CatPacks/index.json` — 13 tests, all
> the range-edge cases from `CatPack_test.cpp` (single-day variant, first-in-order wins on overlap, and
> the two ranges that wrap the new year). Only the pure selection logic is ported; the embedded-resource
> `BasicCatPack` path and the theme wiring stay with the UI.

> ✅ **The end-to-end launch is now pinned by a test.** `LaunchEndToEndLiveTests` creates and resolves
> a vanilla instance against the live meta server, then builds the JVM command line through the same
> `LauncherService.ResolveAsync` the GUI uses, and asserts it is a real Minecraft launch command
> (Mojang's entry point, the chosen version, LWJGL and the client jar on the classpath). It is
> metadata-weight — it does not download libraries/assets or spawn a JVM, and it skips rather than
> fails with no network. This was first confirmed by hand on **two platforms**: a vanilla instance
> created in the GUI launched to the Minecraft main menu on **Windows** (system Java, a real
> GPU-rendered window) and on **Linux** (WSL2 + WSLg, where the launcher auto-downloaded its own JRE
> and built a Linux command line with the correct native classifiers). macOS is not yet verified.

> ⚠️ **Unverified: the INI writer's byte-compatibility.** `INIFile` is a wrapper over `QSettings` with
> `IniFormat`, so the on-disk format is *QSettings' particular INI dialect* — and every existing
> `instance.cfg` on every user's disk is written in it. .NET has no equivalent, so `IniFile.cs`
> reimplements the dialect from Qt's `iniEscapedString` / `iniUnescapedStringList`.
>
> The **reader** is pinned against literal file content inherited from `INIFile_test.cpp`, which is
> real ground truth. The **writer** is verified by round-trip only: there was no Qt build or existing
> launcher install on the dev machine to diff against, and the repo ships no QSettings-written sample.
>
> **To verify before shipping:** take a real `instance.cfg` from any Prism/MultiMC/Extreme install,
> run it through `LoadFile` then `SaveFile`, and diff against the original. Any difference is a bug in
> `EscapeString`. Until that is done, treat settings persistence as unproven.

> ⚠️ **One deliberate behaviour change lives in `CopyTask`.** Upstream `FS::copy::operator()` returns
> `err.value() == 0`, where `err` is a *single* `std::error_code` reused across every file in the walk.
> Each `fs::copy` overwrites it, so the return value reflects only the **last** file: an early failure
> followed by a later success reports overall success. `FS::moveByCopy()` then calls `deletePath(source)`
> when `copy()` returns true — so a partially-failed copy can delete the original. This port fails the
> task if **any** file failed. It is the one place the port does not preserve upstream behaviour, and
> it is tested (`CopyFailsWhenAnyFileFailsEvenIfLaterOnesSucceed`). Revert it if you disagree.

> **Wave 2 is settled.** `Task` is now `LauncherTask`: an awaitable `RunAsync` driven by an abstract
> `ExecuteAsync`, with `CancellationToken` for aborts and events for observation. Three decisions bind
> everything downstream, and they are documented at length in the header of `Tasks/LauncherTask.cs`:
> failure is a **return value** at the boundary (so composites can collect several failures without
> unwinding), cancellation **also returns false** rather than throwing (a deliberate divergence from
> .NET convention, because the UI must tell "aborted" from "failed"), and post-run **state stays
> inspectable**. The deferred `FS::copy`/`clone`/`create_link` classes are now unblocked.

| Source | → | Ported | Tests |
|--------|---|--------|-------|
| `launcher/Version.{h,cpp}` | | `Core/Version.cs` | 56 — from `Version_test.cpp` + shared FlexVer vectors |
| `launcher/Exception.h` | | `Core/LauncherException.cs` | — |
| `launcher/DefaultVariable.h` | | `Core/DefaultVariable.cs` | covered via `GradleSpecifier` |
| `launcher/StringUtils.{h,cpp}` | | `Core/StringUtils.cs` | 24 — characterization only |
| `launcher/GZip.{h,cpp}` | | `Core/GZip.cs` | 5 — from `GZip_test.cpp` |
| `launcher/Json.{h,cpp}` | | `Core/Json.cs` | 19 — characterization only |
| `launcher/FileSystem.{h,cpp}` | | `Core/FileSystem.cs` | 57 — paths from `FileSystem_test.cpp`, I/O characterization |
| `launcher/tasks/Task.{h,cpp}` | | `Tasks/LauncherTask.cs` | 20 — from `Task_test.cpp`, plus failure/cancel cases |
| `launcher/tasks/ConcurrentTask.{h,cpp}` | | `Tasks/ConcurrentTask.cs` | ″ |
| `launcher/tasks/SequentialTask.{h,cpp}` | | `Tasks/ConcurrentTask.cs` | ″ |
| `launcher/tasks/MultipleOptionsTask.{h,cpp}` | | `Tasks/ConcurrentTask.cs` | ″ |
| `FS::copy` (in `FileSystem.cpp`) | | `Tasks/CopyTask.cs` | 23 — from the copy/link half of `FileSystem_test.cpp` |
| `FS::create_link` (in `FileSystem.cpp`) | | `Tasks/CreateLinkTask.cs` | ″ (1 skipped, see below) |
| `launcher/pathmatcher/*.h` | | `Core/PathMatchers.cs` | covered via copy/link |
| `launcher/net/{Sink,Validator,ChecksumValidator,ByteArraySink,FileSink}.*` | | `Net/Sinks.cs` | 34 — characterization, offline via a stub handler |
| `launcher/net/{NetRequest,Download,Upload,HeaderProxy,RawHeaderProxy,NetUtils}.*` | | `Net/NetRequest.cs` | ″ |
| `launcher/net/NetJob.{h,cpp}` | | `Net/NetJob.cs` | ″ |
| `launcher/java/JavaVersion.{h,cpp}` | | `Java/JavaVersion.cs` | 49 — all of `JavaVersion_test.cpp` |
| `launcher/java/JavaInstall.{h,cpp}` | | `Java/JavaInstall.cs` | ″ |
| `launcher/java/JavaUtils.{h,cpp}` | | `Java/JavaUtils.cs` | ″ (discovery only, see below) |
| `buildconfig/BuildConfig.{h,cpp.in}` | | `Core/BuildConfig.cs` | — (API keys intentionally blank) |
| `launcher/settings/INIFile.{h,cpp}` | | `Settings/IniFile.cs` | 29 — from `INIFile_test.cpp` |
| `launcher/settings/{Setting,OverrideSetting,PassthroughSetting}.*` | | `Settings/Setting.cs` | 16 — characterization |
| `launcher/settings/{SettingsObject,INISettingsObject}.*` | | `Settings/SettingsObject.cs` | ″ |
| `launcher/net/HttpMetaCache.{h,cpp}` | | `Net/HttpMetaCache.cs` | 17 — characterization |
| `launcher/net/MetaCacheSink.{h,cpp}` | | `Net/MetaCacheSink.cs` | ″ |
| `launcher/minecraft/GradleSpecifier.h` | | `Minecraft/GradleSpecifier.cs` | 17 — from `GradleSpecifier_test.cpp` |
| `launcher/RuntimeContext.h` | | `Minecraft/RuntimeContext.cs` | 25 — from `Library_test.cpp` |
| `launcher/minecraft/Rule.{h,cpp}` | | `Minecraft/Rule.cs` | ″ |
| `launcher/minecraft/Library.{h,cpp}` | | `Minecraft/Library.cs` | 37 — all of `Library_test.cpp`'s Library cases |
| `launcher/minecraft/MojangDownloadInfo.h` | | `Minecraft/MojangDownloadInfo.cs` | covered via `Library` |
| `launcher/minecraft/ParseUtils.{h,cpp}` | | `Minecraft/ParseUtils.cs` | 12 — all of `ParseUtils_test.cpp` |
| `launcher/ProblemProvider.h` | | `Core/ProblemContainer.cs` | covered via `MojangVersionFormat` |
| `launcher/minecraft/VersionFile.h` | | `Minecraft/VersionFile.cs` | ″ (Mojang fields only) |
| `launcher/minecraft/MojangVersionFormat.{h,cpp}` | | `Minecraft/MojangVersionFormat.cs` | 11 — from `MojangVersionFormat_test.cpp` |
| `launcher/meta/Version.{h,cpp}` | | `Meta/MetaVersion.cs` | 24 — `Index_test.cpp` + characterization |
| `launcher/meta/VersionList.{h,cpp}` | | `Meta/VersionList.cs` | ″ |
| `launcher/meta/Index.{h,cpp}` | | `Meta/Index.cs` | ″ |
| `launcher/meta/JsonFormat.{h,cpp}` | | `Meta/MetaJsonFormat.cs` + `Minecraft/MetadataFormat.cs` | ″ |
| `launcher/minecraft/OneSixVersionFormat.{h,cpp}` | | `Minecraft/OneSixVersionFormat.cs` | 22 — characterization |
| `launcher/minecraft/Agent.h` | | `Minecraft/Agent.cs` | covered via `OneSixVersionFormat` |
| `launcher/minecraft/LaunchProfile.{h,cpp}` + `VersionFile::applyTo` | | `Minecraft/LaunchProfile.cs` | 18 — characterization |
| `launcher/minecraft/Component.{h,cpp}` | | `Meta/Component.cs` | 16 — characterization |
| `launcher/minecraft/PackProfile.{h,cpp}` | | `Meta/PackProfile.cs` | 17 — characterization (core only) |
| `launcher/minecraft/ComponentUpdateTask.cpp` (resolution half) | | `Meta/DependencyResolver.cs` | 20 — characterization |
| `launcher/meta/BaseEntity.{h,cpp}` | | `Meta/MetaEntity.cs` | 14 — characterization, offline |
| `launcher/minecraft/ComponentUpdateTask.{h,cpp}` | | `Meta/ComponentUpdateTask.cs` | 23 — characterization |
| `MinecraftInstance::{javaArguments,extraArguments,processMinecraftArgs}` | | `Launch/LaunchCommandBuilder.cs` | 21 — characterization |
| `launcher/minecraft/AssetsUtils.{h,cpp}` | | `Minecraft/AssetsIndex.cs` | 18 — characterization |
| `launcher/minecraft/launch/ExtractNatives.{h,cpp}` | | `Launch/NativesExtractor.cs` | 17 — characterization |
| `launcher/LoggedProcess.{h,cpp}` + `MessageLevel.{h,cpp}` | | `Launch/LoggedProcess.cs` | 23 — characterization (1 skipped) |
| `launcher/launch/{LaunchStep,LaunchTask}.*` + `VerifyJavaInstall`, `CreateGameFolders`, `LauncherPartLaunch` | | `Launch/LaunchPipeline.cs` | 16 — characterization (2 skipped) |
| `MinecraftInstance` path accessors | | `Launch/InstancePaths.cs` | 11 — characterization |
| `launcher/MMCZip.{h,cpp}` (pure functions) | | `Core/MMCZip.cs` | 31 — characterization |
| `launcher/Commandline.{h,cpp}` (`splitArgs`) | | `Core/Commandline.cs` | 18 — characterization |
| `launcher/Untar.{h,cpp}` | | `Core/Untar.cs` | 23 — characterization (2 skipped) |
| (hard-link primitive, shared) | | `Core/NativeLink.cs` | covered via `CreateLinkTask`, `Untar` |
| `launcher/minecraft/ParseUtils.{h,cpp}` (moved) | | `Core/S3Time.cs` | (see wave 6 row) |
| `launcher/java/JavaMetadata.{h,cpp}` | | `Java/JavaMetadata.cs` | 26 — characterization, shared |
| `launcher/java/download/{Archive,Manifest}DownloadTask.*` | | `Java/JavaDownloadTasks.cs` | ↑ |
| `launcher/java/download/SymlinkTask.{h,cpp}` | | `Java/JavaDownloadTasks.cs` | ↑ |
| `minecraft/launch/{ModMinecraftJar,ClaimAccount,PrintInstanceInfo}.*` | | `Launch/InstanceSteps.cs` | 16 — characterization |
| `minecraft/update/{Folders,Libraries,AssetUpdate,FMLLibraries}Task.*` | | `Launch/UpdateTasks.cs` | 18 — characterization |
| `launcher/SysInfo.{h,cpp}` (platform strings) | | `Core/SysInfo.cs` | 3 — characterization |
| `launcher/java/JavaChecker.{h,cpp}` + `libraries/javacheck/` | | `Java/JavaChecker.cs` | 22 — characterization, incl. a real JVM |
| `launcher/minecraft/mod/Resource.{h,cpp}` | | `Minecraft/Mods/Resource.cs` | 30 — SIX inherited suites |
| `mod/{ResourcePack,DataPack,TexturePack,ShaderPack,WorldSave}.*` + parsers | | `Minecraft/Mods/ResourcePacks.cs` | ↑ |
| `mod/{Mod,ModDetails}.*` + `tasks/LocalModParseTask.cpp` | | `Minecraft/Mods/Mod.cs` | 30 — characterization |
| `mod/tasks/{BasicFolderLoadTask,ModFolderLoadTask}.*` | | `Minecraft/Mods/ResourceFolder.cs` | 13 — characterization |
| `minecraft/launch/ScanModFolders.{h,cpp}` | | `Launch/ScanModFolders.cs` | ↑ |
| `launcher/settings/INISettingsObject.{h,cpp}` | | `Settings/SettingsObject.cs` | 11 — characterization |
| `BaseInstance` + `MinecraftInstance` settings | | `Launch/InstanceSettings.cs` | 15 — characterization |
| `launcher/InstanceList.{h,cpp}` (discovery, groups) | | `Launch/InstanceList.cs` | 24 — characterization |
| `modplatform/packwiz/Packwiz.{h,cpp}` | | `Minecraft/Mods/Packwiz.cs` | 24 — from `Packwiz_test.cpp` |
| `minecraft/launch/{AutoInstallJava,ReconstructAssets}.*` | | `Launch/AutoInstallJava.cs` | 16 — characterization |
| `launcher/minecraft/auth/AccountData.{h,cpp}` | | `Minecraft/Auth/AccountData.cs` | 47 — characterization, shared |
| `launcher/minecraft/auth/AuthSession.{h,cpp}` | | `Minecraft/Auth/AuthSession.cs` | ↑ |
| `launcher/minecraft/auth/MinecraftAccount.{h,cpp}` + `Usable.h` | | `Minecraft/Auth/MinecraftAccount.cs` | ↑ |
| `launcher/minecraft/auth/Parsers.{h,cpp}` | | `Minecraft/Auth/Parsers.cs` | 44 — characterization |
| `launcher/minecraft/auth/AuthStep.h` | | `Minecraft/Auth/AuthStep.cs` | 44 — characterization, shared |
| `launcher/minecraft/auth/AuthFlow.{h,cpp}` | | `Minecraft/Auth/AuthFlow.cs` | ↑ |
| `auth/steps/{XboxUser,XboxAuthorization,LauncherLogin,XboxProfile,Entitlements,MinecraftProfile,GetSkin}Step.*` | | `Minecraft/Auth/Steps.cs` | ↑ |
| `launcher/minecraft/auth/steps/MSADeviceCodeStep.{h,cpp}` | | `Minecraft/Auth/MSADeviceCodeStep.cs` | 22 — characterization |

`FileSystem` is **partial** — see the deferred list. Path logic, file I/O, and filesystem probing are
done; the link/copy/clone machinery is not.

### Deferred, with reasons

- **`FS::copy`, `FS::clone`, `FS::create_link`** — `QObject`/`QThread` classes built on Qt signals and
  progress reporting. They are the single biggest reason to settle wave 2 first: porting them before
  `Task` is reshaped onto `Task<T>`/`IProgress<T>`/`CancellationToken` would bake in the signal-slot
  shape the port exists to shed. Their upstream tests (13 of the 21 in `FileSystem_test.cpp`) travel
  with them.
- **`ExternalLinkFileProcess`, `create_link::runPrivileged`** — `QLocalServer` IPC with the elevated
  `filelink` helper. Wave 9, with the helper.
- **`clone_file` + `win_ioctl_clone` / `linux_ficlone` / `macos_bsd_clonefile`** — per-platform reflink
  ioctls, one P/Invoke surface each. Wave 9.
- **`createShortcut`, `trash`, `hardLinkCount`, `getPathNameInLocal8bit`** — each needs platform APIs
  with no BCL equivalent (COM `.lnk`, `SHFileOperation`, `GetFileInformationByHandle`,
  `GetShortPathNameW`). Wave 9.
- **`StringUtils::truncateUrlHumanFriendly`** — depends on `QUrl::toDisplayString` semantics and has a
  single caller in `launcher/net/NetRequest.cpp`. Wave 3, alongside URL handling.
- **`StringUtils::toStdString` / `fromStdString`** — Qt↔std interop, no C# equivalent. Dropped.
- **`Json` `QVariant` and `QDir` specializations** — `JsonNode` already covers "any JSON value", and
  callers should use path strings. Dropped.

### Wave 3 (Net) — still outstanding

- **`ApiDownload` / `ApiUpload` / `ApiHeaderProxy`** — thin wrappers that inject the CurseForge and
  Modrinth API keys. `BuildConfig` now exists, so these are unblocked — they just need writing.
- **`PasteUpload`** — depends on the paste-service settings in `BuildConfig`.

### Wave 6 (Minecraft) — in progress

`Library` is **complete** — paths, rules, natives and downloads, against the whole of
`Library_test.cpp`. Still outstanding in this wave:

- **`OneSixVersionFormat`** — the launcher's own richer patch format. It fills the `VersionFile`
  fields currently declared but unpopulated: `addTweakers`, `jarMods`, `agents`, `traits`,
  `requires`/`conflicts`, `mavenFiles`, `runtimes`.
- ~~**`ComponentUpdateTask`'s loading half**~~ — done (`Meta/ComponentUpdateTask.cs`); see the wave 5
  notes for what it does and what diverges. The *resolution* half remains `DependencyResolver`,
  decoupled from loading exactly as upstream's own FIXME asks for.

`LaunchProfile` (the merge target), `Component` (one entry) and `PackProfile`'s stack-management core
(ordering, membership, mmc-pack.json persistence, profile building) are done. `PackProfile` still
lacks `reload`/`resolve` (they drive `ComponentUpdateTask`), the `install*` methods (MinecraftInstance
file I/O), the Qt model surface, and the mod-loader queries (wave 8). `Component`
still lacks `KNOWN_MODLOADERS`/`knownConflictingComponents`, which need `ModPlatform::ModLoaderType`
from wave 8. The `UpdateAction` variant is now here, as a closed record hierarchy rather than a
`std::variant` visited with an overload set.

> 🔒 **Security fix: zip-slip in native extraction.** Upstream's `unzipNatives` extracts each entry to
> `directory.absoluteFilePath(name)` with no check that the result stays inside the target, so a jar
> containing an entry named `../../something` writes outside the natives folder. Native jars are
> fetched over the network from Mojang and mod repositories, so this is reachable rather than
> theoretical. `NativesExtractor` refuses escaping entries and fails the step. Tested
> (`AnEntryEscapingTheTargetDirectoryIsRefused`).
>
> **Also noted, not changed:** `Library::m_extractExcludes` is parsed by both version formats and read
> by *nothing* — the `"extract": { "exclude": ["META-INF/"] }` block on every native library is
> decorative, and extraction takes the whole archive. Preserved by default; `applyExcludes: true`
> honours the field if you want it.

> ⚠️ **A fourth upstream bug — this one BEHAVIOURAL, so check it.** In `PackProfile`,
> `componentToJsonV1` writes `"cachedVolatile"` but `componentFromJsonV1` reads `"volatile"`, so the
> volatile flag never survives a reload and volatile components are never auto-removed. This port
> reads **both** keys, which is backward compatible — but it activates a cleanup path that has
> effectively been dormant, so components previously kept forever may now be removed once nothing
> needs them. That is the intended design, but it is a real behaviour change. Revert to reading only
> `"volatile"` if you would rather keep the status quo.

> ⚠️ **A third upstream bug fixed, in `LaunchProfile`.** `applyMods()` loops over incoming mods but
> `return`s instead of `continue`ing after appending a new one, so a patch contributing several
> previously-unseen mods only ever applies its first. Almost certainly a copy-paste from
> `applyLibrary()`, which is single-item and where `return` is correct. Fixed and tested
> (`EveryNewModIsApplied`).

> ⚠️ **A second upstream bug fixed, in `OneSixVersionFormat`.** `versionFileToJson()`'s `"mods"` block
> guards on `patch->mods` but iterates `patch->jarMods`, so serializing a patch that carries both
> writes the jar mods out twice and loses the mods entirely. Unlike the `CopyTask` divergence this one
> has no plausible upside — it corrupts user patch files on every save — so it is fixed and tested
> (`ModsAndJarModsAreSerializedFromTheirOwnLists`).

> **Contract deliberately weakened, with reason.** `MojangVersionFormat_test.cpp` asserts
> `QCOMPARE(doc.toJson(), doc2.toJson())` — a *byte-identical* round trip. That holds only because
> both sides use Qt's JSON writer (alphabetical keys, 4-space indent); `System.Text.Json` does
> neither, so a literal port would mean reimplementing Qt's writer to test a formatting coincidence.
> The ported tests assert a **semantic** round trip instead: deep-compare the whole re-serialized tree
> against the original fixture. That still catches a dropped field, an invented field, a mangled
> value or a lost list element — everything the original protected against except byte layout, which
> no consumer depends on.

### Wave 5 (Meta) — still outstanding

- ~~**`Index.LoadVersion` / `GetLoadedVersion`**~~ — done, as `CreateLoadVersionTask` /
  `GetLoadedVersionAsync` / `CreateVersionLoader`. **The chain is a DEPENDENCY chain, not a
  preference**: the index names the hash of each version list, and a version list names the hash of
  each version document, so bringing one version up to date means refreshing the two above it first —
  otherwise a changed document is validated against a stale hash. The index step is skipped once it
  has been fetched remotely this session, because it is the one document nothing vouches for and
  re-fetching it would make every launch wait on the meta server; offline the chain collapses to the
  version document alone.

  `force` overrides the CHAIN's own skip, not the entity's — a forced chain over an already-current
  index still fetches nothing, because each load task short-circuits on a current entity. Upstream has
  the same two-level arrangement, and the test says so rather than asserting a re-fetch that does not
  happen.

  `getLoadedVersion` upstream spins a nested `QEventLoop` to make itself synchronous, which is how a
  UI thread ends up re-entered halfway through a network fetch. Awaitable here.

  **`ComponentUpdateTask` now resolves a cold cache end to end**, through `CreateVersionLoader` — the
  seam that keeps it testable, taking the fetch as an injected delegate rather than reaching for a
  global application object the way upstream does.

  **A cold-cache bug turned up while wiring this and is fixed.** `Index.Get(uid, version)` is a pure
  lookup and returns null when nothing is known — which, on a first run, is *every* component, because
  the index has not been read yet. An earlier draft of `ComponentUpdateTask` treated that as "no
  metadata is available" and failed the whole path every new install takes. Upstream's `getVersion`
  creates a placeholder for exactly this reason: the document's URL is derivable from the uid and
  version alone, so it can be fetched before anything above it is known. `GetOrCreateVersion` /
  `GetOrCreate` carry that behaviour; the plain `GetVersion` / `Get` stay non-creating, because a
  caller asking "is this known?" must not quietly populate the list.

**`ComponentUpdateTask`'s loading half is done** (`Meta/ComponentUpdateTask.cs`), and it is where the
wave-2 rewrite pays off most visibly. Upstream tracks remote loads through a `RemoteLoadStatus` list
indexed by task, with `remoteLoadSucceeded` / `remoteLoadFailed` / `checkIfAllFinished` counting
completions against a `remoteTasksInProgress` field — roughly a hundred lines whose only job is "wait
for all of these". Here it is one `Task.WhenAll`. The bug class that disappears is real: upstream's
`remoteLoadSucceeded` has to guard against being invoked twice for the same index, and *warns* rather
than failing when it is.

Behaviours worth knowing, all tested:

- **A local patch file always beats the meta server.** That is the entire point of the patches folder:
  a user who hand-edited `net.minecraft.json` gets their version, not Mojang's — and it applies
  offline, because that path never reaches the index at all.
- **A patch whose stored `uid` disagrees with the component it was loaded for is rewritten on disk.**
  Files get copied between instances and renamed by hand, and a mismatched uid makes the patch
  invisible to dependency resolution, so the mismatch is corrected rather than reported. Upstream's
  own note on the save is "FIXME: @QUALITY do not ignore return value"; it stays ignored, because a
  read-only patches folder should not stop a launch that already holds a perfectly good patch.
- **Launch mode and offline mode only REPORT.** Resolution never rewrites versions under someone who
  is trying to play. `Resolution` is exposed afterwards so a caller can show what *would* change.
- **Severity is load-bearing.** A missing requirement or a wrong exact version is an error; being off
  the merely *suggested* version is a warning. Conflate them and every modpack shows a wall of red. A
  dependency's own problems propagate upward at their own severity, so the list points at the entry to
  look at rather than only the one that ultimately broke.
- **The update-action loop repeats until a pass queues nothing new.** Applying an `ImportantChanged`
  queues actions on everything linked to that component, and those queue more in turn — changing
  Minecraft's version forces a new Forge, which forces a new Forge-dependent library. One pass leaves
  the profile half-updated, which is worse than not updating it.
- **`VersionList.GetBetterVersion`: type beats recency.** A `release` wins over a snapshot however
  much newer the snapshot is; only within one type does the newer win. That is what stops a fresh
  snapshot quietly becoming the recommended build for a modloader.

**One deliberate divergence.** Upstream's `performUpdateActions` calls `waitLoadMeta()` — a nested
event loop — when a version change needs a document that is not cached. This port leaves the component
unloaded and lets `FinalizeComponents` report it, because blocking a task on a nested event loop is
exactly what wave 2 removed. The observable difference only appears when a resolution-mode change
picks a version whose metadata has never been fetched.
- **`Index`'s `QAbstractListModel` surface** — UI wave.

**`MetaVersion.IsLoaded` is virtual where upstream's is not.** `Meta::Version::isLoaded()` is
`m_data != nullptr && BaseEntity::isLoaded()` — both halves, and C++ resolves it by *static type*, so
calling it through a `BaseEntity*` silently returns the looser base answer. An earlier draft of this
port dropped the second half entirely, which meant a version parsed out of a stale on-disk file
reported itself loaded and nothing ever fetched the current copy. The base property is now `virtual`
and the derived one `override`s it, so every call site gets the strict answer. Both halves are tested,
including through a base reference.
- ~~`ParseVersion` does not populate `MetaVersion.Data`~~ — done, now that `OneSixVersionFormat`
  exists.

**Project-boundary note.** Upstream's `OneSixVersionFormat.cpp` and `meta/JsonFormat.cpp` include each
other. C++ headers permit that; C# projects do not. `Require`/`RequireSet` and the format-version and
requires helpers therefore live in `ExtremeLauncher.Minecraft` (`Require.cs`, `MetadataFormat.cs`),
the lower layer, with `MetaJsonFormat` forwarding to them so the Meta-side API is unchanged.

Note: `MetaComponentParse_test.cpp` sounds like it belongs here but does not — it targets
`ResourcePackUtils::processComponent` in `minecraft/mod/tasks/`, and travels with the resource
parsers. Wave 5's only inherited contract is `Index_test.cpp`.

### Wave 7 (Launch) — complete

`ExtremeLauncher.Cli` sits on top of it: `list`, `info` (with `--libraries` to dump resolved download
URLs), `launch` (`--offline`, `--name`, `--java`, `--server`, `--dry-run`), `java`, `version`. Not a
ported file — upstream has no headless mode — but the milestone this document set for the end of wave
7, and the thing that found four defects the unit suite could not. See *The headless CLI ran* below.

Command-line construction is done: JVM flags, classpath, main class and the game's argument template
with `${token}` substitution. Extracted from `MinecraftInstance` rather than ported onto it — upstream
builds the command line as methods on a 1,256-line class that also owns settings, paths, logging, mod
folders and the window title. Everything the construction needs is passed in instead.

**`minecraft/update/*` is done** (`Launch/UpdateTasks.cs`) — the four tasks that put the game on disk
before it can start. A resolved `LaunchProfile` says *which* jars and assets are required; these fetch
them, and together they are the difference between "the launcher knows what 1.20.1 is" and "the
launcher can run 1.20.1".

- **`LibrariesTask` covers five artifact pools**, and the composition is the whole point: classpath
  libraries, natives, maven files that are downloaded but never put on the classpath, each agent's own
  jar, and the main jar. Miss one and the game starts with a `NoClassDefFoundError` rather than a
  launcher error — far harder for a user to report usefully. A **"local"** artifact is one the
  metadata says the user must supply (a jar the launcher may not redistribute); a missing one is
  reported *before any request goes out*, with instructions, rather than as a failed download.
- **`AssetUpdateTask` is two rounds that cannot be merged.** The index is a document listing thousands
  of hashed objects, so what to fetch second is unknown until the first has parsed. A corrupt index is
  **evicted** rather than left cached — kept, it would be re-read and re-rejected on every launch with
  no way for the user to break the loop. Assets are content-addressed, so an object already on disk
  cannot have changed and is never re-fetched.
- **`FMLLibrariesTask` is a 1.3–1.5 era arrangement.** FML looked for a handful of jars *by filename*
  in `<instance>/lib` rather than taking them from the classpath. They download to the shared cache and
  are then **copied** in, not linked: FML resolves them by path inside the instance, and a hard link
  into the shared cache would let one instance's edits reach every other. The mapping table is frozen
  data — filenames and SHA-1s fixed when those versions shipped.

**`AutoInstallJava` and `ReconstructAssets` are done** (`Launch/AutoInstallJava.cs`), which closes the
last of `minecraft/launch/*` that does not wait on another wave.

**`AutoInstallJava` NEVER FAILS A LAUNCH**, and that is the property to preserve when touching it.
Every path that cannot produce a runtime logs a warning and succeeds, leaving whatever Java the user
already has: an unpublished platform (FreeBSD, OpenBSD — `SupportedJavaArchitecture` returns empty and
that is *meaningful*, not an error), out-of-date metadata with no `compatibleJavaName`, a meta server
outage, a broken download, or simply running out of candidate majors. Upstream's retry loop is a chain
of signal callbacks re-entering `tryNextMajorJava` with an index member counting position; here it is
a `foreach`. A **half-unpacked runtime is deleted** — left behind, the "already installed" check would
find the directory next launch and hand the game a path with no interpreter in it.

**`SysInfo` carries two vocabularies that must not be confused.** `CurrentSystem`/`CurrentArchitecture`
are the RULE vocabulary, matched against a version JSON's `os.name`/`os.arch`: `"osx"`, `"x86_64"`.
`SupportedJavaArchitecture` is the RUNTIME-DOWNLOAD vocabulary, matched against a Java metadata
entry's `runtimeOS`: `"mac-os-arm64"`, `"linux-x64"`. They look similar, are used for different
lookups, and a swap silently stops matching anything. Note also that .NET's `Architecture` enum spells
things differently again (`"X64"`), so `CurrentArchitecture` translates rather than prints.

**Another layering inversion undone.** `VersionFile.Runtimes` holds `Java.JavaMetadata`, so
`ExtremeLauncher.Minecraft` now references `ExtremeLauncher.Java` — which is upstream's own direction
(`VersionFile.h` includes `java/JavaMetadata.h`) and introduces no cycle, since Java references only
Core, Tasks and Net.

Still outstanding:

- **`LaunchTask` / `LaunchStep`** (~470 lines) — the step-sequencing framework. Maps onto
  `SequentialTask` from wave 2 rather than needing its own machinery.
- **`minecraft/launch/*`** (22 files) — the individual steps. Done: `ExtractNatives`
  (`Launch/NativesExtractor.cs`), `VerifyJavaInstall`, `CreateGameFolders`, `LauncherPartLaunch` and
  the step sequencing (`Launch/LaunchPipeline.cs`, built on wave 2's `SequentialTask` rather than
  upstream's hand-rolled state machine), plus `ModMinecraftJar`, `ClaimAccount` and `PrintInstanceInfo`
  (`Launch/InstanceSteps.cs`). `ReconstructAssets` is effectively done
  (`AssetsIndex.ReconstructVirtualTree`) but still needs a step wrapper. Still needed:
  ~~`ScanModFolders`~~ and ~~`AutoInstallJava`~~, both now done.

  **`ScanModFolders` reads the mod folders before the game starts so the LOG records what was
  loaded.** That is its whole purpose: when a user reports a crash, the first question is which mods
  were installed. It never fails a launch — a folder that cannot be read is worth a warning and
  nothing more. Upstream runs three folder scans concurrently and joins them with three bool members
  and a `checkDone()` called from each completion slot; here it is a loop.

  **A DISABLED FILE SUPERSEDES ITS ENABLED TWIN.** When both `foo.jar` and `foo.jar.disabled` exist,
  only the disabled one is listed: they are the same mod, and showing it twice would let a user enable
  one copy while the other is still there — which the game sees as a duplicate and refuses to start
  on. The pairing is resolved in a second pass, after everything has been seen, so the answer does not
  depend on the order the filesystem listed the directory.

  Three mod folders are scanned, not one: `mods`, `coremods` (a pre-1.6 Forge arrangement) and
  `nilmods` (NilLoader's). The last two are empty for almost every instance, but skipping them would
  silently hide the mods of the people who still use them. On a filename collision `mods` wins, being
  the folder anything modern lives in.

  **Upstream bug #8, in `ModMinecraftJar::executeTask`, fixed here.** Both of its early checks — "could
  the bin folder be created" and "could the stale jar be removed" — call `emitFailed()` and then **fall
  through with no return**. So a launcher that cannot create the bin folder reports the failure and
  then tries to build a jar inside the folder that does not exist, emitting a second, contradictory
  result. Neither is reproducible in a step that returns or throws, and reproducing the double-emit
  would mean deliberately continuing into work that cannot succeed.

  **`ClaimAccount` is the lock that keeps a running game's session valid.** It increments the account's
  use count for the duration, and `MinecraftAccount.ShouldRefresh` returns false while it is held —
  refreshing under a running game invalidates the token the game is holding and drops the player from
  whatever server they are on. It is taken only for a real online, non-demo session, cannot be aborted,
  and nests, so two games on one account release it correctly.

  **The pipeline order is upstream's, and it is not the order that looks obvious.** From
  `MinecraftInstance::createLaunchTask`: folders → claim account → mod the jar → print info → extract
  natives → **verify Java** → launch. An earlier draft of this port put `VerifyJavaInstall` **first**,
  on "fail before anything is written" reasoning. That was wrong twice over: the pipeline's reverse
  unwind cleans up regardless, so nothing is actually left behind either way, and the compatible-major
  list the check reads is produced by the component update that runs before all of it. Log ordering is
  also observable to anyone comparing a pasted log against the Qt launcher's. Pinned by
  `TheStandardPipelineFollowsUpstreamsOrder`.

  **Wrapper commands are supported** (`prime-run`, `gamemoderun`, `mangohud`). The user types a whole
  command line into a settings box, so it goes through `Commandline.SplitArgs`; the first token is the
  program and the rest are prepended ahead of the java path. A wrapper that cannot be found **fails the
  launch** rather than being skipped — starting without it would run the game on the wrong GPU, or
  without the frame limiter the user set up, with nothing in the log to say why.

  **`PrintInstanceInfo` shells out, and upstream waits forever.** Its Linux and FreeBSD probes run
  `lspci`, `glxinfo`, `sysctl` and `pciconf` through `popen` with no timeout — and a wedged `glxinfo`
  on a broken GL driver, which is exactly the case the probe exists to diagnose, would hang the launch
  indefinitely. There is a five-second timeout per probe here. Everything it prints is best-effort;
  a missing tool is silent.
- **`MinecraftInstance`** (1,256 lines) — the path accessors and the `LaunchOptions` assembly are
  done (`Launch/InstancePaths.cs`). Still on that class: settings plumbing, the mod folders, the
  window title, log rotation and the instance-type machinery — all of which belong with the UI and
  instance-management waves rather than with launching.
- **`minecraft/auth/`** (31 files) — done apart from two pieces. Ported: the account model and its
  accounts.json v3 reader and writer (`Auth/AccountData.cs`), sessions (`Auth/AuthSession.cs`),
  offline accounts and the refresh policy (`Auth/MinecraftAccount.cs`), every response parser
  (`Auth/Parsers.cs`), the step contract and request plumbing (`Auth/AuthStep.cs`), the seven service
  steps (`Auth/Steps.cs`), the device-code login (`Auth/MSADeviceCodeStep.cs`) and the sequencing
  (`Auth/AuthFlow.cs`).

  Wave 15 added the rest of what a launcher needs to actually sign somebody in: the account list
  (`Auth/AccountList.cs`), the silent refresh (`Auth/MSARefreshStep.cs`), and the UI that drives them.

  Still outstanding:

  - **`steps/MSAStep.cpp`** — the authorization-code login, whose SILENT half is now ported as
    `MSARefreshStep`. What remains is the interactive browser half.
    It is built on `QOAuth2AuthorizationCodeFlow`, a loopback `QOAuthHttpServerReplyHandler`, and a
    per-platform check for whether the launcher's URL scheme handler is registered (an `xdg-mime`
    query on Linux, a registry read on Windows). All three are desktop-integration concerns rather
    than protocol, so it travels with the UI wave. **The device-code flow is the headless-friendly
    alternative and is fully ported**, so a login is possible without it.
  - **`AccountList`** (696 lines) — a `QAbstractListModel`. UI wave.

  `LaunchSession`, the placeholder the argument builder took while auth was unported, is gone;
  `AuthSession` replaces it, which is what upstream's builder reads directly.

  **These steps deliberately do not use `ExtremeLauncher.Net`**, and the reason is not stylistic:
  `NetRequest` discards the body of a failed response, and for auth the body of a failure *is* the
  diagnosis. XSTS returns its real explanation — "this account is underage and not linked to a
  family" — as an `XErr` code inside the body of a 401, and the device-code flow reads
  `authorization_pending` out of the body of a 400. Routing auth through `NetRequest` would turn every
  one of those into a bare "authentication failed". Upstream also calls `setAskRetry(false)` on all of
  them, so NetJob's retry machinery has nothing to offer either. `AuthHttp` in `Auth/AuthStep.cs` is a
  single send that keeps the body both ways.

  **`FailedSoft` versus `Offline` is the distinction to preserve when touching any of this.** A server
  that answered with a refusal leaves the account `Errored`; a network that never carried the question
  leaves it `Offline`. Only the second is worth retrying by itself, and getting them backwards sends
  users to re-authenticate an account that was never broken. Upstream draws the line with
  `Net::isApplicationError`; here it is `AuthResponse.IsApplicationError`, and it is tested per step.

  Two steps **never fail, inherited**: `EntitlementsStep` does not check for errors at all (ownership
  is re-derivable from whether a profile comes back), and `GetSkinStep` swallows its own (an account
  with no skin is perfectly playable). `XboxProfileStep` fetches a body and discards it — it is a
  liveness check on the Xbox-scoped token, not a source of data; the gamertag comes from the `gtg`
  display claim that `XboxAuthorizationStep` already stored.

  Three things in this area are worth knowing about:

  - **Upstream bug #7, in `XboxAuthorizationStep::onRequestDone`, FIXED here.** `processSTSError()`
    returns true when it has *already* emitted a specific, actionable message — and the caller's
    branches are the wrong way round, so a recognized error emits its real explanation and then
    immediately emits "Unknown STS error" on top of it. The user is told nothing useful precisely when
    the launcher knew exactly what was wrong. The signal-based double-emit has no equivalent in a step
    that returns a single result, so this cannot be reproduced structurally, and reproducing its
    visible effect would mean deliberately discarding the good message. The specific message wins here,
    and each of the eight known `XErr` codes is tested.

  - **Offline UUIDs are a contract with the vanilla server, not with this code.** An offline player's
    identity is a version-3 UUID over MD5(`OfflinePlayer:<name>`) — Java's `UUID.nameUUIDFromBytes`.
    Java and `QUuid::fromRfc4122` read the digest big-endian; .NET's `Guid(byte[])` reads the first
    three fields **little-endian**. Handing the digest straight to it yields a byte-swapped UUID that
    is impossible to spot by inspection and makes the player a stranger in their own world. The test
    vectors were computed independently of the implementation for exactly that reason.
  - **The v3 reader is strict where upstream is strict.** A profile missing a mandatory skin field, or
    with a `capes` that is not an array, is discarded *whole* rather than half-loaded — a half-loaded
    profile gets written straight back on the next save and the unparsed half is gone. Same for typed
    fields: an `ownsMinecraft` of `"true"` (a string) is not believed. `AccountData.cs` uses explicit
    `JsonValue.TryGetValue<T>` probes rather than the `Json.Ensure*` helpers for this reason — the
    helpers coerce, and coercion here would turn a corrupt file into a confident claim.

  Carried-over quirks, all tested: pre-7.2 accounts stored `"offline"` as the offline token and are
  migrated to `"0"` on load; a token block with timestamps but no token, refresh token or extras is
  dropped entirely rather than written as a stub; a session that falls back to a derived UUID still
  builds its combined session id from the *empty* profile id; `MakeDemo` leaves the session
  `PlayableOnline` despite clearing `WantsOnline`, because the demo still downloads its assets; and an
  **offline account run through `AuthFlow` gets no steps at all**, so the empty list runs to the end
  "without incident" and the account is marked `Certain` and `Online` without a byte crossing the
  network. Surprising to read, correct in effect — there is nothing to check.

  **The device-code polling rules are RFC 8628's, and the arithmetic is load-bearing.**
  `authorization_pending` waits at the current interval; `slow_down` adds five seconds *permanently*,
  for this and every later request; a connection timeout *doubles* the interval, likewise permanently;
  any other failure retries unchanged. Polling faster than the server allows gets the client throttled
  or blocked outright, so none of these is a detail to tidy up. A code about to expire triggers a fresh
  device-code request rather than an error — upstream recurses into `perform()` to do it, which is a
  loop here so the stack does not grow with the number of expiries. The delay function is injected, so
  the tests pin all of this without waiting on a real clock.

### Wave 6 (Minecraft) — the resource kinds

`Minecraft/Mod/` holds the five resource types that can be identified by looking at their contents,
plus the `Resource` base that reads what a filename alone can tell. Between them they satisfy **six
inherited Qt suites** — `ResourcePackParse`, `DataPackParse`, `TexturePackParse`, `ShaderPackParse`,
`WorldSaveParse` and `MetaComponentParse` — run against the Qt suite's own fixtures rather than copies,
so both implementations answer to the same zips and folders.

**One validity rule per kind, and they differ in ways worth knowing.** A resource pack needs
`pack.mcmeta` *and* an `assets` directory; a data pack needs `pack.mcmeta` *and* a `data` directory; a
texture pack needs `pack.txt`, a format that predates `pack.mcmeta` entirely and whose whole contents
are the description; a shader pack needs a `shaders` directory and nothing else, because there is no
manifest to check; a world save needs a folder holding `level.dat`, possibly under `saves`.

**The world-save format is decided by how the user exported it.** A world folder on its own is
`Single`; the whole `saves` directory is `Multi`. Both arrive in the same drop target, so both have to
be recognised, and which it was is what the format records.

**Text components inherit only two of their styles.** `ProcessComponent` renders Minecraft's component
tree to HTML, and underline and strikethrough are threaded into nested `extra` arrays while colour,
bold and italic are re-read from each object. That asymmetry is upstream's, and the five inherited
`MetaComponentParse` vectors pin it exactly — they carry both the input and the precise HTML upstream
produces, so this is ground truth rather than characterization. Note also that `"bold": false` emits
`font-weight: normal` rather than nothing: a nested component has to be able to turn OFF what it
inherited, so an absent property and an explicit `false` are different.

**Disabling a resource is a RENAME** — a `.disabled` suffix on the filename — so the type has to be
worked out from what is left after the suffix comes off. A disabled jar is still a jar. That spelling
is shared across this whole launcher lineage, so a pack disabled here stays disabled after a move to
another launcher.

**Mod metadata is six formats** (`Minecraft/Mods/Mod.cs`), because six loaders each invented their own
and none agreed: `mcmod.info` (Forge pre-1.13, JSON, sometimes a bare array), `META-INF/mods.toml`
(Forge 1.13+ and NeoForge, TOML), `fabric.mod.json`, `quilt.mod.json`, `litemod.json`, and
`forgeversion.properties` (Forge itself, as INI, with the version split across four keys). A jar is
checked against all of them **in a fixed order**, because a mod may ship several — a Fabric mod with a
Forge shim carries both — and the first found decides which loader's view the launcher shows.

The quirks are the point; each exists because some mod in the wild does it that way, and dropping one
means that mod appears as a nameless file:

- `mcmod.info` may be a bare array (2013-era), or wrapped under `modlist` **or** `modList`; the version
  key is `modinfoversion` **or** `modListVersion`, and some mods write it as a string.
- A `name` of exactly **"Example Mod"** is discarded — a great many mods ship the template unchanged,
  and a folder full of "Example Mod" helps nobody.
- In `mods.toml`, four fields (authors, displayURL, issueTrackerURL, license) may sit **either** at the
  file's top level **or** inside the `[[mods]]` element, because the template moved them and both forms
  shipped. Top level is checked first, matching upstream.
- Fabric gates everything past the basics on `schemaVersion >= 1`; version 0 predates those fields, so
  reading them would be inventing data.
- Icons may be a path or a map of sizes; the **largest** wins, so the launcher downscales rather than
  stretches. An unparseable size key still yields its path.
- Bare hostnames get **`http://`**, not https — these URLs come from mods written a decade ago, and
  upgrading one with no TLS breaks the link rather than fixing it.

**A NuGet dependency was added** — `Tomlyn` 0.19.0, for `mods.toml`. Upstream vendors `toml++` for the
same reason. Pinned to 0.19 deliberately: 2.x replaces the `Toml.TryToModel` entry point with a
different serializer API.

**NOT PORTED:** the NilLoader format, which needs the QDCSS parser (a CSS-like dialect used by nothing
else in the codebase).

**A NAMING COLLISION forced a rename.** Upstream has `class Mod` in `launcher/minecraft/mod/`; in C# a
type cannot share its simple name with its own namespace and still resolve from outside, so the
namespace is `ExtremeLauncher.Minecraft.Mods` (plural) while the type stays `Mod`.

**NOT PORTED from this area:** the `QObject` and model surface — signals, `compare()`, `applyFilter()`
and the sort machinery — which exists to drive `ResourceFolderModel`, a `QAbstractListModel` bound for
the UI wave. `LocalModParseTask` (753 lines, every mod-loader metadata format) and the folder models
are the rest of `minecraft/mod/` and are still outstanding.

### Wave 6 (Minecraft) — instance settings

`Launch/InstanceSettings.cs` holds every key an `instance.cfg` contains, plus the launcher-wide
defaults in `GlobalSettings`. **These names and defaults are a compatibility surface, not a design
choice** — users already have these files on disk, and a renamed key silently resets whatever it used
to hold.

**THE OVERRIDE PATTERN IS THE WHOLE DESIGN.** Almost nothing is a plain instance setting. A boolean
gate (`OverrideMemory`, `OverrideJavaLocation`, ...) decides whether the instance's own value or the
global one applies, and the gated settings are registered as overrides of the global object's. Three
consequences worth knowing:

- Turning a gate off restores the global value **without losing** what the instance had, so a user can
  toggle back and forth while deciding. A delete could not do that.
- **One gate covers a whole group**: "override memory" means min, max and permgen together. A UI
  cannot offer them separately without a gate each, and upstream chose the group.
- The Java-location gate **also covers `IgnoreJavaCompatibility`**, which is not a location. Inherited,
  and defensible: an instance pinned to a specific JVM is exactly the case where the user also wants to
  say "run it anyway". Java *arguments* have their own separate gate, because changing where Java lives
  is not the same decision as changing how it is invoked.

Two settings are **passthroughs** rather than overrides — `ConsoleMaxLines` and `ConsoleOverflowStop`
follow the global with no gate at all, because an instance cannot have its own console scrollback limit
and pretending otherwise would need a gate the UI has nowhere to show.

Carried over: `OverrideCommands` is registered with `OverrideLaunchCmd` as a synonym, since the key was
renamed and old files still carry the first; and a **negative `totalTimePlayed` is reset on open**,
repairing in place a value an older version could write that would otherwise render as nonsense
forever.

The default `MaxMemAlloc` is derived from the machine via `SysInfo.SuitableMaxMemory` rather than fixed
— 4 GiB on a 4 GiB laptop is a swap storm.

### Wave 6 (Minecraft) — the instance list

`Launch/InstanceList.cs` covers discovery, loading, grouping and `instgroups.json`. **NOT PORTED:** the
`QAbstractListModel` surface (rows, roles, drag and drop), the filesystem watcher, and the trash/undo
mechanism, which needs the UI to offer the undo. What is here is what the UI wave will build a model
over.

**What makes a directory an instance: an `instance.cfg` inside it.** Nothing else — a half-created
instance with no `mmc-pack.json` still shows up, because hiding it would leave the user unable to see
or delete it. **The folder name is the id**, which is why renaming an instance in the UI does not
rename its folder: the id has to stay stable or the group file and every reference to it break.

Two guards are worth knowing:

- **A symlink leading back into the instances folder is skipped.** Without that, an instance linked to
  its own parent is discovered twice under two ids, and the group file gains a phantom entry that can
  never be cleaned up.
- **`SaveGroupList` refuses to write before discovery has run.** The file is written as a *complete
  picture*, so saving it while the instance list is still empty would erase every user's grouping. The
  same reasoning drops entries for instances no longer on disk.

**A group is nothing but the set of instances naming it**, derived rather than stored, so one whose
last instance leaves stops existing. Upstream keeps a separate count alongside, and the two can
disagree.

**Every failure reading `instgroups.json` is silent and leaves the grouping empty** — missing,
unreadable, malformed or wrong-version all mean the user sees an ungrouped list rather than an error
they cannot act on. A malformed *individual* group is skipped and the rest still load.

> ⚠️ **`formatVersion` is written as a JSON STRING, not a number**, and read with Qt's
> `toVariant().toInt()`, which parses either. An earlier draft of this port read it strictly as a
> number — which refuses **every `instgroups.json` that exists**, silently ungrouping every user's
> whole library on first run. Caught by the tests asserting the literal file shape rather than only a
> round trip; a round-trip test would have passed happily, because the writer and the reader were
> wrong in the same direction.

### Wave 8 (Mod platforms) — the metadata index

`Minecraft/Mods/Packwiz.cs` is the on-disk half of wave 8, and satisfies the inherited
`Packwiz_test.cpp` against the Qt suite's own fixtures. **NOT PORTED:** the CurseForge and Modrinth API
clients that fill these files in.

**Why the index exists.** A jar on disk says what it *is*, but not where to look for a newer one. Every
mod the launcher downloaded gets a `.pw.toml` in the instance's `.index/` folder recording its
provider, project and file, and that is what makes "update this mod" possible at all.

**The format is packwiz's deliberately**, so an instance's index is readable by the packwiz CLI and by
other launchers that speak it. Consequences worth knowing:

- CurseForge is spelled **`curseforge`** in the file, never `flame` — the internal name differs from
  the on-disk one.
- The two providers identify a download differently: CurseForge by two **integers** (`file-id`,
  `project-id`), Modrinth by two **opaque strings** (`mod-id`, `version`). Only one pair is ever
  populated, and `IsValid` checks the right one.
- The launcher's own additions are prefixed `x-extremelauncher-`. packwiz ignores unknown keys, so they
  travel harmlessly in a shared index.
- The serializer is written **by hand** rather than through a TOML writer, so the key order and section
  layout match what packwiz itself produces — a diff against a packwiz-managed index should be empty
  rather than a reshuffle.

**Index lookups are case-insensitive**, because providers are not consistent about slug case and on
Linux the filesystem would otherwise treat `JEI.pw.toml` and `jei.pw.toml` as two different mods. A
write **renames** a differently-cased file to the normalized spelling, so the index converges rather
than accumulating both.

**Both `[download]` and `[update]` are required.** Without the first there is nothing to fetch; without
the second there is no way to look for a newer version, which is the only reason the file exists. A
half-filled entry is refused on read *and* on write — it would look like tracking that is not there,
and the launcher would offer to update a mod it cannot resolve.

### Wave 4 (Java) — complete apart from the UI model

- ~~**`JavaChecker`**~~ — done (`Java/JavaChecker.cs`). `JavaUtils.FindJavaPaths` deliberately does not
  validate its candidates, exactly as upstream does not; probing is this class's job.

  **The jar is checked in** at `src/ExtremeLauncher.Java/Resources/JavaCheck.jar` and embedded, rather
  than compiled during the build — see the README beside it. Building it would put a JDK on the
  critical path of every .NET build on every machine to produce 1 KiB of bytecode that changes
  approximately never. It is compiled `-source 8 -target 8` so it runs on the Java 8 that old modpacks
  need. The `.java` source stays shared with the Qt launcher, so there is one definition of what gets
  probed.

  Three deliberate departures, all because the upstream shape does not survive contact with an async
  model:

  - **A missing checker jar is reported rather than silently dropped.** Upstream logs and `return`s
    without emitting any signal at all, so its caller waits forever. Here it is an errored result.
  - **Both pipes are drained concurrently with the wait.** Upstream reads them from `readyRead` slots
    driven by the event loop; a naive `WaitForExit`-then-read would deadlock on a JVM that fills a
    pipe buffer, and this runs against candidates that may misbehave by definition.
  - **The 15-second kill is a linked `CancellationTokenSource`** rather than a `QTimer`. Upstream's
    comment on it is "NO MERCY. NO ABUSE.", and it is right to be firm — a candidate turned up by
    scanning the filesystem may be a broken install or a stub that blocks on a network mount.

  Carried over: the environment scrub (upstream's "dangerous java crap" — `_JAVA_OPTIONS` and
  `JAVA_TOOL_OPTIONS` are injected into *every* JVM on the machine, so a user with one set would have
  their real architecture reported through a filter they had forgotten about); the Bedrock Linux
  stdout-noise workaround for GH-4125; passing the memory flags so a 32-bit JVM asked for `-Xmx4096m`
  fails during the *check* rather than at launch; and treating an unrecognised `os.arch` as 32-bit,
  which is the safe direction because it caps the heap rather than failing a start.
- **`JavaInstallList`** — a `QAbstractListModel`. UI wave.
- ~~**`JavaMetadata`**~~ — done (`Java/JavaMetadata.cs`).
- ~~**`java/download/*`**~~ — done (`Java/JavaDownloadTasks.cs`): `ArchiveDownloadTask`,
  `ManifestDownloadTask` and `SymlinkTask`. What remains before `AutoInstallJava` can run is the
  *list* of available runtimes, which is a meta-index document (wave 5) rather than anything in this
  wave.

  **A LAYERING INVERSION HAD TO BE UNDONE FIRST.** Upstream's `java/JavaMetadata.cpp` includes
  `minecraft/ParseUtils.h` to read release timestamps — C++ headers tolerate that, C# projects do not.
  The S3 timestamp functions are pure string-to-date with no Minecraft in them, so they moved to
  `Core/S3Time.cs`; `ExtremeLauncher.Minecraft.ParseUtils` forwards to them, leaving every call site
  and the ported `ParseUtils_test.cpp` assertions untouched.

  **Both installers write files whose paths come out of downloaded data**, and upstream guards
  neither. The archive path inherits the traversal check now built into `Tar` and `MMCZip`; the
  manifest path — whose *keys* name the files — got its own, and it refuses before any download
  starts. Tested.

  Carried over deliberately: a checksum type that is not `"sha256"` is treated as **SHA-1** rather
  than rejected, so a future third algorithm would validate against the wrong hash and fail
  confusingly. Archive kind is decided by **extension**, not content, so a tarball served as
  `download.bin` is refused rather than sniffed. `SymlinkTask` searches for the macOS bundle at the
  root and **one level down only**, which is enough because the extractor has already stripped the
  archive's own top-level folder. And `SymlinkTask` creates **hard** links despite its name — a
  symlink on Windows needs elevation; the name is inherited.

  `JavaMetadata` equality is **version and name only**, so two builds of the same version for
  different operating systems compare equal. Worth knowing before using it to deduplicate a list that
  spans platforms.

### Resolved

- ~~`StringUtils::truncateUrlHumanFriendly`~~ — done; `NetRequest` was its only caller and now exists.
- ~~`Json::write(…, filename)` / `Json::requireDocument(filename)`~~ — done, now that `FileSystem.Write`
  provides the atomic temp-file swap. Exposed as `Json.Write` and `Json.RequireDocumentFromFile`; the
  latter is named rather than overloaded because the C++ distinguishes its two overloads by
  `QByteArray` vs `QString`, which collapses to an ambiguous `string` in C#.

### Dependencies taken

- **`System.IO.Hashing`** — CRC-32 for gzip trailer validation. See `GZip.HasValidTrailer` and the
  note below; without it, truncated streams pass silently.
- zlib, quazip, cmark, tomlplusplus have direct BCL or NuGet equivalents and are **not** needed as
  vendored submodules.

### Carried-over quirks (deliberate, tested)

These are upstream bugs kept bug-for-bug so behaviour does not shift under existing users. Each has a
test pinning it and a comment at the call site. Fix them as separate, deliberate changes.

- `SplitFirst(s, string)` with a missing separator returns the whole string as *both* halves, minus
  `separator.Length - 1` leading characters on the right. The `char` overload does not share the bug.
- `HumanReadableFileSize` always scales at least once (`0` renders as `"0.00 KiB"`), and its
  `decimalPoints` argument affects only the rollover threshold, never the printed precision.
- `NaturalCompare` skips whitespace while scanning but its ordinal fallback does not, so `"a b"` and
  `"ab"` are ordered by the space.
- `Version` comparison ignores everything from a `+appendix` section onward, making `1.0` equal to
  `1.0+build` despite differing serialized forms.
- `FS::getFilesystemTypeFuzzy` resolves a reported `"EXT4"` to `Ext`, not `Ext234`, because `Ext`
  precedes `Ext234` in the enum and the lookup returns the first *containment* match. The declaration
  order of `FilesystemTypeNames` is therefore load-bearing — do not sort it. The exact-match
  `GetFilesystemType` gets it right.
- `PathCombine` returns **forward** slashes on every platform (it ends in `cleanPath`), while
  `PathTruncate` returns **native** separators (it ends in a join on `QDir::separator()`). Both shapes
  are pinned by the ported tests. Do not "fix" one to match the other.
- **`Untar` had no path-traversal guard at all, and it unpacks downloaded archives.** A tar member
  named `../../../.ssh/authorized_keys` would have been written exactly there. This is the code path
  that installs Java runtimes fetched over the network, so a guard was **added** rather than ported —
  the same one that already exists in `MMCZip.ExtractSubDir` and `NativesExtractor`. A link whose
  target resolves outside the destination is refused too; it is the same escape spelled differently.
- **Upstream bug #9, in `Tar::extract`, fixed here.** The link target is read from offset **0** — the
  *name* field — instead of offset 157, the `linkname` field. Every link member therefore gets its own
  filename as its target, producing a link that points at itself. Not preserved: there is no behaviour
  worth being bug-compatible with, and Java runtimes on Linux do contain links.
- **Upstream bug #10, in `Tar::extract`, fixed here.** The octal-parsed mode is handed straight to
  `QFile::Permissions`, whose flags are laid out one **hex nibble** per class (`ReadUser` = `0x400`)
  rather than one octal digit — so mode `0755` arrives as `0x1ED` and is read as an unrelated set of
  bits. That is why the `| ReadUser | WriteUser` hack was needed to make anything work at all. .NET's
  `UnixFileMode` *is* the POSIX layout, so the value maps directly. This matters concretely:
  `bin/java` without its execute bit is a Java runtime that cannot be launched. The owner-read/write
  forcing is kept — an archive with a read-only file in it would otherwise leave the launcher unable to
  replace what it just wrote.
- **`Untar` reads a block at a time and upstream trusts a single `read`.** A `QIODevice` returns what
  was asked for; `GZipStream` routinely does not. The ported reader loops until the block is full,
  which is the difference between unpacking a real `.tar.gz` and corrupting every archive larger than
  the decompressor's internal buffer. Pinned by `ABlockSpanningAGzipReadBoundaryIsStillReadWhole`.
- **`IniSettingsObject`'s multi-path constructor was a fallback chain, and should be a MIGRATION.**
  Upstream always uses the FIRST path and copies an older config into place when it finds one further
  down the list, so the next run reads the new location and the old file stops being consulted. My
  earlier draft opened whichever path loaded first — which reads a user's old config forever and
  writes their changes back to it, so an upgrade never actually takes effect. It was also untested.
  Fixed and pinned, including that an existing config at the preferred path is never overwritten by an
  abandoned one at an older path.
- **`IniFile.ParseLegacyFormat` had two off-by-one bugs of my own**, found when `ModUtils.ReadForgeInfo`
  handed it an empty `forgeversion.properties`. The comment scan started at index **1**, so
  `IndexOf(char, 1)` on an empty line threw `ArgumentOutOfRangeException` — a crash in code that was
  already written and already tested. The same off-by-one meant a `#` at position 0 was never found,
  so a whole-line comment containing `=` was parsed as a setting. Both are fixed and pinned by
  regression tests naming the caller that found them.
- **`Commandline::splitArgs` is not POSIX splitting**, and the differences reach users. Quote
  characters are **consumed**, so `-Dfoo="a b"` yields `-Dfoo=a b` — which is what makes it usable for
  the JVM arguments box. A backslash escapes only **inside** quotes, so an unquoted `C:\Users\bob`
  survives but a quoted `"C:\Users\bob"` comes out as `C:Usersbob` — **quoting a Windows path to
  protect a space is exactly what destroys it**. Single quotes are not literal the way POSIX makes
  them. An unterminated quote is not an error. And an **empty argument cannot be expressed at all**,
  because the final append is guarded on a non-empty buffer, so `""` contributes nothing.
- **Jar-mod precedence, in `MMCZip::createModdedJar`.** Mods are merged in **reverse** list order and
  the **first** writer of a path wins, so the mod **last** in the list is the one whose classes the
  game actually runs. Both halves are needed to get the answer right, and neither is obvious from
  reading one of them. Reversing the loop or letting later writes win swaps which mod is in effect,
  and nothing about the resulting jar would look wrong — it is pinned by `TheLastModInTheListWins`.
- **Two "FIXME: buggy" paths in `createModdedJar` are reproduced rather than fixed.** A single-file jar
  mod registers its name *after* writing, so a collision produces two entries under one path; and the
  folder case filters collisions by comparing absolute filesystem paths against zip entry names, which
  can never match. Both are upstream's own annotations. Kept because jar-mod lists are hand-built and
  an existing pack may depend on whichever entry the JVM currently picks — fixing it would silently
  drop a file a pack expects. The folder case is additionally unreachable: nothing produces one.
- **Upstream bug #6, in `Parsers::parseMinecraftProfile`.** `currentCape` is assigned from an ACTIVE
  cape *before* that cape's `url` and `alias` are validated, so a cape missing either one leaves
  `currentCape` naming an entry that never enters the map. Reproduced deliberately: `AccountData`'s
  reader already drops an unknown `CurrentCape` on the way back in, so the damage is contained, and
  diverging here would change what gets written to disk.
- **The two profile parsers disagree with each other**, and both answers are on users' disks. From the
  profile endpoint, the skin variant is whatever Mojang sends (lower case, `"classic"`/`"slim"`) and
  capes are keyed by id. From the session server, the *default* variant is upper case
  (`"CLASSIC"`/`"SLIM"`) and the single cape is keyed by the literal string `"cape"`, because that
  endpoint returns no cape id. Anything comparing variants must fold case.
- **`parseMinecraftProfileMojang` leaves by a different door than its neighbours.** Every other parser
  treats a non-object body as an empty object and returns `false`; this one calls `requireObject`,
  which throws. A plain-text gateway error therefore raises where the others report failure.

### Behaviour gaps the BCL would have introduced (fixed, tested)

Places where the obvious .NET equivalent is **not** equivalent. Each was caught by a test.

- **`GZipStream` accepts truncated input.** zlib only reports success on `Z_STREAM_END`, so
  `GZip::unzip` returns false for a truncated or corrupt stream. `GZipStream` instead treats a
  short read as end-of-data and hands back whatever it inflated, with no exception. `GZip.cs` now
  validates the gzip trailer's CRC-32 and ISIZE explicitly to restore the stricter contract. This
  matters: it is the difference between rejecting a corrupt `level.dat` and silently loading half a
  world.
- **Tomlyn's `TomlTable` indexer THROWS on a missing key**, where `System.Text.Json`'s `JsonObject`
  returns null. Every lookup in the packwiz reader is on a key a malformed file may simply not have,
  so none of them can use the indexer — `Packwiz.SubTable` wraps `TryGetValue`. Six tests failed at
  once on this, all with `KeyNotFoundException`.
- **`JsonNode.Parse` rejects a UTF-8 BOM; `QJsonDocument::fromJson` skips it.** Real packs have one —
  the inherited `ResourcePackParse/another_test_folder/pack.mcmeta` fixture starts with `EF BB BF`,
  because it was written by an editor that adds one. Without tolerating it, every pack whose author
  used Notepad reads back as corrupt. `ResourcePackUtils.ParseManifest` strips it.
- **`DateTimeOffset.TryParse` is looser than `Qt::ISODate`.** It accepts `"08/16/2024"`, which Qt
  rejects. `Json.cs` uses `TryParseExact` against an explicit ISO format list.
- **`Convert.FromHexString` throws where `QByteArray::fromHex` skips.** Qt ignores non-hex characters
  and pads an odd leading nibble (`"abc"` → `0x0a 0xbc`). `Json.FromHexLenient` reproduces that.

### Bugs the rewrite deleted rather than ported

- **The `ConcurrentTask` stack overflow is gone by construction.** Upstream, `subTaskFinished()`
  re-invokes `executeNextSubTask()`, so a queue of synchronously-completing subtasks would recurse;
  the C++ only avoids it by bouncing every hop through `QMetaObject::invokeMethod(...,
  Qt::QueuedConnection)`, and `test_stackOverflowInConcurrentTask` exists to keep that hop honest.
  The worker-loop form is a `while` loop, and `await` on an already-completed Task resumes inline
  within the same state-machine invocation, so the queue drains in one stack frame regardless of
  length. The test is ported anyway, at 50,000 subtasks instead of 4,096.
- **Composite abort is now structural.** Upstream `ConcurrentTask::abort()` hand-iterates children,
  disconnects signals, and ANDs their return values. Cancelling the parent `CancellationToken`
  reaches every child automatically.
- **The bookkeeping is now actually thread-safe.** `ConcurrentTask.h` states "This is not
  thread-safe" and relies on every mutation being marshalled onto one Qt event loop. With real
  concurrency the shared queue/sets need a lock, and now have one.

### Wave 8 (Mod platforms) — started

`ExtremeLauncher.ModPlatform` is the new project. `ModIndex` comes first because everything else in
the wave parses into it: CurseForge and Modrinth describe the same world in different words, and one
shared vocabulary is what keeps a third provider to one parser rather than a change everywhere.

**Ids are strings, not `QVariant`.** Modrinth ids are strings (`P7dR8mSH`), CurseForge ids are
integers (`306612`), and upstream reaches for `QVariant` to hold either. Strings are the honest common
type — an integer id round-trips through one exactly, and every use is equality or interpolation into
a URL. The one place the difference is real, CurseForge wanting a JSON number, is handled where the
request is built.

**`Packwiz` moved here from `Minecraft/Mods/`,** where I had put it earlier. Upstream has it under
`modplatform/` and it needs `ResourceProvider` from `ModIndex` — a duplicate enum was the alternative,
which is how two spellings of "curseforge" eventually get written to disk.

**The two `…Loaded` flags on `IndexedPack` default opposite ways and that is deliberate.** Versions
always arrive eventually, so `false` means "still coming". Extra data does not exist at all for some
providers, so `true` means "nothing more is coming" — default it the other way and the UI waits on an
answer that never arrives. Pinned by a test, because it reads like a mistake.

#### murmur2 is CurseForge's fingerprint, and it is not stock MurmurHash2

Three deviations, each load-bearing: whitespace (tab, LF, CR, space) is stripped before hashing, so a
jar rebuilt with different line endings fingerprints the same; the seed is `1 ^ filtered_length`, which
is why the whole input must be measured before any of it is mixed, and why the input has to be
seekable; and the tail is mixed by the same routine as the body, selected by a remaining-length
counter. Get any one wrong and every lookup silently returns "unrecognised" rather than failing.

The result is formatted as a **decimal** number, not hex — `QString::number` upstream, and what the
fingerprint endpoint expects.

**How the vectors were produced, because it changes what they are worth.** Upstream has no test for
this hash, so there was nothing to inherit, and asserting the C# against itself would prove only
self-consistency — the exact failure mode that let four defects through the CLI work. So
`testdata/murmur2-vectors.txt` was generated by a **second transcription of the C++, written in Python
without reference to the C#**; the generator is kept beside the vectors as `murmur2-vectors.py` so the
claim can be re-checked. Two independent readings agreeing on 25 inputs — empty, every length modulo
four, whitespace-only, and cases past the read buffer — is evidence. It is **not** a fingerprint
CurseForge has confirmed; only a real jar checked against their API would be that.

`Hashing.AlgorithmFor` does not use the first entry of `ProviderCapabilities.HashTypes` for
CurseForge, and upstream is right not to: that list is what the API *reports* hashes in (sha1 first),
while identifying an unknown local file goes to the fingerprint endpoint, which only knows murmur2.
Modrinth has no such split and does take the first entry. Tested, because the asymmetry looks like a
bug.

`Md4` is ported into the enum and **refused at runtime** — the BCL has none, and neither provider asks
for one. Upstream lists it only because Qt happens to offer it. Refusing beats returning a plausible
hash computed with the wrong algorithm.

#### The provider API is an interface, and the callback structs are gone

Upstream passes a struct of `on_succeed` / `on_fail` / `on_abort` to every call, because a Qt task
cannot be awaited. Those three are exactly what awaiting already means: a return value, an exception,
and a cancellation token. Nothing is lost, and "the caller forgot to set `on_fail`" stops being
possible.

Upstream's unimplemented defaults log a TODO and return a **null task**, which every caller has to
remember to check; a forgotten check is a null dereference. Here the base class throws, so a provider
that cannot answer a question says so at the point it is asked.

`ModrinthApi` is almost entirely URL construction, so the builders are separated from the fetching and
tested directly, asserting **whole URLs** rather than fragments. That is not fussiness: a malformed
Modrinth facet does not return an error, it returns an empty result page — indistinguishable from
"nothing matches". There is no exception to assert on, so the request is the only checkable artifact,
and checking it in pieces would miss precisely the punctuation errors that break it.

Two behaviours worth naming:

- **A search whose loaders Modrinth does not support is refused, not broadened.** A user who asked for
  Cauldron mods and got a page of Fabric ones has no way to tell the filter was dropped.
- **Carried-over inconsistency, preserved and pinned.** The same empty loader set is dropped from a
  search but sent as `loaders=[""]` — a filter matching nothing — when listing versions. Upstream
  tests only whether the option is *present* there, where the search also tests it is non-empty. Which
  behaviour was intended is not recoverable from the code, so it is left as found with a test that
  names it. My first draft of the `SearchArgs` comment asserted a null-vs-empty rule that upstream does
  not actually hold to; that comment was wrong and is now corrected.

#### CurseForge: same interface, a genuinely different API

Every difference below is why the shared interface earns its keep, and each is pinned by a test:

- **Sorting is by number**, not name — which is why `SortingMethod` carries both.
- **One Minecraft version, not a list.** Upstream sends the first and drops the rest silently. There
  is nowhere better to put them, but the narrowing is real.
- **Listing a project's files takes a SINGLE loader** (`modLoaderType`) while searching takes an array
  (`modLoaderTypes`). With two loaders selected upstream omits the parameter rather than choosing —
  right, because guessing would hide files the caller asked to see.
- **Resource types are numeric class ids**, and an unrecognised type falls back to mods because
  upstream's `default:` shares a case with `MOD`.
- **Cauldron and LiteLoader are mappable but not searchable.** Both have ids, so a file on disk can
  report one; neither is offered as a filter, since CurseForge returns nothing for them.
- **Ids and murmur2 fingerprints go over the wire as JSON strings**, not numbers, even though
  CurseForge's own docs type them as integers. That corrects a claim I had written into `ModIndex`:
  I assumed there was a place the string/integer distinction had to be restored, and there is not.

**A divergence between the two providers, preserved.** Modrinth *refuses* a search whose loaders it
cannot filter on; CurseForge declares the same validator and never calls it, so the same search goes
out with `modLoaderTypes=[]`. Both are upstream's behaviour and a caller may depend on either.

#### The Modrinth parser, and what it decides silently

`ModrinthPackIndex` turns Modrinth's JSON into the shared vocabulary. The tests concentrate on the
paths that decide *what actually gets installed* — which file of a multi-file version, which hash it
is verified against, which versions are rejected — because those fail silently rather than loudly.

**An unusable version returns null, not a blank object.** Upstream returns a default-constructed
`IndexedVersion` and every caller tests `fileId.isValid()` — a heuristic, in upstream's own comment.
A nullable return says the same thing where the compiler can see it.

**Versions sort newest-first with a STABLE sort.** Dates are RFC 3339, which sorts chronologically as
text — that is why upstream compares the strings rather than parsing them. But `std::sort` is not
stable, so for a project that published several versions in the same second, "the newest version" is
a coin flip that can differ between runs. LINQ's ordering is stable, so the provider's order breaks
the tie. Deterministic, and it costs nothing.

- **Upstream bug #11, in `Modrinth::loadIndexedPackVersion`, reproduced rather than fixed.** Choosing
  a file because its name matched `preferred_file_name` sets `is_preferred = true` inside the
  selection loop — and the line after the loop overwrites it unconditionally with
  `primary || files.count() == 1`. The in-loop assignment is dead and the preference is lost. Left as
  found *deliberately*: every caller reads `is_preferred` as "the file Modrinth considers canonical",
  so making a name match set it would change what the flag means. Pinned by a test that states the
  behaviour is the bug.

Two smaller things left as found and tested: the file-selection loop stops one short of the end, so
the last file is the default by *falling out* rather than by being chosen (near-unreachable, since
Modrinth requires a primary in practice); and a version declaring no loaders passes the dependency
filter, which is correct — resource packs and data packs have none, and excluding them would make
every non-mod dependency unresolvable.

`LoadDependencyVersions` takes the loaders directly instead of a `MinecraftInstance`. Upstream reads
them off the instance and also reads the Minecraft version off it — into a variable it never uses.

#### `gameVersions` is three fields in a trench coat

The CurseForge parser's single most consequential line is the heuristic that unpacks one flat array:

```json
"gameVersions": ["1.20.1", "Forge", "Client", "1.20"]
```

Minecraft versions, loader names and sides, all mixed. CurseForge does not separate them, so the
reader tells them apart by shape: **a dot means a Minecraft version**, a match against a loader name
means a loader, and `client`/`server` are sides. Crude, and it holds — every Minecraft version has a
dot and no loader or side name does.

Getting it wrong does not throw. It offers Forge files to a Fabric instance. So the classification is
tested value by value rather than through one representative case.

Three details preserved from upstream and pinned:

- **Sides accumulate rather than overwrite.** A file listing both ends up `"both"`; the first seen
  takes the empty slot and a second, different one promotes it.
- **The version test is a separate `if`, not an `else`**, so a dotted string would be classified as
  both a version and a loader. Nothing in CurseForge's vocabulary currently is.
- **A snapshot name like `23w31a` has no dot and is therefore not recognised as a version.** Observed,
  not fixed — upstream drops it silently and CurseForge's mod listings use release versions.

Two more differences from the Modrinth parser, both real and both tested:

- **A missing download URL is normal here.** CurseForge omits it for projects whose authors forbid
  third-party downloads, so it is read with `Ensure` and the file stays a valid version. On Modrinth
  the same absence invalidates the version. What to do about it belongs to the installer.
- **The first listed hash wins, not the strongest.** The Modrinth parser walks its provider's
  preference order; this one takes the array's order, so a file listing md5 before sha1 is verified by
  md5. Upstream's behaviour, kept — reordering would change which hash a download is checked against.

**Carried-over hazard, pinned rather than fixed.** Upstream's hash-algorithm switch shares `default:`
with case 1, so an algorithm id CurseForge has not documented yet is reported as `"sha1"` — a hash
labelled with an algorithm that did not produce it, which fails verification with a message blaming
the file. Changing it would reject files that work today, and the alternative is a third value the
rest of the launcher has no handling for.

`LoadBody` takes the description rather than fetching it. Upstream calls the API from inside the
parser through a file-static `FlameAPI`, which makes parsing a network operation and the parser
untestable without one.

#### "What is this jar?" — the first real use of the fingerprint

`EnsureMetadata` recovers provenance for a mods folder someone copied in from elsewhere: hash each
file, ask the provider which release has that hash, ask which project that release belongs to, write
the answer beside the pack as packwiz metadata. **Neither API accepts a filename**, and that is the
point — names get renamed, versioned and suffixed; content does not.

**A pipeline, not a signal graph.** Upstream wires five tasks together with signals, holds a
`m_current_task` pointer so `abort()` has something to forward to, disconnects everything in `abort()`
so signals are not delivered to a dead object, and threads results through two member dictionaries.
All of that is machinery for *"do these four steps in order and let me cancel"* — four awaits and a
token. The steps and their order are upstream's; the plumbing is gone.

The provider calls sit behind an interface and the hash function is injected, so the tests exercise
the **flow**: what gets skipped, what gets looked up, how many requests that costs, and what lands on
disk. Upstream reaches for file-static `ModrinthAPI` / `FlameAPI` objects from inside the task, which
makes every step a network operation.

Details worth naming, each tested:

- **The local filename is recorded, never the provider's.** A jar the user renamed, or one CurseForge
  files under a different name, must be recorded as it sits on disk or the next scan sees a different
  mod. And a disabled mod is `foo.jar.disabled` on disk but recorded as `foo.jar` — otherwise enabling
  it renames the file and the metadata stops matching.
- **Invalidity is checked before folder-ness**, so an unreadable folder fails rather than silently
  succeeding.
- **Project ids are deduplicated before asking.** Twenty files from one project is one request.
- **The file's own side beats the project's**, since the file is the more specific statement.
- **`metadata:curseforge` mode is chosen by provider, not by whether a URL exists.** CurseForge omits
  download URLs for projects that forbid third-party downloads, so packwiz records "ask CurseForge at
  install time".
- **The same jar under two names identifies once and the duplicate is reported failed.** Upstream's
  `QHash` insert would overwrite it, leaving that file unaccounted for either way.

The metadata is written for real into a temp directory and read back **through the packwiz reader** —
asserting the object just built would prove nothing about the file that ends up beside the pack.

#### Upstream bug #12, and it is a security one: .mrpack path traversal

A `.mrpack` is an untrusted zip from the internet, and its `modrinth.index.json` **chooses where every
download lands**. Upstream's `parseManifest` reads `path` straight out of the JSON, normalises
backslashes, and hands it to `PathCombine` against the game directory. Nothing checks it. An entry of

```json
{ "path": "../../../../.bashrc", "downloads": ["http://attacker.invalid/x"] }
```

is written exactly there. No exploit chain is needed — the path is taken at face value.

This is the same class of hole as zip-slip, which the archive layer in this port already guards
(`MMCZip.ExtractSubDir`); a manifest that names its own destinations needs the identical check. So
`ValidateRelativePath` is **added here and has no upstream counterpart**: it rejects absolute paths in
every spelling (rooted, leading slash, drive-qualified) and resolves each path against a marker root to
catch anything the filesystem would treat as an escape, however it is written — `mods/../..`, trailing
dots, backslash escapes. It **rejects rather than sanitises**: a pack whose paths escape is malicious
or broken, and quietly rewriting one would install a pack that is not the one published. Nine tests
cover it, and I verified all nine fail with the guard removed rather than assuming they would.

The check runs where the path *enters* the launcher, not where it is eventually joined to a directory —
one place to audit instead of every call site.

#### Parsing a pack without a UI on screen

Upstream's `parseManifest` opens a modal dialog partway through, to ask which optional mods the user
wants — so the format cannot be read without a UI running. Here optional files come back in their own
list and the caller decides; `ApplyOptionalSelection` folds the answer back in. Everything in the
parser is a pure function of the file's bytes.

Optional files are a **separate list**, not a flag on one list, because a list you must remember to
filter is a list you will forget to filter. A declined optional mod is still installed with
`.disabled` appended — upstream's behaviour, and better than it looks: enabling it later is a rename
rather than a download.

`RemoveUnchanged` matches on **hash, not path**. A mod that moved folders between versions is the same
file and needs no fetching; a mod at the same path with different contents does. Path matching gets
both backwards. Whatever is left in the old manifest after the pairing is what this version dropped,
and it has to be deleted — two conflicting copies of one mod is a crash on startup, not untidiness.

I wrote that method badly the first time: a `Clear()` followed by a ternary testing `Count == 0` made
one branch dead and the other unreachable. It happened to produce the right files by accident. It is
now a one-for-one pairing that also preserves the pack's own ordering.

#### A CurseForge pack names ids, not URLs

`manifest.json` is a different shape from a `.mrpack` in the way that matters most. Where Modrinth
lists a download URL and a sha512 per file, CurseForge lists only:

```json
{ "projectID": 306612, "fileID": 3814740, "required": true }
```

Two consequences, neither of them incidental:

- **Nothing can be downloaded from the manifest alone.** Every entry has to be resolved through the
  API first, so importing a CurseForge pack needs an API key and network access before the first byte
  of any mod arrives — and a pack whose files were later deleted from CurseForge cannot be installed
  at all, even if those files exist on a mirror.
- **The manifest carries no integrity information.** There is no hash to verify a download against;
  the launcher trusts whatever the API returns. That is CurseForge's design rather than a gap in this
  port, but it is a real difference in the two formats' security properties and worth stating.

`FlamePackManifest.cs` is **Apache-2.0, not GPL-3.0-only** like the rest of the port — it carries
MultiMC's original header, because it ports a file predating the fork and the header travels with the
code.

Details preserved and tested: `required` is spelled the positive way round and defaults to true (a
pack that says nothing gets everything); files are keyed by file id, so a manifest naming one twice
collapses to a single entry; and the type check runs *before* anything else is read, since CurseForge
uses `manifest.json` for non-modpacks too and the remaining fields would parse happily against the
wrong document.

**The overrides folder name gets the same containment check as `.mrpack` paths.** It comes out of an
untrusted manifest and is used as a path inside the archive, so it is exactly the same exposure as
bug #12 above, and it is guarded the same way. Also not present upstream.

`SplitModloaderId` splits `"forge-47.2.0"` at the **first** hyphen, not the last: loader names contain
no hyphen and versions routinely do (`fabric-0.15.0-build.1`). Splitting from the wrong end yields a
loader named "fabric-0.15.0" that no metadata server has heard of.

#### Resolving a CurseForge pack, and the Modrinth fallback for blocked files

`FlameFileResolver` is what makes a manifest of bare ids installable: three requests in order, each
depending on the last — every file id at once, then anything CurseForge will not serve looked up on
**Modrinth** by sha1, then every project id for names and links.

**The middle step is the clever one, and it is upstream's idea.** CurseForge lets an author forbid
third-party downloads; such a file comes back with an empty URL, so the launcher is told what it needs
and not allowed to fetch it. But CurseForge still publishes the file's sha1 — so upstream asks Modrinth
whether it hosts a file with that hash. Same bytes, different host, and the pack installs without the
user hand-downloading a dozen jars. Only the URL is taken from the match; everything else stays
CurseForge's, because the pack is theirs.

Three properties of that fallback, each tested, because every one of them fails *quietly*:

- **A multi-loader Modrinth match is not trusted**, even though the sha1 agreed. A Modrinth release can
  cover several loaders where a CurseForge file is per-loader, so the match is not confidently the same
  artifact — and the cost of being wrong is installing the wrong jar with no error at all. Upstream's
  guard, kept.
- **Modrinth being unreachable does not fail the import.** The step is opportunistic; the files stay
  blocked, which is exactly where they were without it.
- **A file with no sha1 is never asked about**, because the hash is the entire question.

**One fix over upstream.** Attaching project details searches the file list for a matching project id
and stops at the first hit, so a pack shipping two files from one project leaves the second without a
name or a link — which is precisely what the blocked-mod dialog then shows the user. Mapping by id
instead gives all of them the project. Tested.

`IsBlocked` is computed from the state rather than stored: it is the single question the whole
resolution exists to answer, and a flag would be one more thing to keep in step.

#### Where a pack becomes an instance

Both importers end at the same place — a list of `(uid, version)` components for `mmc-pack.json` — but
they arrive very differently, and that difference is all of `PackComponents`.

**Modrinth names its loaders.** Its dependency block is already a map of loader to version, so the
conversion is a lookup table and nothing else can go wrong.

**CurseForge packs loader and version into one string.** `"forge-47.2.0"` has to be split, mapped to a
metadata uid, then cleaned up for the special cases CurseForge has accumulated. Each step can silently
produce a loader no metadata server has heard of, which surfaces much later as "component not found"
rather than here as "this pack is odd". So the uids are asserted literally rather than round-tripped.

Two carried-over hacks, both upstream's and both kept as narrow special cases:

- **NeoForge's 1.20.1 prefix.** For that one version CurseForge writes `neoforge-1.20.1-47.1.0` where
  everything else is `neoforge-20.4.190`. Upstream hardcodes the string and calls it "a mess for
  curseforge". Left hardcoded: NeoForge's scheme changed exactly once, and a rule general enough to
  cover both would also mangle a legitimate version starting with digits and a dot.
- **"Mysterious trailing dots"** in the Minecraft version, upstream's phrase. They come from
  hand-edited manifests, and `"1.20.1."` matches nothing on any metadata server — so the import fails
  at component resolution with nothing pointing at the cause.

`"recommended"` is passed through rather than resolved here: resolving it needs the metadata index, and
the caller filters the recommended build by Minecraft version **for Forge and NeoForge only**, since
Fabric and Quilt releases are not tied to one.

Both mappings are **pure functions of the parsed manifest**. Upstream builds a `MinecraftInstance` and
a `PackProfile` just to hold the answer, so the mapping cannot be exercised without a staging directory
on disk.

**Correction to my own earlier work.** I documented `FlamePack.GetPrimaryModloader` as implementing
"upstream's effective behaviour — first if none marked". That was wrong on both halves: upstream
ignores the `primary` flag entirely on import (it loops every entry, overwrites its working variables,
and never breaks, so the **last** recognised loader wins), and the flag is written on export and never
read back. The helper now prefers `primary` — the manifest's own explicit statement, which CurseForge's
exporter sets on exactly the intended loader — and falls back to **last** recognised, so it matches
upstream for every manifest that marks nothing and differs only where upstream was picking against a
stated preference. The test that asserted "first" now asserts "last" and says why.

#### Remembering what a pack put there

Both pack formats ship an overrides folder copied wholesale over the instance — configs, scripts,
resource packs. Those files land mixed in with the user's own, so on the next update the launcher has
no way to tell which of them it wrote, and every override the pack later drops would linger forever.

The answer is a plain list written beside the instance at import: one relative path per line. Update
reads it back and deletes those before copying the new set.

- **Upstream bug #13, in `Override::createOverrides`.** The relative path is derived by splitting the
  absolute path on the folder's *name* — `split(name).last()` — and dropping one leading character.
  That works until the name appears elsewhere in the path, which is not exotic: a staging directory
  under a pack called "overrides", or any user folder containing the word. Every recorded path is then
  truncated at the wrong point, so the record names files that do not exist, the next update deletes
  nothing, and the stale overrides stay forever. **Fixed** — computed as a real relative path, same
  output for the ordinary case and correct for the rest. Tested with the folder name appearing twice
  in the path.
- **Upstream's read loop appends before testing for the end**, so the list it returns always ends with
  an empty string — and every caller opens with a check for one. That check is the tell. Blank lines
  are dropped here instead.

`GetStalePaths` checks containment on every recorded path before returning it. The record is written
by this launcher, but it sits in the instance folder where anything could have edited it, and the
result of that call is a **delete list** — the one place a bad path does the most damage. A legitimate
entry alongside an escaping one still survives; only the escaping entry is dropped.

An overrides folder that does not exist still gets a record written, so a later read can tell "nothing
was written" from "this instance was never imported".

#### The other direction: an instance back out to a pack

`PackExport` writes both formats, and the same asymmetries show up mirrored. A `.mrpack` carries
everything needed to install it — URL, sha1, sha512 and size per file. A CurseForge manifest carries
only ids, so the pack it produces is smaller and useless without the API.

**The tests round-trip through the parsers in this wave rather than asserting the JSON.** A field
spelled wrong on the writing side produces a pack other launchers reject, and asserting the JSON I
meant to write cannot catch a name only the reader knows — the two halves would simply agree with each
other. Only fields no parser reads are asserted literally.

Three behaviours worth naming, each tested:

- **A disabled mod exports as optional, not dropped**, with its `.disabled` suffix removed — so the
  pack records that the author shipped it, and an importer that installs it writes a filename the
  launcher recognises. With optional support off, everything exports as required, suffix and all;
  the format has no way to say "shipped but off", so the choice is required or lost.
- **The side handling is asymmetric on purpose.** A client-only mod is marked server-unsupported, but
  a server-only mod is left installable on the client. Upstream's comment gives the reason: "a server
  side mod does not imply that the mod does not work on the client" — and a `.mrpack` entry marked
  server-only is *skipped* by client importers, so wrongly marking one silently drops a mod the pack
  needs.
- **CurseForge gets exactly one loader**, by a fixed priority (Quilt, Fabric, Forge, NeoForge). The
  format has room for a list but every consumer reads one, so an instance with two components has to
  lose one and a stable order at least makes which one predictable. The NeoForge 1.20.1 prefix is
  written here too, mirroring the import side — without it CurseForge's own client cannot install the
  result.

**A defect the round-trip found in my own earlier work.** `ModrinthPack.Parse` decoded the sha512 with
`Convert.FromHexString`, which throws a bare `FormatException` — not the `LauncherException` every
other parse failure in this codebase raises, and not what any caller catches. A hand-edited or hostile
pack would escape the parser as a raw BCL error rather than "this pack is malformed". Wrapped, with
the offending path named. Chasing it also surfaced that an **empty** hash decodes happily to an empty
array, so it is now refused explicitly: a file with nothing to verify against is worse than a missing
one, because it downloads, passes an empty check, and installs whatever the mirror served.

I only found it because the export fixture happened to use odd-length hex. Round-tripping earns its
keep.

#### Importing from the FTB app, where nothing is allowed to throw

A different problem from importing a pack file: there is no archive and no manifest to download. The
FTB app is already installed, its instances sitting in a folder, and this reads them where they are.

**So a directory that does not parse is not an error.** The caller points at the app's instances folder
and tries every subdirectory; anything that is not an FTB instance is skipped. That is why nothing in
`FtbAppImport` throws — a failed parse returns null, and one unreadable instance must not lose the
other twenty. Eight varieties of "not an instance" are covered: neither file, either file alone, either
file not JSON at all, and JSON of the wrong shape.

`instance.json` **alone** counts as not-an-instance, deliberately: that is an install the app never
finished, and importing one produces an instance with no loader and no explanation of why.

Both loaders and the game version come out marked **important**, unlike the pack importers where only
Minecraft is. Upstream's choice, and defensible — an FTB app instance is being *adopted* rather than
installed from a recipe, so the loader is as much the user's as the game version.

**Two meanings in one field, separated.** Upstream's target loop assigns the loader's version over the
pack's own `version`, and the install task reads it back from there — so the struct's own
`loaderVersion` member is dead code and the pack version is destroyed. It never surfaces because
nothing upstream displays the pack version. Not a behavioural bug, so not counted as one; kept as two
fields here because two meanings in one is one too many.

#### Technic packs do not say what they are

Modrinth and CurseForge packs both declare their loader and its version in a field named for the
purpose. A Technic pack ships a **vanilla-shaped version.json** and leaves the launcher to work it out
from the library list — which loader is present is inferred from Maven coordinates, and its version
from wherever that particular loader happens to encode it.

That inference is the whole difficulty, and every branch of it exists because some real pack broke the
previous rule:

- **Forge** puts its version in the coordinate: `net.minecraftforge:forge:<mc>-<forge>`, so it is
  everything after the first hyphen — **except on 1.7.10**, where the coordinate repeats the Minecraft
  version on the end (`1.7.10-10.13.4.1614-1.7.10`) and only the middle field is wanted.
- **NeoForge does not put its version in a coordinate at all.** Upstream reads the value following
  `--fml.neoForgeVersion` in the *game argument list* — a launcher inferring a component version from
  a command line it is about to build. Fragile, and there is nowhere else it appears.
  `--fml.forgeVersion` is accepted too, since NeoForge kept the older spelling for a while.
- **Everything else** goes through a prefix map, which — unlike the two above — does **not** stop the
  search: upstream's inner `break` exits only the map lookup, so a Forge or NeoForge library later in
  the list still wins. Preserved, and tested both ways round.

The coordinate shapes that motivated each rule are written into the tests as literals. There is
nothing to derive them from, so naming the inputs they were built for is the only way to keep them
honest.

`inheritsFrom` is the Minecraft version, with `fmlversion.properties` as the fallback for old FML
packs that predate the field. With neither, the pack is refused — there is no version to fetch.
Everything else degrades: a pack whose loader is unrecognised still installs as vanilla plus whatever
its jar mods provide.

Solder — Technic's pack host — splits a pack into a build list and a per-build mod list, so a pack with
a hundred builds does not send all of them at once. Its only integrity value is an **md5**, which is
weak and is what is on offer.

#### ATLauncher: the only pack format with an installer in it

The others list files. An ATLauncher pack describes a small install *program* — mods that are optional,
mods that are recommended, mods that depend on other mods, mods grouped so only one of a group may be
taken, mods hidden from the user entirely, and mods that are archives to be unpacked into a named
folder rather than dropped in as they are.

**Mod types are a delivery instruction, not a category.** `mods` means the mods folder, `jar` means
patch it into the game jar, `extract` means unpack it, `decomp` means unpack it and take one file out.
Nineteen of them, each a different thing to do with the download.

Three details that could not be derived from anything, all preserved:

- **`"depandency"` is accepted alongside `"dependency"`.** ATLauncher shipped the misspelling, packs
  were published with it, and it has to be honoured forever — removing it breaks real packs that are
  still installable today.
- **A mod named exactly "Minecraft Forge" with type `jar` is corrected to type `forge`.** Upstream
  explains why: Forge detection relies on the type, but there is little practical difference between
  `jar` and `forge` and some packs use the wrong one. Without the correction the pack installs with no
  Forge component and every mod fails to load. Matching on the exact name is crude and it is the only
  signal available — so the tests pin how *narrow* it is: not a different name, not a different type,
  not a different case.
- **Each loader hides its version under a different key** — Forge under `version`, Fabric under
  `loader` — so the type has to be read first and the version looked up by it.

`%s%` is ATLauncher's placeholder for a path separator, because the field is hand-written into JSON
where a backslash would need escaping and packs predate anyone being careful about that.

`EffectivelyHidden` is computed as hidden-or-library rather than read: a library is not marked hidden
but has no business in a mod chooser either, since the user cannot make a meaningful decision about
IC2's shim jar.

Dependency resolution follows names transitively and is guarded against cycles — nothing in the format
forbids one — and returns mods in the **pack's own order**, so the install list does not depend on
traversal order. A dependency naming a mod the pack no longer ships is ignored rather than fatal.

**That completes wave 8's importers**: Modrinth, CurseForge, FTB app, Technic and ATLauncher, plus both
exporters. What is unported there is the install orchestration that drives them, which belongs with the
instance layer.

### Wave 9 (Support tooling) — started

`TimeFormat` (renamed from `Time`, which collided with everything and said nothing) holds two
formatters that read alike and answer different questions. A **play time** is always exactly two
units, largest first, and never shows seconds once there are hours — nobody reads "14h 3min 22s" for
a play time. An **elapsed time** shows every non-zero unit down to milliseconds.

Two things worth recording:

- **Seconds are printed whenever anything larger registered**, so `1h 0s` is correct and `1h` is not.
  Upstream's condition for the seconds field is "did anything at all register", unlike the three
  fields above it, which are skipped when zero. My first test expectations here were wrong and the
  code was right — corrected, with the asymmetry written down.
- **No field padding.** Upstream writes through a `QTextStream` with `qSetFieldWidth(2)`, which in Qt
  persists until changed — so it pads the unit letters as well as the numbers, producing stray spaces.
  Nothing parses the string, so the padding is cosmetic and plainly not the intent. Dropped, and
  tested.
- The `precision` argument only decides **whether milliseconds appear at all**. Upstream passes it to
  a real-number precision setting that never applies, because the value printed is an integer count
  of milliseconds. Kept with that meaning rather than a pretence of decimal places that were never
  produced.

`InstanceCopyPrefs` turns "what to bring" into an **exclusion** pattern, because copying walks the
source and skips matches rather than assembling a list — so every box is inverted, and each unticked
one names the several paths that box actually covers. Resource packs is two directories (Minecraft
renamed them in 1.6), servers is three files including the backup Minecraft writes beside it, and
**mods also excludes config** — configs for mods that are not there produce an instance that fails
differently from a clean one, which is harder to diagnose than either.

The anchor is `[.]?minecraft/` with the dot **optional**, since the folder is `.minecraft` in some
layouts and `minecraft` in others, and it is re-emitted between every alternative. An unanchored
pattern would match a user's own `saves` folder anywhere in the tree. `IsExcluded` is added because
the pattern is the only artifact upstream produces, and a caller asking "is this file excluded" would
otherwise rebuild the regex and get the anchoring subtly wrong.

#### Five filters that differ only in what "nothing" means

`Filter` is a small family used to narrow version lists, and the only interesting thing about it is
the null case — which is different in each:

| Filter | Empty pattern | Empty value |
|---|---|---|
| `ExactFilter` | matches only an empty value | rejected |
| `ExactIfPresentFilter` | matches only an empty value | **accepted** |
| `ExactListFilter` | **accepts everything** | rejected |
| `ContainsFilter` | accepts everything, incidentally | matched normally |
| `RegexpFilter` | matches everything | matched normally |

Picking the wrong one shows a user an empty version list with no explanation, or a filter that
silently never filters. Neither errors.

**My first pass got two of the five backwards** — it gave `ExactFilter` the empty-value exemption that
actually belongs to `ExactIfPresentFilter` (a separate class I had missed), and gave `ContainsFilter` a
deliberate empty-pattern guard it does not have; upstream writes a bare `contains()` and the
everything-matches behaviour falls out of what a substring test does. Corrected against the header, and
now tested per filter per case rather than one representative each.

`ApplicationMessage` is what a second copy of the launcher says to the first — only one instance may
own the data directory, so a second one started with "launch this instance" hands over its arguments
and exits. Thin, and still a **trust boundary**: the bytes come from another process and the receiver
acts on them. Upstream's `toString()` on every field yields an empty string for anything that is not a
string, so a malformed message produces an **empty command, which does nothing**, rather than a
half-populated one that gets acted on anyway. Kept, because a sender on a different version may include
fields this one does not know and refusing the whole message over one would break the handover.

#### The filtered copy engine, and upstream bug #14

`FileCopy` is what duplicates an instance and what migrates a MultiMC or PolyMC data directory — both
need a subset rather than the whole tree. It walks the source itself rather than asking the filesystem
for a recursive copy, and upstream's comment says why: a recursive copy has nowhere to consult a
matcher. It is also the first consumer `IPathMatcher` has had in this port.

**The dry run is not a diagnostic — it is how the progress bar gets its denominator.** The caller runs
the whole traversal once to count and again to copy, so both passes must select exactly the same set.
Tested by running them back to back with a filter in place and comparing the counts; a mismatch leaves
the bar finishing at the wrong number.

- **Upstream bug #14, in `FS::copy::operator()`, FIXED here.** The return value is
  `err.value() == 0` — an error code **shared by every iteration and overwritten by each** — so it
  reports only whether the *last* file succeeded. A run where an early file fails and a later one
  succeeds returns true, and `DataMigrationTask` takes that as "everything copied" and reports success
  to the user. What makes it so quiet is that the failed paths are recorded in a list either way: the
  evidence is right there and nothing looks at it. Now returns whether *every* file was copied, with a
  test that fails one file, succeeds a later one, and asserts both the false return and that the later
  file really did copy.

Two behaviours preserved and written down because they surprise:

- **Empty directories are not copied.** The traversal yields files only; directories come into
  existence because a file inside them needed a parent. An instance with an empty `shaderpacks` folder
  arrives without one.
- **The whitelist flag inverts the same matcher.** Upstream expresses the decision as
  `matches(path) != whitelist`, so one matcher serves both readings — instance copying passes a
  blacklist of unticked boxes, data migration passes a whitelist of what is worth bringing.

Symlinks are always followed on Windows, which is upstream's choice and its comment: "the alternatives
are too messy". Creating one there needs a privilege the launcher may not have, and a broken link is
worse than a duplicated file.

#### Adopting a MultiMC or PolyMC data directory

This lineage has forked repeatedly and the data layout barely changed, so a new install offers to bring
the old one across.

**It is a whitelist, and that is the whole safety property.** The old directory holds whatever else its
owner put there, and anything imported lands somewhere this launcher will later write to — so only the
eleven paths it actually understands come over, and an unknown file is left behind. The list is
upstream's verbatim, including the entry for **this launcher's own config file**, whose upstream comment
reads "it is possible that we already used that directory before".

Most of the tests check what does *not* cross: notes, screenshots, a Java install, another launcher's
database. One pins that the prefixes are prefixes *with a trailing slash*, so `instances-backup/` is not
mistaken for `instances/` — a directory named to look like a whitelisted one would otherwise be
imported wholesale.

Two smaller things: the status line shortens a long path to the first 20 characters, an ellipsis and the
**last 29** — upstream's numbers, totalling exactly 50, with the tail favoured because the interesting
part of a path being copied is its filename. And the failure message now **names a few of the paths
that failed** rather than only saying "Some paths could not be copied!"; the list already exists, and a
locked file and a full disk look identical without it.

**`InstanceCopyTask` is deferred**, not forgotten: it needs a copy-on-write clone engine and a
link-tree engine (plus Windows privilege escalation for symlinks) that nothing has ported yet.

#### Copying an instance without copying the files, and upstream bug #15

A modpack instance is mostly a mods folder of jars that already exist elsewhere on the disk, so
duplicating one to try a change can cost gigabytes for nothing. `FileLink` links instead — at the price
that editing a linked file edits the original, which is why it is a choice the user makes.

**The depth limit is the design, not an optimisation.** At depth 0 the launcher links the mods *folder*
rather than each jar in it: one link instead of three hundred, and adding a mod to the copy adds it to
the original as well. At unlimited depth every file is linked individually, so the two instances share
file contents but have independent folders. Those are genuinely different products and the user picks
between them — which is why most of the tests check the **plan** rather than the linking. The plan is
also the whole reason this needs no separate dry run: the list is built first, so the total is known
before anything happens.

- **Upstream bug #15, in `FS::create_link`, FIXED here.** Requesting hard links forces recursion on —
  a directory cannot be hard linked — but it leaves the **depth limit applying**, so a truncated path
  plans a hard link *to a directory*, which cannot exist and fails every time. It is reachable straight
  from the UI: `InstanceCopyTask` passes depth 0 whenever "link recursively" is unticked, so a user who
  ticks "hard links" without it gets a copy that always fails. The limit is now ignored for hard links,
  since unlimited depth is the only way the request can succeed. Tested both as a plan and as a real
  run.

I found it by writing the wrong expectation: I had asserted that hard links ignore the depth limit,
which is what the *code* should do and not what upstream does. The failure was mine, and chasing it
surfaced the bug rather than just correcting a number.

**Fails fast, unlike `FileCopy`.** Upstream returns on the first link error, and that is the better
choice here: the usual failure is a missing privilege, which fails identically for every remaining
link, so continuing would produce hundreds of identical errors and a half-linked instance either way.

Symlink creation needs `SeCreateSymbolicLinkPrivilege` or developer mode on Windows. Upstream responds
by re-running itself elevated; that path is **not ported**, so the reason is surfaced instead — a caller
can suggest hard links, which need no privilege. The tests that really create symlinks are skippable
for the same reason: a suite that fails on an ordinary developer machine gets ignored rather than fixed.

#### Copy-on-write cloning — ported, and explicitly unverified

A reflink is the best of the three ways to duplicate an instance where it works: a real, independent
file that shares its blocks with the original until one of them is written. Editing the copy does not
touch the original (unlike a link) and it costs nothing until it does (unlike a copy).

**One idea, three syscalls** — `ioctl(FICLONE)` on Linux, `clonefile()` on macOS,
`FSCTL_DUPLICATE_EXTENTS_TO_FILE` on Windows ReFS — and nothing in .NET abstracts over them.

> ⚠️ **VERIFICATION STATUS.** The traversal, the same-filesystem precondition and the refusal are
> tested and run. **The clone syscalls are not.** This port has only ever executed on Windows with
> NTFS, where reflinks do not exist, so the Linux and macOS paths are unexercised code written from
> platform documentation. They must be checked on btrfs, XFS, APFS and ReFS before anyone relies on
> them. The test that would prove them is written and **skips on every machine this has run on** —
> which is why it is written as a skippable test rather than not written: when someone does run the
> suite on a copy-on-write filesystem, that test is what tells them whether the port is right. A green
> suite here means the *refusal* works, not that cloning does.

**Windows ReFS is deliberately not implemented**, rather than implemented untested:
`FSCTL_DUPLICATE_EXTENTS_TO_FILE` has alignment and file-size preconditions that are easy to get subtly
wrong, and a subtly wrong reflink produces a file that looks right and is not. `CanClone` already
returns false on NTFS, so nothing reaches that path on an ordinary Windows machine — and when something
does, it gets a message naming the alternative rather than an obscure failure.

Two design points worth keeping: the precondition is checked **once, up front** rather than per file,
since a reflink cannot cross filesystems and an unsuitable pair is unsuitable for all of them; and the
**dry run skips the precondition on purpose**, because a caller still choosing between clone, copy and
link needs the count either way.

#### Duplicating an instance — the thing the three engines were for

`InstanceCopyTask` picks between copy, clone and link, then does the several things that are only true
of the option chosen. **Linking is not just a cheaper copy**: two of its steps exist purely because a
linked instance shares files with its original.

- **Upstream bug #16, in `RegexpMatcher::caseSensitive`, FIXED — reversing my own earlier decision.**
  Upstream's `caseSensitive(true)` sets `CaseInsensitiveOption` and `caseSensitive(false)` sets
  `NoPatternOption`: the method does the exact opposite of its name. I first ported this as a
  *deliberately preserved quirk*, reasoning that call sites must have been written against the
  behaviour rather than the name — and wrote that reasoning into the file. It did not survive finding
  the only call site. `InstanceCopyTask` passes `false` while building a filter that excludes "saves"
  and "mods" from a copy, and on Windows and macOS those folders are as likely to be spelled "Saves".
  It wanted case-insensitive matching and got the opposite, so unticking "saves" failed to exclude
  them. Fixed, with the stale comment replaced rather than left contradicting the code.
- **A defect in the link path, whose cause is upstream's ordering.** Upstream links *everything* —
  saves included, since they are only filtered out when the user unticks them — and **then** copies
  the saves over the top, with a `FileCopy` whose `overwrite` defaults to false. The copy lands on
  files that already exist and fails, taking the whole instance copy with it. At depth 0 it is worse:
  the copy would be writing *through the link into the original instance*. Saves are excluded from the
  **link** here instead, which is the only ordering in which the stated intent works. Inferred from
  reading upstream rather than running it — but this port reproduced the failure exactly, and the two
  link tests went from skipping to passing once it was fixed.

`PathMatchers` had **no tests at all** until now: it was ported early, when nothing consumed it, and
bug #16 sat in it until `FileCopy`, `FileLink` and `InstanceCopyTask` finally gave it callers. Written
now, including that the trailing slash changes the *kind* of match — with one it is a prefix, without
one an exact match — which is what makes a whitelist safe to write as a flat list of strings.

Also preserved: a plain copy does **not** follow symlinks, so an instance whose mods folder already
links to a shared one stays that way instead of becoming a private duplicate; and `allowed_symlinks.txt`
is deleted first if it is itself a link, because after linking it may *be* the original's file and
appending would grant the original permissions it never asked for.

#### What kind of pack is this zip?

A user drops in a file and every format is just a zip with different things inside, so the launcher has
to work it out from the contents. Five formats, four marker files, and **an ordering that is
load-bearing**.

Upstream's comment gives the reason: *"prioritize modpack platforms that aren't searched for
recursively. Especially Flame has a very common filename for its manifest, which may appear inside
overrides"*. A CurseForge pack is identified by `manifest.json` — a name common enough that some
*other* pack's overrides folder may contain one. So:

1. **Root-only markers first** — `modrinth.index.json`, then `bin/modpack.jar` or `bin/version.json`.
   Distinctive enough that finding one anywhere but the root would be coincidence.
2. **Then the recursive search** for `instance.cfg` and `manifest.json`, which **skips `overrides/`**
   for the same reason: everything under it is the pack's payload, not its metadata.

Getting this wrong does not error — it imports a Modrinth pack as a CurseForge one and then fails much
later, with a message about a manifest that was never the pack's. So the tests build real zips and
check the collisions directly: a Modrinth pack shipping a `manifest.json` in its overrides, a Technic
pack shipping one in its config, an exported instance that also carries one.

Two things made explicit rather than left to accident. Upstream checks every file in a directory before
descending, so **shallower always wins** — which stops a nested example pack hijacking the real one —
and within one directory `instance.cfg` is found before `manifest.json` only because the entry list
happens to be ordered that way. Stated here as a rule: **an exported instance that also carries a
CurseForge manifest is still an instance**.

**Technic extracts into `minecraft/`**, alone among the five: its archive *is* the game directory
rather than an instance containing one.

A **wrapping folder is reported as the root** so extraction can strip it — a user re-zipping a pack
from their file manager gets one by default, and without stripping the instance lands a directory
deeper than anything expects. Note the Modrinth marker is root-only, so a *wrapped* `.mrpack` is not
recognised at all; that is upstream's behaviour and is tested as such rather than quietly improved.

#### Where every importer ends: staging and commit

Creating an instance — from a pack, a copy, or nothing — builds it in a staging directory and only then
moves it into place. Nothing half-built is ever visible in the instance list, and a failure leaves
nothing behind to clean up by hand.

**The staging directory lives inside the instances folder, not the system temp directory.** The commit
is a *move*, and a move across volumes is a copy — staging next to the destination keeps committing a
ten-gigabyte pack a rename rather than a second full write of everything just downloaded.

**The retry loop is not defensive programming.** Upstream names the cause exactly: *"the whole reason
why this uses an exponential backoff retry scheme is antivirus on Windows. Basically, it starts messing
things up while the launcher is extracting/creating instances and causes that horrible failure that is
NTFS to lock files in place because they are open."* A scanner opens the files an importer just wrote,
the move fails, and waiting is the only remedy — nothing to fix, nothing to ask the user.

Two asymmetries preserved, and both are the right way round:

- **A failed build destroys its staging directory**, because a half-extracted pack sitting in the
  instances folder would be picked up as an instance on the next scan.
- **A failed commit does not.** The instance is fully built and only the move was blocked; throwing it
  away would discard a completed download because a scanner was slow.

Testing the loop needed a commit that fails deterministically, which took two attempts to get right.
A *new* instance cannot be made to fail that way at all — `DirNameFromString` de-duplicates against
what is already there, so occupying the destination merely picks another name. An **override** has a
fixed destination, and pointing it at a path that is a file makes the merge fail every time.

**A test-harness bug worth recording**, because it is the kind that hides: my "blocked" description
started as a *property* returning a fresh object, so `new BuildTask(Blocked), Blocked` handed the build
task a different object than the staging task got. The build then wrote its file relative to an empty
staging path, and the first retry test passed for entirely the wrong reason — it was asserting on a
staging directory that was empty because of the harness, not because of the code. Caught when the
second test disagreed with the first.

#### Installing one mod, which is also how a mod is updated

An update is an install of a newer version plus the removal of the older file, and **the order those
happen in is the whole point** of `ResourceDownloadTask`.

**The old file is deleted last, and only on success.** Upstream explains the indirection that makes
this possible: *"so that we don't delete a mod before being sure it was downloaded successfully"*.
Update a mod, lose the network mid-download, and the instance must still have the version it started
with — getting this backwards costs the user a working mod in exchange for nothing. So the failure
cases carry more tests here than the happy one.

**And only if the name changed.** Many updates keep the filename, in which case the download has
already replaced the file and deleting "the old one" would delete the new one. The disabled form is
removed too, since a user may have turned the mod off before updating it.

Metadata is written **first**, because writing it is what reveals the previous version's filename — the
packwiz entry is keyed by slug, so reading before overwriting is the only record of what is already
installed.

> **A limitation worth stating plainly: a CurseForge download whose only hash is murmur2 is installed
> UNVERIFIED.** murmur2 is a file *fingerprint*, not a cryptographic digest, and no validator speaks
> it — upstream's switch silently falls through for exactly this reason. That is the format's
> limitation rather than a choice made here, and it is pinned by a test so nobody later assumes the
> download was checked. A malformed hash lands in the same place: it cannot verify anything, and
> refusing the install over it would block a mod the provider is willing to serve.

#### options.txt belongs to Minecraft, not to the launcher

`GameOptions` reads and writes Minecraft's own settings file so the launcher can show and edit them
without starting the game. The format is barely a format — one `key:value` per line, no escaping, no
sections, no comments — so everything here is about **not damaging it**. The file holds every keybind
and video setting a player has ever changed, Minecraft rewrites it on every exit, and most of the keys
in it are ones this launcher has never heard of.

Four rules follow from that, each tested by a round trip rather than by reading a value back:

- **Order is preserved** — a list, not a dictionary. Rewriting the file sorted would work perfectly and
  produce a diff against every backup the user has.
- **Unknown keys survive untouched**, because most of them are unknown.
- **Duplicate keys survive.** The format permits them and Minecraft reads the last; collapsing them
  would silently change which one wins.
- **A line with no colon is skipped**, not treated as an error. There is no such thing as an
  options.txt worth refusing to open.

Only the **first** colon separates, since keybinds (`key.keyboard.left.control`) and resource-pack
lists (JSON arrays) contain more.

**One fix over upstream.** Its reader chops only `'
'`, so a file saved with CRLF — by a user editing
in Notepad — leaves `'
'` on the end of every value. Nothing then matches `"true"` or `"fullscreen"`,
and the stray byte is written straight back on save. Stripped here, and the file is always written with
LF on every platform, which is what Minecraft itself writes and what keeps a file from gaining
line-ending churn just because the launcher opened it.

The `version` key is held apart from the rest and written back first, where Minecraft puts it — and an
old file that never had one does not gain one from being opened.

#### Hardcoded knowledge about Minecraft's past

`VersionFilterData` holds facts that are not derivable and are not written down anywhere else in the
project: which Maven coordinates count as LWJGL, the day Mojang started needing Java 8, the one version
whose Forge installer is known broken. It exists because the metadata does not say, and because these
facts stopped changing years ago. Transcribed exactly, **including the timezones** — upstream records
one date in +02:00 and three in UTC, and a boundary that moves by two hours moves which snapshot falls
on which side.

`RemoveLwjglFromPatch` strips the LWJGL libraries an old version document bundles among Minecraft's
own, because this launcher installs LWJGL as its own component so it can be upgraded independently —
most often for a native build on a platform the original never shipped for. Leaving both in place puts
two LWJGLs on the classpath, and which one wins is whichever the ordering happens to put first. Only
the plain library list is filtered, matching upstream: jar mods and maven files are left alone.

**I nearly invented behaviour and dressed it as a port.** I wrote a `GetRequiredJavaMajor()` around the
three Java boundary dates — and the giveaway was that its pre-Java-8 branch had nothing meaningful to
return, so both sides of the ternary said `8`. Checking properly showed **those three dates have no
consumer at all**, in upstream or here: the launcher takes a version's Java requirement from the
`javaVersion` block its metadata carries, and for older versions it does not guess. The function is
gone; the constants stay, carried for fidelity and because they are the only written record of those
boundaries, with a comment saying exactly that so nobody later mistakes them for live configuration.

#### The calls that change how a player looks

`SkinApi` covers `SkinUpload`, `CapeChange` and `SkinDelete` — Apache-2.0, like the files they came
from. Unlike everything else in this port these are **mutating requests against the user's own Mojang
account**: uploading a skin replaces the one they have, and removing a cape unequips it for every
client they use, not just this launcher.

So nothing is sent. The requests are **built separately from being sent** and the tests inspect them,
which is both the safe way to test this and the only way without an account. What matters is exactly
what would go on the wire: URL, verb, header, body.

Mojang chose the verbs well — PUT a cape to equip it, DELETE the same path to remove it, DELETE the
active skin to fall back to the default — so there is no "unset" body to get wrong. Removing a skin is
*not* uploading a blank one: the default is derived from the account UUID and no image reproduces it.

Two details preserved: the uploaded filename is always `skin.png` regardless of what the file is called
on disk (Mojang requires one and does not care which, so sending the user's own would leak a path
fragment for nothing), and the variant goes out **UPPERCASE**, unlike the same two words in the profile
responses this launcher parses elsewhere.

**One fix.** Upstream builds the cape body by interpolating the id into a JSON string literal —
`QString("{\"capeId\":\"%1\"}").arg(capeId)` — which is fine for the UUIDs Mojang issues and produces
an unparseable body the moment anything else reaches it. Serialised properly here.

Writing the test for that, I first asserted the exact escaped bytes and got it **wrong in the same
direction as the bug** — I wrote the expected string as though the quote were not escaped. It now
asserts the property that matters instead: the body parses and gives the id back. How `System.Text.Json`
spells the escape is the encoder's business, and pinning it would test the encoder rather than this
code.

#### NBT, written out rather than depended on

`level.dat` is NBT, and upstream vendors libnbt++ — which is not a thing that ports. Written out here
instead: thirteen tag types and no more, about 250 lines.

**The launcher reads four fields and has to write the whole file back.** Renaming a world means
changing one string inside a document full of tags this launcher has never heard of — player
inventories, datapack lists, boss bar state, whatever the next version adds. So the reader cannot skip
what it does not recognise; **every tag has to survive a round trip byte for byte**, or renaming a
world quietly destroys it.

Two places the format is not what it looks like, both pinned:

- **Strings are Java's "modified UTF-8", not UTF-8.** A NUL is written `C0 80` rather than `00`, and
  characters above the basic plane are written as their UTF-16 **surrogate pair**, each surrogate
  encoded separately — six bytes where UTF-8 uses four. An emoji in a world name goes through both
  rules, and emoji in world names are not rare. The test asserts the exact CESU-8 bytes for U+1F600
  *and* that they differ from what `Encoding.UTF8` produces.
- **Everything is big-endian**, including string length prefixes.

**How this is verified matters more than usual.** A reader and a writer tested against each other agree
by construction and prove nothing — the trap that has already caught several things in this port. So
the byte sequences in the tests are **hand-assembled from the NBT specification**: tag id, big-endian
length, payload, written out explicitly. The reader is checked against those bytes; the writer is
checked to produce them. Round-trip tests appear too, but only for the property the launcher depends on
— that a document survives unchanged — and never as the sole evidence a tag type works.

Length fields are validated before they size an allocation, since a `level.dat` is a file on disk that
anything could have written.

**A self-inflicted one worth recording:** I first wrote the encoder's boundary comparisons as raw
control characters embedded in the source (`' '`, `''`) rather than as escapes. They compiled and
behaved correctly, but made the file register as *binary* to `grep` and would not survive a re-encode.
Replaced with `' '` and `''`; the file is ASCII apart from comment prose.

#### The most irreplaceable thing the launcher touches

A mod can be re-downloaded and an instance rebuilt; a world someone has played for two years cannot.
`World` is shaped by that, and its tests lean on what happens when things go wrong rather than on
reading a good save.

**A world has two names and they disagree.** The FOLDER name is what the filesystem calls it; the
LEVEL name is what Minecraft shows, stored inside level.dat. They start the same and drift apart the
moment a world is renamed in-game or a folder is renamed by hand, so both are kept and neither is
derived from the other.

**Renaming writes level.dat and then moves the folder**, in that order. If the write fails there is
nothing to undo; if the move fails afterwards the world still opens and merely sits in a folder with
the old name. The other order risks a world whose folder says one thing and whose contents say
another.

**Invalid worlds are listed, not hidden.** A saves folder routinely holds half-copied worlds and
leftovers, and hiding one makes a save that failed to copy simply vanish from the launcher — which is
indistinguishable from having been deleted.

The seed moved between versions (`WorldGenSettings/seed`, falling back to `RandomSeed`) and both
layouts are still in the wild: a world created in 1.15 and played in 1.21 keeps the old one. The game
type keeps its **original number** alongside the interpretation, so a future fifth mode shows as "4"
rather than silently as "Survival".

**Two real defects, both found by tests rather than by reading:**

- **The NBT writer crashed on an empty compound.** A tag whose children were never touched has a null
  payload, which is a legitimately empty compound and not a broken one — the writer cast it and threw.
  Found because a test built a `level.dat` with no fields at all, which is exactly what a
  half-initialised world looks like.
- **`World.Path` went stale after a rename moved the folder.** Every later operation on that object —
  reload, a second rename, a **delete** — would have addressed a directory that was no longer there,
  and a delete addressing the wrong path is the worst outcome available in this file. Upstream
  refreshes its `QFileInfo` for the same reason; I had simply not.

The round-trip test that matters adds a `LongArray` tag the launcher has never heard of, renames the
world, and checks it comes back intact — because renaming rewrites the whole file, and that is the
property the hand-written NBT layer exists to provide.

### Wave 10 (UI) — started, and shaped so it can be tested

Avalonia restores and builds here, so the wave is viable. It starts with a decision about structure
rather than with a window.

**`ExtremeLauncher.ViewModels` has no Avalonia reference at all.** Upstream expresses the instance
list as a `QSortFilterProxyModel`: the ordering rules live inside a Qt class that cannot be constructed
without a `QApplication`, so they are only observable by *looking at a window*. Everything decidable
about a screen — what is shown, in what order, under which headings — moves into classes with no
toolkit dependency, so it runs in a test process with no display.

That is not a stylistic preference. It is the only way any of wave 10 gets tested on the machine this
port has been written on, and the views that follow are meant to be thin enough to check by reading.

`InstanceListViewModel` is the first of them, tested against **real instance folders read through the
ported `InstanceList`** rather than hand-built view models — so the tests exercise the whole stack
beneath the screen.

Behaviours worth naming:

- **Ungrouped instances come first**, under a heading whose name is empty — upstream's arrangement.
  Instances the user has not filed sit at the top rather than under a "Misc" heading invented for them.
- **Natural order by name**, so "Pack 2" precedes "Pack 10". This is the single most visible ordering
  rule in the launcher and the reason the `StringUtils` port exists.
- **Last-launch order is descending**, because the interesting end of "when did I last play this" is
  the recent one, with the name breaking ties among never-played instances.
- **Search matches the name only.** A user typing into a search box is naming what they want to play,
  not writing a query; matching the group too would surface every instance in a group whose name
  happens to contain the text.
- **"Nothing here" and "nothing matched" are different things to tell a user**, so they are different
  states.

**A wrong expectation of mine, pinned rather than smoothed over.** I asserted that "Sky Factory" sorts
before "Skyblock" — the ordinal answer, since U+0020 is below `'b'`. The code disagreed and the code
was right: ordering is **locale-aware**, a space is a variable-weight character under CLDR collation,
so "Sky Factory" sorts as though it were "SkyFactory" and lands *after*. That is faithful to Qt's
`localeAwareCompare`, which is what upstream sorts with. There is now a test named for the property so
the next person does not "fix" it back.

#### The window exists

`ExtremeLauncher.App` is the Avalonia shell: entry point, application class, and a main window bound to
`MainWindowViewModel`. It builds and **starts** — run for twelve seconds and killed by the timeout,
with no crash and no error output, having read real instances off disk through the ported stack.

> **What that does and does not establish.** It proves the app initialises, loads instances and binds
> without throwing. It does **not** prove the window looks right — there is no way to see it from
> here. Layout, spacing and theming are unverified, and the first person to run this on a desktop
> should expect to find them wrong somewhere.

**Compiled bindings are on** (`AvaloniaUseCompiledBindingsByDefault`), which is worth more than it
sounds: a binding to a property that does not exist becomes a **compile error** rather than a silent
blank in the UI at runtime. A clean build is therefore real evidence the view and the view models
agree, which is most of what can be checked without a display.

`MainWindow.axaml.cs` is deliberately empty of logic, and says so: if code starts accumulating there,
that is the signal it belongs in the view model where it can be tested.

**Enabled state is a property of the selection, expressed once.** Upstream re-derives it by hand
across `selectionBad()` and a scattering of `setEnabled()` calls that each have to remember the same
rules; here the buttons bind to `CanLaunch` and `CanEdit`, so a Launch button enabled for an instance
that cannot start is not a reachable state. Note `CanEdit` is true for an **unsupported** instance
while `CanLaunch` is not — deleting or renaming one does not require understanding it, and refusing
would leave a user unable to clear up an instance the launcher has already told them is broken.

**A dead control I caught and fixed.** The sort picker was a `ComboBox` with two hardcoded
`ComboBoxItem`s and `SelectedIndex="0"` — it looked right, selected nothing, and applied nothing. It
now binds to `SortOptions` and `SelectedSortOption` on the view model, with tests that the picker
offers every mode, that choosing one reorders the list, and that setting the mode in code moves the
picker. A hardcoded list in XAML is exactly how a control ends up decorative.

#### Pressing Play

The launch itself was already proven — the headless CLI starts a game with it. `LaunchCoordinator` is
the half that belongs to the screen: what the button may do, what the user is told while it happens,
and what happens when it fails.

**The launch sits behind an interface so this can be tested**, and not for purity: a real launch needs
a metadata server, a JVM and several hundred megabytes of downloads, none of which belong in a unit
test — while the decisions here are exactly the ones that break and are cheap to check.

Five of those decisions, each tested:

- **Every exception is caught, not a chosen few.** This is the last place a launch failure can be
  stopped before it reaches a UI thread and takes the window with it, and a launcher that *vanishes*
  when a mod pack is broken is worse than one that says what happened.
- **Cancelling is not a failure.** The user asked for it; reporting an error would be a lie.
- **The failure message is cleared when the NEXT launch starts, not when this one ends** — otherwise
  it disappears before anyone reads it.
- **One launch at a time.** A second would write to the same instance directory, extracting natives
  into a folder the first is reading. Refused rather than queued.
- **The log is capped at 5000 lines, dropping the oldest.** A modded game writes tens of thousands of
  lines to stdout, and a launcher that keeps every one in a UI collection is a memory leak with a
  progress bar on it. The test checks the *right end* is dropped.

**The command guards itself rather than trusting the button.** A command reachable by keyboard, by
double-click and by a menu item will eventually be invoked in a state the button was not in.

**A build with no launcher wired up says so.** A Play button that silently does nothing is the worst
of the three possible behaviours — worse than an error, and far worse than a disabled button — because
the user cannot tell it from a hang. `UnavailableLauncher` throws a message naming the situation, and
there is a test that it does.

The instance tiles are **Buttons rather than Borders with a pointer handler**: focusable, keyboard-
operable and announced by a screen reader, none of which a bare panel would be.

**Still to do here:** the app supplies `UnavailableLauncher`, so the window cannot actually start a
game yet. Wiring the real one means lifting the CLI's resolve-and-launch path into a service both
front-ends share, which is the next piece rather than a missing one.

### The headless CLI ran, and it found four defects nothing else would have

`ExtremeLauncher.Cli` exists to satisfy this document's own milestone: *a launcher that can start
Minecraft headlessly, before a month goes into UI*. It now resolves `net.minecraft 1.20.1` +
`org.lwjgl3 3.3.1` against the live Prism meta server, downloads all 65 libraries and the asset index,
probes the JVM with `JavaCheck.jar`, extracts natives and prints the full command line.

Getting there cost four fixes. **Every one of them passed the entire unit suite first**, which is the
part worth remembering:

1. **`Library.LimitedCopy` dropped `MojangDownloads`.** Upstream copies it (`Library.h:77`); I missed
   one line. Consequence: every library in every launch profile lost its `downloads` block and fell
   back to deriving a URL from its Maven coordinate. That is *correct for almost every library in
   existence*, which is exactly why it hid — it broke only the repackaged LWJGL natives, whose
   artifact id (`lwjgl-jemalloc-natives-windows-x86`) and real path (`lwjgl-jemalloc/…-natives-
   windows-x86.jar`) disagree. 21 of 65 downloads 404'd; the other 44 derived correctly by luck.
   There *was* a `LimitedCopy` test — it asserted five fields out of ten. It now asserts all of them.

2. **`FileSink` ran validators on a 304.** Upstream skips them, and says why in a comment
   (`FileSink.cpp:97`): validators are for bodies, not for "your data is still the same". Running the
   parse validator on an empty body fails the load. First run always worked; every run after the ETag
   was recorded failed.

3. **Two concurrent loads of one metadata document raced.** `ComponentUpdateTask` awaits components
   with `Task.WhenAll` — my design, replacing upstream's event-loop serialisation — and each component
   independently loads the *shared* index. Two loaders downloaded and renamed the same file at once;
   on Windows the rename fails outright. `MetaEntity` now holds a per-entity load gate, and the second
   loader finds the document current and skips the network. **This one is mine, not upstream's**: the
   concurrency is mine, so the mutual exclusion has to be too.

4. **The CLI never chdir'd to the data directory.** Library storage paths are relative and become
   absolute against the process directory, so `--dir` produced a classpath pointing at wherever the
   launcher was started from — while the jars themselves downloaded to the right place. Upstream does
   the same chdir for the same reason (`Application.cpp:349`); my port of the path logic was faithful,
   the CLI around it was not.

The pattern across all four: **unit tests confirm the code agrees with itself.** Three of these were
integration seams (copy → resolve, cache → validate, task → task) and the fourth was process state.
Each is now covered by a test that fails without its fix — verified by reverting each fix in turn, not
assumed.

Two smaller things the run surfaced, both now fixed: `--offline` was an option taking a value, so
`--offline --dry-run` launched as a player named `--dry-run` with no dry run (it is a flag now, with
`--name` for the player); and a failed download reported only "failed to validate", which reads the
same for a checksum mismatch, an unparseable body and an empty cache hit — it names the status now.

Still open from this exercise:

- Everything above was found on Windows. **The port has still never executed on Linux or macOS.**
- `meta.extremelauncher.net` does not resolve, so `BuildConfig.MetaUrl` points at an unregistered
  domain and the CLI needs `--meta` to do anything. The Qt launcher in this repo has the same problem.
- 1.20.1 asks for Java 17 and this machine has 21 and 25, so the launch was proved to the command line
  with `IgnoreJavaCompatibility`, not to a running game. That setting not reaching JVM *selection* was
  a real bug, fixed in wave 10 below, along with upstream bug #17 that the same run surfaced.


## Wave 10 — one launch path, two front-ends

The CLI proved the port by starting Minecraft headlessly, and every decision in that path lived inside
`Cli/Program.cs`. The window could not reach any of it, so `MainWindowViewModel` was constructed with
an `UnavailableLauncher` that refused with a message. Writing a second launch path for the window would
have produced two launchers agreeing until they did not, and the ways they would disagree are the ones
that matter: which JVM gets picked, whether an offline launch is allowed, what happens to a component
that will not resolve.

`Launch/LauncherService.cs` is now that path, and **both** front-ends call it. The CLI was rewritten to
go through it rather than keeping its own copy, so the two cannot drift. `LauncherPaths` moved from
`Cli` into `Launch` for the same reason — the directory layout is a compatibility surface with existing
Prism and MultiMC installs, and two front-ends deriving it separately is two chances to derive it
differently.

Progress leaves the service through an `ILaunchReporter` rather than `Console`: the CLI writes it to a
terminal, the window puts it in a status bar, and neither knows about the other.

### What the window needed that the CLI did not: threads

`LauncherTask` events fire on whatever thread did the work. The CLI does not care. The window does —
touching an `ObservableCollection` from a worker thread is a crash in Avalonia, exactly as it was in Qt
and for the same reason.

`ViewModels/BatchingLaunchReporter.cs` carries reports onto the UI thread, **batched, not posted per
event**. A modded game writes tens of thousands of lines to standard output during startup and a
download reports progress per chunk; one dispatcher post each would queue work faster than the UI
thread can retire it, and the window would stop repainting — the classic way a launcher appears to hang
at exactly the moment it is busiest. Workers append under a lock and a single drain is posted only if
one is not already pending, so bursts collapse into one update. Status and progress keep only their
latest value.

**The dispatcher is injected as an `Action<Action>` rather than calling `Dispatcher.UIThread`
directly.** Not for purity: this is threading code whose failure mode is a crash under load, and
against a real dispatcher it could only be tested by starting a UI. With the post as a parameter, every
rule — coalescing, ordering, the buffer cap, what a drain leaves behind, and eight workers logging at
once — is checked in plain unit tests. `App/AppLauncher.cs` is what is left over: `Task.Run` off the UI
thread, and the one line that supplies the real dispatcher.

Both guards were verified by breaking them: removing the coalescing check fails the burst test,
removing the buffer cap fails the bounded-buffer test.

### `MinecraftTarget::parse`, ported properly

`--server` parsing was never a port — it was a convenience I wrote for the CLI that split on the
**last** colon. Upstream `MinecraftTarget::parse` splits on **all** colons, gives up and takes the whole
string when there is more than one, and unwraps `[ipv6]:port` brackets. Three behaviours differed:

- `::1` became the host `":"` on port 1. It is now taken whole.
- `[::1]:25566` kept its brackets. They now come off.
- `example.com:notaport` kept the whole string as the address. Upstream keeps `example.com` and
  defaults the port — **and my own test had asserted my behaviour**, which is the recurring lesson
  again: a test written from the implementation confirms the implementation.

It now lives on `LaunchTarget.Parse`. The logic is MultiMC-origin and Apache-2.0; the surrounding file
stays GPL-3.0, which Apache-2.0 permits in that direction.

One quirk is preserved deliberately: the port is parsed as 32 bits and **truncated** into a `quint16`,
so `example.com:70000` is port 4464 rather than a rejected address. Upstream's own comment on this
function is that it "can't ever do any sort of validation and can accept total junk".

### `IgnoreJavaCompatibility` was only half honoured

Found by running the CLI, not by reading: the setting reached `VerifyJavaInstall` inside the pipeline,
but **JVM selection had already refused**, so a user who ticked "ignore compatibility" still could not
start the game. `FindUsableJavaAsync` now takes the flag and falls back to the first candidate when
nothing acceptable is found — unprobed, like the no-requirement branch, because the user asked for
compatibility to be ignored and refusing anyway answers a different question.

The candidate list is now injectable, which is what makes any of this testable without depending on
which JVMs happen to be installed on the machine.

> Note the gate that made this confusing: `IgnoreJavaCompatibility` is only read from the instance when
> `OverrideJavaLocation` is set. That is inherited and correctly ported — an instance pinned to a
> specific JVM is exactly the case where the user also wants to say "run it anyway".

#### Upstream bug #17, in `JavaChecker`, FIXED here — and it is user-visible

**Every Windows candidate is a `javaw.exe`**, in upstream and in this port, because that is what the
game should be started with: `javaw` has no console, so a played game does not sit behind a black
window. But `javaw` reports its own startup failures in a **modal message box** rather than on stderr,
and probing is precisely the business of running a JVM that might fail.

This surfaced as a screenshot of a dialog reading *"Could not find the main class. Program will exit."*
On a machine carrying Java 5, 6, 7, 8, 21 and 25, probing for a pack that wanted 17 walked the whole
list, and each ancient JRE that could not load the checker class put up that dialog and sat there until
the 15-second kill timer fired.

Measured, on the JRE 1.5 that produced it:

| probed with | result | time |
| --- | --- | --- |
| `javaw.exe` (upstream) | same text on the pipe, **plus a modal dialog**; had to be killed | at least 6 s, capped at 15 s |
| `java.exe` (now) | `UnsupportedClassVersionError` on stderr, exits by itself | **0.24 s** |

`JavaChecker.ProbeExecutable` swaps `javaw.exe` for the `java.exe` beside it, which exists in every JRE
and JDK layout and differs only in having a console. **The path reported stays the one that was asked
about** — this changes how a JVM is inspected, not what gets launched. If there is no `java.exe`, the
original is used rather than a guessed path.

### Checked and found faithful, so not changed

The dry run puts `lwjgl-*-natives-windows`, `-windows-x86` **and** `-windows-arm64` on the classpath at
once, which looks wrong. It is not: Prism's meta gives all three an `os.name: windows` rule, and
`RuntimeContext::classifierMatches` matches a bare OS name whenever the architecture is a legacy one
(x86 or x86_64). The C# `ClassifierMatches` is identical to upstream's. Left alone.

### Proved end to end after the extraction

`launch --dry-run` against a real 1.20.1 instance and `meta.prismlauncher.org`: 65 libraries
downloaded, asset index fetched, JVM probed and reported as *Java 21.0.9 (Oracle Corporation,
64-bit)*, and a complete command line printed ending in `minecraft-1.20.1-client.jar
net.minecraft.client.main.Main`. `list` and `info` go through the same service and still work.

### The game actually starts now — from both front-ends

Before this, nothing in this port had ever started Minecraft. Not the window, not even the CLI: the
launch was only ever "proved to the command line", which meant the arguments looked right.

**From the CLI**, with the pack's `IgnoreJavaCompatibility` doing its job: datafixer bootstrap, LWJGL
3.3.1, `Setting user: Tester`, texture atlases, `OpenAL initialized`, sound engine started. It exited
143 — SIGTERM, from the 75-second cap put on it, not a crash.

**From the window**, via `--launch`: a JVM under the launcher process and a game-written
`logs/latest.log` reading `Setting user: Player` — the app's offline name rather than the CLI's
"Tester", which is what makes it proof of the *window's* path rather than a stale log.

Two things in the game log that are **not** bugs in this port, checked rather than assumed:

- `Unrecognized user type: offline`. Upstream's `MinecraftAccount::typeString()` returns exactly
  `"offline"` for an offline account, so Prism sends the same thing and the game complains at it too.
- `Failed to verify authentication ... 401`. Expected for an offline session; the game carries on.

### The window could not reach a metadata server at all

Its first real launch failed with *"Could not resolve the version: Some component metadata load tasks
failed."* — shown correctly in the status bar, which was itself the proof that the whole UI chain
worked: resolve on a worker thread, failure caught, marshalled back, displayed.

The cause was that **the CLI only ever worked because every command in this document passes
`--meta`.** The window had no such option, so it used `BuildConfig.MetaUrl` —
`meta.extremelauncher.net`, the domain flagged near the top of this file as not resolving. An
unconfigured launcher therefore failed every resolve, and a user had no way to fix it.

Upstream's answer is a setting, now ported from the "Meta URL" block of `Application.cpp`:
`MetaURLOverride`, **validated at startup and reset when it is not an http(s) URL** rather than left to
fail on every download — a typo in a config file otherwise looks exactly like the network being down.
`GlobalSettings.ResolveMetaUrl` resolves it once per run instead of upstream's per-request
`BaseEntity::url()`, because the launch path threads a meta URL through several tasks and re-reading a
setting in each is how two of them come to disagree.

Both front-ends now resolve it the same way, so they talk to the same server when neither is told
otherwise. `--meta` is also accepted by the window — **not an upstream option**, added because there is
no API settings page yet and the built-in default cannot work on this fork.

### The command line, ported from `Application.cpp`

`Launch/LauncherArguments.cs`: `-d/--dir`, `-l/--launch`, `-s/--server`, `-w/--world`, `-a/--profile`,
`--alive`, `-I/--import`, `--show`, help and version, with upstream's short forms. This is a
compatibility surface — desktop entries, Steam shortcuts and batch files across this lineage invoke the
launcher as `--launch <id> --server <address>`, and every one of them breaks if an option is renamed.

Parsing lives in `Launch`, away from Avalonia, **so it can be tested**. Upstream's lives inside the
`Application` constructor, which is exactly why its "`--server` without `--launch`" check — pure logic,
one line — has never been exercised by anything but a user.

Two deliberate departures:

- **An unknown option is an error, not an import URL.** Upstream treats every positional argument as a
  URL to import, so a mistyped `--lanuch` lands in that bucket and the launcher starts normally as
  though nothing had been asked for.
- **Options this build cannot honour are named, not ignored.** `--profile`, `--show` and `--import`
  parse and then report themselves as ignored. An option accepted in silence is worse than one
  rejected: a script passing `--profile` would appear to work while the game started as somebody else.
  (They currently report into the launch log, which the next launch clears — a poor home for them until
  there is somewhere better to put a startup warning.)

`--alive` is implemented, writing `live.check` beside the data directory as upstream does, and removing
it rather than leaving a truncated file if the write fails.

### A GUI process has nowhere to print

Diagnosing the above was harder than it should have been: the window is a `WinExe`, so an unhandled
exception produces no stack trace anywhere a person can find — the launcher simply vanishes. Upstream
attaches the parent console for exactly this reason.

`Program.cs` now catches `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` and
anything escaping `AppMain`, and **appends** them to `crash.log` beside the executable. Appended, not
overwritten: a crash on startup followed by a crash on the retry would otherwise leave only the second,
and the first usually explains it. Beside the executable rather than in the data directory, because the
data directory may be the thing that could not be opened.

This is not scaffolding. "It disappeared and I don't know why" is the least actionable bug report a
launcher can produce, and it is what this build did before the handler existed.


### The progress plumbing, generalised

`BatchingLaunchReporter` became `BatchingProgressReporter`, and `ILaunchProgress` became
`IProgressSink`. Copying needed exactly what launching needed — a `LauncherTask` running off the UI
thread, firing status and progress on worker threads, feeding an object the window is bound to — and
the previous note in this file said the machinery was "generalisable, and was not generalised". This is
that, done rather than noted again.

`InstanceDuplication` is now its own `IProgressSink`, so the copy's progress goes through the same
coalescing path as a launch's. Its log lines are **dropped on purpose**: there is nowhere to show them
yet, and the interesting one — why it failed — comes back through `FailReason`.

Verified by breaking it: reporting straight onto the bound properties from the worker thread fails
`ProgressArrivesThroughThePostedDelegateOnly`.

> The test that catches it asserts only that **values** arrive through the post. The flow's own
> bookkeeping — clearing the bar before and after — runs on the calling thread, which is the UI thread
> in the app and is as safe as setting `IsBusy` there. My first version asserted on every change and
> failed against correct code.


### The Mods page

`ViewModels/ModsPageViewModel.cs`, ported from `ui/pages/instance/ModFolderPage.cpp` less its Qt model.
The most-used page in a Minecraft launcher, and the one whose defining property is that **nothing is
deferred**: enabling a mod renames a file the moment it is pressed, deleting one removes it
immediately. There is no Save button, because the filesystem is the document.

`HasUnsavedChanges` is therefore always false, and that is stated explicitly rather than left implied:
the instance window prompts about unsaved changes on close, and a page reporting dirtiness it could not
resolve would prompt every time and never stop.

The list is re-read after every change. A rename alters the file's name, its path and its sort position
at once — and mods also appear from outside the launcher, dropped into the folder by hand or written by
another launcher. `Refresh` is the same code path, which is why it is one line.

Rows show the mod's **own declared name and version** where it has them (`Sodium`, not
`sodium-fabric-0.5.3+mc1.20.1`) — the reason the page reads the jars at all rather than listing the
directory. A jar whose metadata will not parse still gets a row, named after its file: a mod the
launcher cannot read is exactly the one a user came looking for.

### `Resource::enable`, and the ".duplicate" quirk

`Resource.SetEnabled` and `Resource.Destroy` were never ported — `Resource.Enabled` was read-only,
derived from the filename. **The suffix is the state**: a mod is turned off by appending `.disabled`,
which is how the whole lineage does it and how the game sees it, since the loader simply does not
recognise the extension.

Two upstream behaviours kept deliberately:

- Disabling `sodium.jar` when `sodium.jar.disabled` already exists cannot use that name, and
  `FS::getUniqueResourceName` appends **`.duplicate`, not another `.disabled`** — giving
  `sodium.jar.duplicate`, which the loader also ignores, but for the different reason that it is not a
  jar. The test that matters is the negative: **neither file is lost**.
- `Resource::destroy` is `(attemptTrash && trash()) || deletePath()` — it **falls through to a
  permanent delete without asking**. Kept, and defensible for a mod in a way it would not be for an
  instance: a mod jar is a download, and what it takes with it is not somebody's world. It is still a
  real difference from how this port removes instances, where the second question is asked.

> A guard in `SetEnabled` refusing to enable something with no `.disabled` suffix is **deliberately
> untested**: that state cannot be reached through the constructor, since `Enabled` is derived from the
> suffix, so a test could only manufacture it by reflection and would pin the guard rather than any
> behaviour a caller can produce. My first draft had a test claiming to cover it that actually
> exercised the already-enabled no-op path; it is now a note instead of a misleading green tick.

#### A path-separator bug the tests caught

`SetEnabled` first ran its path through `GetFullPath`, which on Windows normalises separators to
backslashes — while everything else in this port produces Qt-style forward slashes via
`FileSystem.PathCombine`. So after a toggle the renamed resource no longer compared equal to the row
the page was holding, and **the mods page lost its selection on every toggle**. The next press would
then act on nothing.

Caught by `TheSelectionSurvivesAToggle`, which exists precisely because a rename is the one operation
where a selection held by path cannot survive by accident.


### Worlds, resource packs and shader packs

Six pages now: Version, Mods, Worlds, Resource packs, Shader packs, Notes.

**Resource and shader packs are the Mods page with a different folder.** Upstream has `ModFolderPage`,
`ResourcePackPage` and `ShaderPackPage` as separate classes over a shared `ExternalResourcesPage` —
the same arrangement with more of it written down twice. Here one class takes a `ResourceFolderKind`,
which picks the folder, the page title and the word used in messages. Shader packs are read as plain
resources rather than parsed: there is no manifest format for them, so the filename is all there is.

**Worlds is genuinely different, and the difference is the point.** A mod is a download and an instance
can be rebuilt; a world someone has played for two years cannot. So:

- Deleting **asks**, where the mods page does not.
- `World::destroy` trashes first and only deletes outright where there is no trash — upstream draws
  that distinction here and not for mods, which is exactly the right place to draw it.
- A save that will not parse is **listed under its folder name** rather than hidden. Hiding it is
  indistinguishable from having deleted it, and it is the row someone came here to find.
- Rename is disabled for such a save — the name lives *inside* `level.dat`, so renaming means rewriting
  a file the launcher has just failed to read — while Delete stays live.

#### Upstream bug #20, in `WorldListPage`, FIXED here

`worldChanged()` enables Remove whenever a **row** is selected — `index.isValid()` is about the model
index, not the world — while `World::destroy()` returns false when `!is_valid`. **So pressing Remove on
a corrupted save does nothing whatsoever, with no message**, and the user cannot clear it up from the
launcher at all. That is the main reason to open this page.

`World.Destroy` grew an `allowInvalid` parameter, defaulting to upstream's refusal; the worlds page
passes true and confirms first, so the outcome is a choice a person made rather than a button that
quietly does not work.

> **I nearly shipped a test that hid this.** The first version asserted
> `Directory.Exists(path) || page.Worlds.Count == 0` — true whatever happens, so it asserted nothing.
> Tightening it to the two real conditions is what surfaced the bug. My page comment had also claimed
> broken saves could be deleted while the code refused: an assertion about behaviour the code did not
> have, which is the third time that class of error has appeared in this port.

Two smaller slips fixed along the way: I renamed `WorldList.Scan` for no reason (reverted), and put
`World.Destroy` inside the static `WorldList` class rather than on `World` — the build accepted both.


### The Settings page, and a stale-key bug of my own

Seven pages now. `ViewModels/SettingsPageViewModel.cs`, from `ui/pages/instance/InstanceSettingsPage.cpp`.

**Every setting here is inherited until a box is ticked**, and that is the whole shape of the page. An
instance setting is a gate plus a value; while the gate is off, the value the launcher uses is the
global one. `OverrideSetting.Get()` already implemented that, and its `DefaultValue` is the global
value — which is what makes ticking a box harmless: it starts from what the instance was already
using, so a user who ticks "Memory" and saves has not silently changed how much memory the game gets.

An unticked group therefore shows the values the instance is **actually running with**, not blanks —
blanks would suggest it had no memory limit — and shows them as not editable, so nobody types into a
field the launcher will ignore.

The groups are **upstream's gates, not a layout choice**. `OverrideJavaLocation` governs the Java path
*and* `IgnoreJavaCompatibility`, which is not a location. Grouping them apart on screen would let
someone tick a box that silently does not apply.

#### The bug, and the test that nearly missed it

My first version skipped writing a group's values when its box was unticked, and I described that in a
comment as the thing that "leaves the instance inheriting again". It does not. **An instance that had
been overriding still has the old value in its `instance.cfg`** — unticking left a key that looks
exactly like an override behind.

Upstream gets this right: `applySettings` has an `else` branch calling `reset()` on every key in the
group. I had ported the `if` and not the `else`.

Nothing in the launcher noticed, because reading back through `OverrideSetting` returns the global
value either way. But **`instance.cfg` is a compatibility surface** — Prism and MultiMC read the same
file — and the next launcher to open it may well treat that key as an override.

> **The test that should have caught it did not, twice over.** It asserted on values read back through
> the settings object, which are correct either way. Removing the guard entirely left all fourteen
> tests green. Only an assertion on the **file contents** pinned it — and when I added that, it failed
> against my supposedly-correct implementation, which is how the missing `reset()` surfaced.
>
> The lesson generalises: for anything whose output is a file other software reads, assert on the file.
> Reading back through your own abstraction tests the abstraction.


### Servers, and the rest of the settings groups

Eight pages: Version, Mods, Worlds, Resource packs, Shader packs, Servers, Settings, Notes.

The Settings page gained Console, Miscellaneous, Native workarounds, Performance and Environment —
table entries each, now that the gates are registered and tested. The Linux-only ones (Feral GameMode,
MangoHud, discrete GPU, Zink) are shown **on every platform**, as upstream does: an instance configured
on Linux and opened on Windows must not silently lose its settings, and hiding the boxes is how that
happens.

`Minecraft/ServerList.cs` reads `servers.dat`, the game's multiplayer list. Three things about that
file are worth writing down:

- It is **uncompressed** NBT, unlike `level.dat`, which is gzipped. Reading it with the wrong one
  yields an empty list rather than an error — indistinguishable from "this instance has no servers".
  There is a test that feeds it a gzipped file for exactly that reason.
- `acceptTextures` has **three** states, not two. Absent means "ask me", which is what the game does
  for a server the user has never answered about; collapsing it to false would silently turn "ask" into
  "never", and since the game rewrites this file, the lie would stick.
- A corrupt file reads as **empty rather than failing**, which is upstream's behaviour and the right
  one: the servers page is where someone would go to fix a broken list, so it has to open.

The page is **read-only, deliberately**. `servers.dat` is a file the game owns and rewrites; reading it
is safe at any time, writing it while the game has it open loses whatever the game writes next. Add and
Remove are disabled with a tooltip saying so — the same choice as "Change version" on the version page,
and the opposite of the dead buttons this port shipped twice.

> **A caught-by-test slip:** the "corrupt file reads as empty" catch clause was written from memory —
> `IOException`, `InvalidDataException`, `FormatException` — and a file of plain text sailed straight
> through it. The NBT reader throws `Core.JsonException`, which is an odd name for an NBT failure but
> is this port's general parse error. Two tests failed on the first run and named it.


### Screenshots

Nine pages. `ViewModels/ScreenshotsPageViewModel.cs`, from `ui/pages/instance/ScreenshotsPage.cpp`.

**PNG only**, which is upstream's filter and the game's own output format. Showing other files would
invite renaming one, and the rename appends `.png` to whatever is typed — so a `.txt` in there would
quietly become a PNG that is not one.

Names are shown **without the extension**, as upstream does, which is why the rename has to add it
back: typing it would give `shot.png.png`, and typing nothing would give a file the page then filters
out of its own list.

Deleting **asks**, and trashes before deleting. A screenshot is not a world, but it is not a mod
either — it is the only copy of a moment somebody wanted to keep. Renaming onto an existing name is
**refused rather than overwriting**: `File.Move` would replace the other file without a word, and that
one is also the only copy of something.

**The Imgur upload is not ported.** Upstream can put screenshots on a third-party host, which means an
API key, an album API, and sending a user's images somewhere. Not a thing to reproduce by halves.


## Wave 12 — the instance window becomes the launch window

Ten pages, and the last one is Log. Following upstream's arrangement, which is not the obvious one:
`InstanceWindow` carries **Launch and Kill** buttons, and `runningStateChanged` **selects the log page
the moment a game starts**. Pressing Launch anywhere puts the output in front of you rather than
leaving it somewhere to be found.

Two upstream behaviours worth naming:

- **Closing the window does not stop the game.** Upstream's `closeEvent` saves the pages and accepts;
  nothing kills the process. The window is a console onto a running game, not the game itself, and
  someone tidying their desktop should not lose their session.
- **The log survives the game exiting.** The coordinator keeps its lines, and the page shows them —
  the ten seconds after a crash are exactly when the log is wanted.

### "One at a time" was scoped wrongly, and I wrote the reason down myself

`LaunchCoordinator` refuses a second launch while one is running, and its own comment gives the reason:
*"both would write to the same instance directory, and the second would be extracting natives into a
folder the first is reading."*

**That reasoning is about ONE instance.** With a single coordinator on the main window it applied to
the whole launcher, so starting a second, unrelated pack was refused for a conflict that could not
happen. Upstream has no such limit — `BaseInstance::isRunning()` is per instance.

`ViewModels/LaunchRegistry.cs` is one coordinator per instance, made on demand and kept after the game
exits (the log page wants them). The main window's status strip now **points at** the registry's
coordinator for whatever it last started, rather than owning one.

> **An intermediate version of that launched the game twice.** Keeping the old coordinator alongside
> the registry's and awaiting both started two JVMs writing into the same instance directory — the very
> thing the "one at a time" rule exists to prevent. Every existing test passed, because all of them
> only checked *that* a launch happened. `LaunchingStartsTheGameExactlyOnce` now checks how many.

### The UI tests were flaky, and it was the tests

Three symptoms, one cause. A different toolbar button failed on each run; the test passed alone every
time; and the real message — only visible in the `.trx` — was
`Unable to locate 'Avalonia.Platform.IFontManagerImpl'`, in whichever test happened to be running.

**The tests never closed their windows.** Headless Avalonia keeps one application and one dispatcher
for the whole assembly, so a window left showing gets re-laid-out by later tests, and once the platform
has been torn down, measuring text throws. Both test classes now track what they open and close it in
`Dispose`, and `UseHeadlessDrawing` is set explicitly because that is what supplies the stub font
manager.

> I first assumed parallelism and added `DisableTestParallelization`. It is right to have — one
> dispatcher, one application — but it was not the cause, and two runs after "fixing" it still failed.
> **Guessing at a flaky test's cause and watching it pass once is how a flaky test becomes permanent.**
> Reading the actual exception took one command and named it immediately.

Nondeterministic failures in a UI suite are worse than no UI suite: they teach you to re-run rather
than to look. Five consecutive clean runs before moving on.


### Other logs — the game's own files

Eleven pages. `ViewModels/OtherLogsPageViewModel.cs`, from `ui/pages/instance/OtherLogsPage.cpp`.

**The Log page shows this session; this one shows yesterday's.** It reads what the *game* wrote:
`logs/latest.log`, the rotated `.log.gz` files, and crash reports — which is what somebody actually has
when the launcher was closed and reopened between the crash and the question about it.

The four file patterns are upstream's exactly, and two of them are for versions almost nobody runs:

```
.*\.log(\.[0-9]*)?(\.gz)?    latest.log, 2024-01-01-1.log.gz, debug.log.3
crash-.*\.txt                 the game's crash reports
IDMap dump.*\.txt             a 1.6-era Forge artefact
ModLoader\.txt(\..*)?         older still
```

The last two are kept for the same reason `coremods` is scanned for mods: the people still running
those versions are exactly the ones who need a launcher that can see their files. They are pinned by
tests, because they are precisely what a tidy-up would delete.

**Rotated logs are gzipped**, which is the ordinary case for anything but the current session — a page
that could not decompress them would only ever show today's. Both of upstream's size caps are kept: 12
MiB on the file *before it is read at all*, and 50 MB again on the **decompressed** text, since a 12
MiB archive is a great deal more than 12 MiB of log.

Every failure returns **a sentence**, never an empty box: too big, not really gzipped, or locked by the
game writing to it right now. This is the page someone is looking at when something has already gone
wrong, and `""` is indistinguishable from an empty log.

> **Verified against real game output**, not a fixture: pointed at the instance this port launched
> earlier, it found `logs/latest.log` (4,224 bytes) and read back the Datafixer and LWJGL lines
> Minecraft itself wrote at 20:39.


### The launcher's own log, and a watcher

Both gaps from the previous section, closed.

`Core/LauncherLog.cs` — the launcher's view of a run now survives it. **Five files, rotated on every
start**, which is upstream's scheme exactly: `logs/ExtremeLauncher-0.log` is this run, `-4` is dropped
when the next start shifts everything along. Rotated at *start* rather than by size or date, because
the question is nearly always "what happened the last time I ran it", and a run is the unit people
think in.

- **Flushed every line.** A launcher that crashes with its last few lines still buffered has written a
  log that stops just before the interesting part — worse than no log, because it looks complete. The
  test reads the file back *without disposing*, which is the state a crash leaves behind.
- **Opened with `FileShare.Read`**, so the Other logs page can show it while it is being written. Its
  own name matches the `.*\.log` pattern, so it appears in that list.
- **An unwritable folder is not fatal**, unlike upstream, which refuses to start. The caller here may
  be the CLI pointed at somebody else's read-only install, and refusing to run is a worse answer than
  running without a log — so it says so on the console and carries on.
- Rotation walks **downwards** (3→4, then 2→3). The other direction overwrites each file with the one
  before it and leaves five copies of the newest run, which looks like a working rotation until
  somebody reads them.

Verified against the real binaries: two CLI runs produced `-0` and `-1` with the right contents, and
the failing one recorded `[ERROR] No instance called 'NoSuchInstance'.`

`Core/RecursiveFileWatcher.cs` — much shorter than upstream's, because `QFileSystemWatcher` does not
recurse and .NET's does. What does *not* disappear is the flood: a game writing to `latest.log`
produces a change event per buffer flush, and upstream has no debounce because Qt coalesces at the
signal level. Changes are gathered and one rescan is scheduled.

The pending flag is cleared **before** the event, so a change arriving during a rescan schedules
another rather than being swallowed — and the swallowed one would be the write that happened while the
page was reading it. A `FileSystemWatcher` buffer overflow is itself treated as a change, because it
means events were dropped and rescanning is the only safe response.

> **The delay is injected**, like the dispatcher in `BatchingProgressReporter`, and for a sharper
> reason: this port has already had a round of flaky UI tests, and a file-watcher test that sleeps and
> hopes is the canonical example. Every rule above is checked without touching a real timer. The one
> test that genuinely needs the OS waits five seconds and then **skips** — `FileSystemWatcher` latency
> is unbounded on a network share or a loaded machine, and a test that asserts on it is a flaky test
> waiting to happen.

The Other logs page follows its folder now, so a crash report appearing while the window is open shows
up without pressing Refresh. The watcher is disposed when the window closes: a launcher left open all
day would otherwise hold one OS handle per instance window ever opened.


### A launch in the log, and what running it taught

`Launch/LoggingLaunchReporter.cs` tees an `ILaunchReporter` into the launcher's own log on the way to
wherever it was already going. Both front-ends wrap their reporter in it, so a launch from the window
and a launch from the CLI leave the same record.

This is what makes the log worth having. Startup lines and a fatal error say *that* something went
wrong; the sequence — which components resolved, which JVM was chosen and why, what the pipeline was
doing when it stopped — is the part that answers the question.

**Progress is not logged**, deliberately: a download reports per chunk, and a file with forty thousand
lines of "37 of 65" in it is not a log, it is a denial-of-service on whoever opens it.

#### The dedup I wrote did nothing, and only a real run showed it

The first version dropped statuses equal to the previous one, with a comment claiming that stopped
`LauncherTask` flooding the file. **A real dry run of the 65-library instance produced 137 lines, 130
of them that flood.**

`ConcurrentTask` reports `"Executing 5 task(s) (3 out of 65 are done)"` — the progress is *inside* the
status text, so no two statuses are ever equal and an equality check drops nothing.

Statuses are now compared with **the digits removed**. That collapses the whole run to its first line,
while anything differing in a non-numeric way is still written. The rule generalises cleanly: a status
distinguished *only* by a number is progress wearing a different hat, and progress does not belong in
a log.

The same run now produces **8 lines**, and the last one is
`[ERROR] Could not determine the version of .../jre1.5.0/bin/javaw.exe.` — which is precisely the
detail a bug report needs.

> Twice in two sections now, the thing that found the bug was **running the real binary and reading the
> output**, not adding a test. The tests were written first and passed; they encoded the same wrong
> assumption the code did. A test proves the code does what you meant — it cannot tell you that what
> you meant was wrong about the data.


### Copying a log out

Both log pages have a Copy button now, behind an `IClipboard` the view models can use without knowing
about Avalonia — separate from `IUserPrompts`, because a page that can copy but must not delete should
not be handed the ability to delete. A build with no clipboard **says so** rather than doing nothing,
which is the failure this port keeps finding and which a Copy button hides especially well: the log is
still on screen, so nothing looks wrong until the paste.

## Wave 13 — the launcher can create an instance

`Launch/VanillaCreationTask.cs`, from `minecraft/VanillaInstanceCreationTask.cpp`.

**Until this existed the launcher could not make an instance at all.** Everything else assumed one was
already on disk — put there by Prism, by MultiMC, or by hand. A launcher that can only run instances
somebody else created is not a launcher, and this had been true for twelve waves.

It is far smaller than the gap suggests, because everything underneath was already ported: write an
`instance.cfg`, build a `PackProfile` pinning `net.minecraft`, and let `InstanceStagingTask` commit it.
Upstream's `createInstance()` is eighteen lines for the same reason.

- `InstanceType=OneSix` is what makes the launcher recognise the directory at all. Without it the
  instance lists as **unsupported**, which is a confusing way for creation to fail.
- Minecraft is written **important**, which is what makes it unremovable on the version page — the
  difference between an instance and a folder of components.
- A loader is **not** important: it is exactly the thing somebody may want to take back off.
- **Nothing is resolved at creation**, as upstream has it. Creation writes what was asked for and the
  first launch resolves it, which is what lets an instance be made with no network — and is why the
  caller must offer a list rather than a text box.

#### A real bug in `CommitStagedInstance`, and a test of mine that had been hiding it

The first test that created an instance **into a group** failed: the group came back empty.

`SaveGroupList` skips any id it does not know as an instance — upstream's `saveGroupList` has the same
guard — and upstream does `instanceSet.insert(instID)` in `commitStagedInstance` for exactly that
reason. My port did not. So a newly committed instance's group was written to `instgroups.json` and
immediately filtered back out, and **every new instance landed ungrouped**.

> **This also affected copying, and my copy test said it did not.**
> `TheCopyJoinsTheOriginalsGroup` asserted `list.GetInstanceGroup(id)` on the same in-memory list —
> and `SetInstanceGroup` updates the in-memory index happily while the *save* drops it. The copy landed
> ungrouped for anybody who restarted the launcher, and the test was green throughout.
>
> It now re-reads from disk, and I verified it fails with the fix removed. **Third time this exact trap
> has appeared** — settings, launcher log, now groups. The rule is written at the top of two test files
> already: for anything whose output is a file, assert on the file.


### ...and now the UI can too

`ViewModels/NewInstanceViewModel.cs`, `Launch/MetaVersionListSource.cs`, `App/NewInstanceWindow.axaml`
and a **New** button. The launcher can now be handed to somebody with an empty data directory.

**The version list is not garnish.** `VanillaCreationTask` deliberately does not validate the version —
upstream does not either, because creation must work offline and resolution happens at first launch.
That is only safe if the user *picked* from a list; a text box would turn a typo into an instance that
fails at launch with a metadata error nobody can act on.

- **Snapshots and old versions are hidden by default**, as upstream hides them. Against the live server
  that is **102 releases out of 1,013 versions** — the rest being 2011-era alphas and weekly snapshots.
  Somebody looking for "the latest one" should not have to find it among those.
- The **recommended** version is pre-selected, and the selection survives the filter being toggled.
- A failed fetch gives **a named error, not an empty list**: "no versions" reads as "Minecraft has no
  versions", which is never true.
- New is the **one button that works with nothing selected** — and with nothing in the list at all,
  which is exactly the state a new user is in. That makes it the one most likely to be wired like its
  neighbours by mistake, so there is a rendered-window test for precisely that.
- The dialog appears **before** the fetch completes, with the name box ready to type in; awaiting first
  would leave the user looking at nothing while the metadata server is contacted.

> Verified end to end against `meta.prismlauncher.org`: fetched 1,013 versions, filtered to 102
> releases, pre-selected the recommended one, created an instance, and read it back through
> `InstanceList` — name, supported flag and group all correct, with
> `"uid": "net.minecraft", "version": "26.2", "important": true` in its `mmc-pack.json`.

> A smaller slip caught while writing it: my first `NewInstanceViewModel` built its own `RuntimeContext`
> with `JavaArchitecture` hardcoded to `"64"`, which is wrong on a 32-bit machine. It now uses
> `LauncherService.CurrentRuntimeContext()` — the same derivation the launch path uses, rather than a
> second copy of a rule that would drift.


### Mod loaders, filtered two different ways

Fabric, Quilt, Forge and NeoForge can now be chosen when creating an instance. The filtering rule is
upstream's and is not a tidy one:

- **Forge and NeoForge** publish a build per Minecraft version and say so in `requires`, so their lists
  are filtered by it exactly. Offering a 1.20.1 Forge for a 1.16.5 instance makes an instance that
  cannot resolve.
- **Fabric and Quilt** declare nothing. Their metadata says which loader versions exist and nothing
  about which Minecraft versions they suit, and upstream's own comment is *"FIXME: dirty hack because
  the launcher is unaware of Fabric's dependencies"*: every loader version for Minecraft **1.14 and
  later**, and none before, 1.14 being where Fabric began.

Reproduced as it stands, hack and all — filtering Fabric by `requires` would show an empty list for
every Minecraft version, which is a worse answer than a slightly too generous one.

Verified against the live server, on 1.20.1: Fabric offered **251** versions unfiltered, Forge **132**
filtered by `requires`, NeoForge **61** and pre-selected a **47.x** build — which is genuinely its
1.20.1 line. Fabric on 1.12.2 offered none and said why.

#### A concurrency bug that only real data could produce

Setting `Loader` starts a fetch, so a combo box does not have to. Anything driving the view model
without one — a test, or the probe above — naturally awaits the same method. **Both ran**, and both
cleared and repopulated `LoaderVersions` while the other was walking it: a `NullReferenceException` out
of `FirstOrDefault`.

Every fixture-backed test passed, because a fake that returns `Task.FromResult` completes before the
second call begins. **Only the real metadata server was slow enough for the two to overlap.**

The fix is to share the in-flight task, so a second caller joins the first rather than starting
another. The regression test pins **`Assert.Same(first, second)`** rather than trying to reproduce the
timing — reproducing it would be the same flaky-test mistake this port already made once.

> Third finding in three sections that came from running the real thing rather than from a test. The
> pattern is consistent enough to be worth stating as a rule: **tests confirm the behaviour you thought
> of; running against real data is what tells you which behaviours exist.**


### The dialog, rendered

`NewInstanceWindowTests`. The rules were tested and verified against live metadata; what was
unverified was whether the window was connected to any of it.

The thing worth rendering is the loader row: a combo box that **appears and disappears** with the
loader choice. A version box shown for "None" would ask a question with no answers; one hidden for
Fabric would leave Create disabled with nothing on screen to explain why. Both are `IsVisible`
bindings, which is exactly what the build cannot check.

> **A ComboBox only realises its items when the dropdown opens**, so a closed one contains no TextBlock
> for its selection. My first assertion looked for the version's text in the window and failed against
> a perfectly correct dialog; asserting on `SelectedItem` is both meaningful and deterministic. The
> same shape of mistake as the layout-pass one earlier: the window was right and the test did not know
> how windows work.


## Wave 14 — importing a modpack

`Launch/ModrinthImportTask.cs`. Every piece of this was ported and tested in wave 8 — the manifest
parser, the component mapping, the path-traversal guard, the archive detector — and **none of it was
reachable**, because nothing turned a file on disk into an instance.

A `.mrpack` is a zip holding `modrinth.index.json`, `overrides/` and `client-overrides/`. **The mods
are not in it**: the manifest lists them with a URL and a sha512 and the launcher fetches them, which
is why a `.mrpack` is a few kilobytes and why importing one needs a network although creating a vanilla
instance does not.

- `overrides/` then `client-overrides/`, in that order, because the client ones are meant to win —
  which is what lets a pack ship a server config and a different client one under the same name.
- **Every download is hash-checked** against the manifest's sha512, so a file served wrongly fails the
  job rather than landing in somebody's mods folder. That matters more here than anywhere else: these
  URLs come from a pack file, which came from the internet.
- The path is validated **again** at download time, although `Parse` already refused a traversing one.
  Upstream bug #12 was a real path-traversal hole in exactly this format; the guard is a string
  comparison and this is the one place a destination path comes out of a downloaded file.
- Importing **without a client** writes the instance and skips the mods. Worth having as a state rather
  than a failure: the components and overrides are the part that cannot be recovered later.

It lives in `Launch`, not `ModPlatform`, because writing an instance needs `PackProfile` and
`ModPlatform` sits below `Meta`. That layering is right — parsing a pack format should not require the
component system — so the reference goes `Launch → ModPlatform`.

### Three test failures, three different lessons

**One was a dead guard.** I wrote `if (components.Count == 0)` for "the pack does not say which
Minecraft version it is for". `PackComponents.FromModrinth` **always** returns at least the Minecraft
component — with an empty version when the manifest named none — so the branch could never be taken,
and a pack with no dependencies imported happily into an instance pinned to Minecraft `""`. It checks
the version now. Same shape as the `? 8 : 8` and the `Clear()`-then-`Count == 0` this port has already
found: the condition looked like the question and was not it.

**One was a wrong assumption about the tests' own environment.** The traversal test asserted the
instances folder was empty afterwards; the staging root (`instances/.tmp`) legitimately lives there.
It would have read as the security guard failing when the guard worked perfectly.

**One was a test asserting the opposite of documented behaviour.** I wrote
`AWrappedArchiveIsStillFound` — and `PackTypeDetector` treats the Modrinth marker as **root-only**, so
a wrapped `.mrpack` is not recognised at all. That is upstream's behaviour, and *this file already said
so*, in wave 8: "the Modrinth marker is root-only, so a wrapped `.mrpack` is not recognised at all;
that is upstream's behaviour and is tested as such rather than quietly improved."

> I wrote a test contradicting a note I had written myself, in this document, about this exact
> function. Reading the notes is cheaper than re-deriving them, and I did not. The test now pins the
> real behaviour and names the divergence.


### Proved against a real pack, and reachable from the UI

**Fabulously Optimized**, the most-downloaded modpack on Modrinth, fetched from the real CDN as a
172 KB `.mrpack`:

| what | result |
| --- | --- |
| components | `net.minecraft 26.2` (important), `net.fabricmc.fabric-loader 0.19.3` (not) — exactly its manifest |
| overrides | 55 files extracted, configs and resource packs |
| downloads | **50 files, all sha512-validated, 42 MB, in 15.7 seconds** |
| read back | an ordinary instance: right name, right group, `IsSupported` true |

The hash validation is the part that most needed this. Every file passed its manifest sha512 — the job
fails the whole import otherwise — and that path had only ever been exercised against zips the tests
built themselves.

An **Import** button now reaches it, next to New, and needs no selection for the same reason New does
not: importing a pack is the other way somebody gets their first instance, and quite often the only way
they ever make one. The picker accepts `.mrpack` **and** `.zip`, because a Modrinth pack downloaded
through a browser is routinely saved as the latter and refusing to show it would look like the launcher
cannot read the file the user is holding. The format is decided by looking inside, not by the
extension.

A failed import is **shown in a dialog**, not swallowed: the wrong kind of pack, a pack with no
Minecraft version, a download that would not verify — each is a sentence worth reading. Both outcomes
also go to the launcher's log.


## Wave 15 — signing in

The whole MSA chain had been ported and tested for several waves and **nothing could reach it**. Every
launch built an offline session with a hardcoded name, so no instance could join an online-mode server
whatever was configured. Three things were missing between "the protocol works" and "a person is
signed in": somewhere to keep accounts, a way to keep them alive, and a way to see them.

### The credentials problem, and why it is solved at runtime

`BuildConfig` ships **no API keys** — README.md requires a fork to supply its own or blank them, and
shipping upstream's means accepting the Microsoft Identity Platform and CurseForge terms on the user's
behalf. That rule is right and this port keeps it. But empty forever means Microsoft sign-in can never
work, which is not a port of anything.

So they are read **at startup** rather than compiled in (`Core/BuildConfigOverrides.cs`): from
`EXTREMELAUNCHER_MSA_CLIENT_ID` and friends, or a `credentials.json` beside the executable or in the
data directory. Both are outside source control, which is the property that actually matters — a build
from this repository has no keys until somebody deliberately supplies them. A missing client id is
**said out loud in the log and on screen**, rather than leaving a disabled button nobody can explain.

`BuildConfig` became a `record` for this: filling in three fields by copying the other twenty by hand
is exactly the code that silently drops one when a field is added.

### `AccountList` — accounts.json

Upstream's is a `QAbstractListModel`; the model half is Qt table plumbing (four columns, header text, a
`PointerRole`) and Avalonia binds to objects instead, so what is ported is the behaviour:

- **replace-in-place on a duplicate profile id.** Signing in again to an account you already have is
  the ordinary way a stale token is renewed; appending would leave two rows for one person, one dead.
  It **inherits being the default**, or signing in again silently deselects you.
- **only a non-empty profile id dedupes.** An account mid-sign-in has none yet, so treating those as
  equal would make a second sign-in eat the first.
- `"active": true` is written **only on the default**, not `false` on the others — something reading
  the file may reasonably test for the key.
- an unknown `formatVersion` **renames the file to accounts-old.json** rather than deleting it. It
  holds refresh tokens somebody may want back.
- one broken entry does not lose the others; a corrupt file does not stop the launcher starting.

Two things this file gets that upstream also gets, and that are easy to leave out: it is written
**through a temp file** (upstream gets this from `QSaveFile`) because a crash mid-write costs every
signed-in account, and it is written **owner-only** on Unix, because it holds refresh tokens and a
shared machine has other users on it.

> Each of those three — the active flag, the replace-in-place, the temp file — was verified by
> **breaking it and watching exactly one test fail**.

### `MSARefreshStep` — what makes a saved account worth saving

An access token lasts about a day. Without the refresh exchange, `accounts.json` stores a refresh token
that nothing ever spends, and every session starts with the device-code dance again — which is
indistinguishable from not persisting accounts at all. **This step did not exist**; only the device-code
login had been ported.

`AuthFlow.CreateMsaFlow` already took the OAuth step as a parameter, so refresh is the same chain with
a different first step, and both share `MsaOAuth.StoreToken` rather than keeping two copies of "what a
successful sign-in leaves behind".

The distinction the step exists to get right is **which failure is which**, and upstream draws the
lines in a specific place worth copying exactly:

| state | when | why it matters |
| --- | --- | --- |
| `Disabled` | no refresh token, or one issued to a different client id — known before asking | retrying cannot help |
| `FailedHard` | the server returned an OAuth error (`invalid_grant`) | the token is spent |
| `FailedSoft` | a 500, or a body that made no sense | might work next time |
| `Offline` | could not reach Microsoft at all | says nothing about the account |

My first draft collapsed these into Disabled-or-Failed. Reading `MSAStep.cpp`'s silent branch showed
the real rule: the `error` signal is **always** `FAILED_HARD`, while `requestFailed` while silent is
`OFFLINE` for a network error and `FAILED_SOFT` otherwise. Getting it wrong is a bug in both
directions — one signs the user out over a blip, the other retries forever against a dead token.

One subtlety with real consequences: **a rotated refresh token replaces the old one, an absent one does
not clear it.** Microsoft may or may not return a new refresh token; blindly assigning wipes a good one
whenever the response omits it, and the account then survives exactly until the access token expires —
which looks like a random sign-out a day later.

### The launch path, at last

`LaunchRequest.Account` carries who is playing; `AccountRefresh.PrepareAsync` gets them ready first,
because the stored token is usually stale and launching with it unexamined means being bounced by the
first online-mode server.

Deliberate decisions there:

- it **will not prompt.** A sign-in needs a human to type a code into a browser, and starting that from
  inside a launch puts a dialog in front of somebody who pressed Play. It reports `NeedsSignIn` and
  lets the caller decide.
- a refresh that cannot be done **does not stop the launch.** Offline, or a lapsed sign-in, still
  leaves a perfectly good single-player game; the reason is reported and the launch continues. Refusing
  here would make a lapsed sign-in look like a broken launcher.
- **`Offline` is not `NeedsSignIn`.** The stored token may well be fine; what failed was the checking.
  Prompting for a sign-in that cannot possibly succeed is the wrong answer on a train.
- a token with **no recorded expiry is refreshed rather than trusted**, and one expiring within five
  minutes is refreshed early — a token that dies while the game is still loading is useless.

### The UI

`AccountsViewModel` plus `AccountsWindow`: the list, sign in with Microsoft, add an offline account,
set default, remove. Device code rather than an embedded browser — upstream's own fallback and the
better choice here: no web view, identical on all three platforms, and it never asks anyone to type a
Microsoft password into a window this program drew. **The code is shown in large monospace**, which is
the entire point of the flow; a spinner there would be useless.

The toolbar button shows **the account name**, not the word "Accounts". The most useful thing the
toolbar can say before somebody presses Launch is which account the game is about to use.

### A UI test caught what every unit test missed

`CanSetDefault` is bound to a button's `IsEnabled`. `MarkDefault` notified the *command* but never
raised `PropertyChanged` for the *property*, so after making an account the default the button stayed
live. **Every view-model test passed** — they read the property, which recomputes on every read, so the
assertion is true whether or not anything was ever announced. The headless window test found it in one
run.

> The lesson generalises: asserting a computed property's *value* proves nothing about whether the UI
> was ever told. The view-model test added alongside the fix subscribes to `PropertyChanged` instead.

Two smaller ones from the same wave. The end-to-end sign-in test drives the **entire** chain against a
stubbed Microsoft, and finding the right stub was itself the lesson: the Xbox parser rejects a response
missing `IssueInstant`/`NotAfter` with "response could not be understood", which reads like a launcher
bug rather than two absent fields. And `UuidFromUsername` — the offline-UUID endianness trap, carrying
one of the longest comments in this port — **had no test at all** until now; the expected value was
computed independently rather than read out of the implementation.

### Proved against the real service

The ported device-code step was run against **the live Microsoft endpoint** with the fork's own client
id from `CMakeLists.txt`, supplied by environment variable and never written to the repository:

```
client id configured: True
REAL CODE FROM MICROSOFT: 9EPJADAW at https://www.microsoft.com/link (expires in 900s)
```

A real user code and verification URI, and the polling loop then behaving correctly when nobody entered
it. **The sign-in was deliberately not completed** — that requires a human to authorize in a browser,
which is the user's action to take, not the launcher author's.

The application itself was then run three ways: with no credentials (logs the reason sign-in is
unavailable), with a `credentials.json` (reads it, enables the button), and with an existing
`accounts.json` (`Loaded 1 account(s); default is TestPlayer`) — the file left byte-identical
afterwards, with no stray temp file.


## Wave 16 — the settings nobody could edit, and the version nobody could change

Two windows that were missing rather than broken, and both had a fully ported, fully tested backend
sitting behind nothing.

### Global settings

**39 global settings were registered, defaulted and tested several waves ago and not one of them could
be edited.** The only way to change a global was to hand-write `extremelauncher.cfg` — while the
instance settings page had been cheerfully offering per-instance *overrides* of values the user had no
way to set in the first place.

`GlobalSettingsViewModel` reuses **the same editor view models the instance page uses**, minus the
override gates. That took one small refactor: `SettingViewModel.Read/Write` took an `InstanceSettings`
and only ever touched its inner `SettingsObject`, so widening it to the settings object made both pages
share one tested set of editors. Two implementations of "edit JvmArgs" is two chances for the global and
the per-instance copy to disagree about what a setting means — and this window is exactly where somebody
compares them.

The test that matters walks **the registered keys**, not a hand-written list:

> `EverySettingTheLauncherRegistersIsEditable` — a setting added later and forgotten in the UI fails the
> test instead of quietly having no editor.

What is deliberately absent, and why: **Language** (no translation machinery ported, so a picker would
list one language), **Proxy** (no proxy support in the network layer at the time, so the page would be
a form that did nothing — added in wave 33), **External tools** (MultiMC-era JProfiler/MCEdit
integration, unported), and **API keys**
— those are supplied at runtime now, and the entire point of that scheme is that the launcher never
writes one down.

### Change version, and add a loader

`CanChangeVersion` had been sitting on `ComponentViewModel` since wave 11 **with no command behind it**.
The Version page could remove a component and reorder the list, and there was no way to change what
version anything was, or to put Fabric on a vanilla instance. Both had to be done by hand-editing
`mmc-pack.json`.

One view model serves both, because they are the same question asked twice. Upstream has two dialogs —
`VersionSelectDialog` for "change this component" and `InstallLoaderDialog` for "add one of these" — and
the second is the first with a uid picker in front. Here that picker is a four-item menu on the button,
which is faster than a second window to choose between four things.

**A deliberate divergence.** Upstream's `isVersionChangeable` is `VersionList is { Versions.Count: > 0 }`
— the list must already be in memory, because its dialog is handed one. This port's dialog fetches the
list itself, so that gate would disable a button that works perfectly: an instance whose metadata index
has not been walked has no version list on any component, and Change version would be permanently dead.
A **custom** component is still refused — its local patch file *is* its definition, so "which published
version is this" has no answer.

### The real server found a bug that seventeen tests did not

Probing the live metadata server through the finished view model:

```
Minecraft  mc=-       default=  102  show-all= 1013
Forge      mc=1.20.1  default=    0  show-all=  132     <-- empty by default
NeoForge   mc=1.20.1  default=    0  show-all=   61     <-- empty by default
Fabric     mc=1.20.1  default=  251  show-all=  251
```

"Add loader → Forge" opened on **an empty list**, and the only way to see anything was to tick "show
snapshots and old versions" — for a component that has no snapshots. The reason, from the same probe:

```
net.minecraft               'snapshot'=744  'release'=102  'old_snapshot'=75  'old_alpha'=35 ...
net.minecraftforge          ''=5028
net.neoforged               ''=1729
net.fabricmc.fabric-loader  'release'=251
```

**Forge and NeoForge publish an empty `type` on every build.** The filter kept only `type == "release"`,
which hid all 5,028 of them. It now excludes the pre-release types — snapshot, old_snapshot, old_alpha,
old_beta, experiment — so anything else, including an empty type, shows.

> Every unit test passed, because every fixture in the file was one I wrote, and I had written
> `type: "release"` on all of them. This is the third time in this port that the rule has held:
> **tests confirm the behaviours you thought of; running against real data tells you which ones exist.**

After the fix, on the live server: Forge 1.20.1 → 132 builds and 1.19.2 → 116 (different sets, so the
`requires` filter genuinely works on real data), NeoForge → 61, Fabric → 251 and Quilt → 301 unfiltered
because they are version-independent and say so by declaring no Minecraft requirement — which matters
because `Require` is a **struct**, so "no requirement" is a default with an empty version, exactly the
shape a careless check treats as a mismatch.

### And the notification bug, for the third time

`CanPickVersion` and `CanAddLoader` are bound to `IsEnabled`, and `RaiseSelectionDependent` did not
announce them. Same class of bug as the accounts window an hour earlier: a computed property that
nothing raises leaves a button frozen in whatever state it was first evaluated in, and **every
value-based assertion passes anyway**, because reading the property recomputes it. It now gets a
subscribe-to-`PropertyChanged` test each time it appears.


## Wave 17 — downloading mods

**The thing a modded-Minecraft launcher is for**, and the largest single feature this port has been
missing. `ModPlatform` has had Modrinth and CurseForge search, project metadata, version listing and a
download task — all ported, all tested — since wave 8, and **nothing in the UI could reach any of it.**
The only way to add a mod was to drop a jar into the folder by hand.

What was missing turned out to be about twenty lines: `ResourceSearchSource` in the launch layer, which
calls the URL builder, then the parser, then hands back `IndexedPack`s. Everything underneath already
worked.

### A basket, not a picker

Nobody adds exactly one mod. Upstream's dialog lets you search, add several, keep searching, and
install the lot on OK — so `Selection` is a separate collection from `Results` rather than a
highlighted row. Two consequences that needed care:

- **a selected mod survives searching for something else.** This is the entire reason the basket
  exists; a selection cleared by the next search would mean installing mods one at a time.
- **a mod already in the basket comes back ticked when it reappears.** A new search builds *new* row
  objects for the same mods, so "is this one already added" is matched on the addon id. By reference it
  would show unticked and let the same mod be added twice.

A mod with **no build for this instance** cannot be added at all, and says so — the basket holds a
*file* to download, not a project, so an entry with no file would fail at install time with nothing to
point at.

### The stale-search problem

Typing "sodium" a letter at a time makes several requests, and the shorter query matches more, so its
answer can take longer and land **after** the longer one's — leaving the results for "sod" on screen
under the query "sodium". Each search carries a sequence number and a stale response is dropped on
arrival. The test releases the slow search only after the fast one has finished, which is the ordering
that actually causes the bug; deleting the guard fails exactly that test.

### Two filters that are easy to get backwards

- **A resource pack is not filtered by loader; a mod is.** Sending Fabric as a facet on a
  resource-pack search returns *nothing at all* on Modrinth — resource packs declare no loader, so the
  filter excludes every one of them.
- **A vanilla instance passes null, not `ModLoaderTypes.None`.** None is a filter matching nothing;
  null is no filter. An instance with no loader searching for mods should see what exists, not an empty
  list.

**Only mods get a metadata index.** A resource pack is installed as a bare file — upstream tests the
model type before building the task for exactly this reason, and passing an index directory would write
packwiz entries for something nothing tracks.

### CurseForge is refused up front

Modrinth is an open API and always available. CurseForge needs a key this build does not ship, so the
provider is refused **with a reason** the moment it is selected, rather than failing later on a 403 that
reads as "the search is broken".

### Proved end to end against the real services

Search, pick, download, and read back — nothing stubbed:

```
chose Sodium  0.5.13 for Fabric -> sodium-fabric-0.5.13+mc1.20.1.jar
chose Lithium 0.11.4 for Fabric -> lithium-fabric-mc1.20.1-0.11.4.jar

installed in 948 ms
mods/    sodium-fabric-0.5.13+mc1.20.1.jar      971,552 bytes   magic=PK
         lithium-fabric-mc1.20.1-0.11.4.jar     691,483 bytes   magic=PK
.index/  sodium.pw.toml, lithium.pw.toml
```

The packwiz metadata is complete and correct — name, filename, side, release type, loaders, Minecraft
versions, the CDN URL, a sha512, and the Modrinth project and version ids for updating later. And the
**mods page reads the folder back** and shows both, with names and versions parsed out of the jars:

```
Lithium   version=0.11.4          enabled=True
Sodium    version=0.5.13+mc1.20.1 enabled=True
```

That is the whole loop: a search against a live API, real files on disk, and the launcher's own page
recognising what it just installed.

Downloads run **one at a time** rather than all at once. Ten mods is ten small files from one CDN and
the wall-clock difference is seconds; what serial buys is a failure that names the mod it happened on,
and a partial install where everything before the failure is complete and usable. One mod failing does
not abandon the other nine — the failures are collected and shown together at the end.


## Wave 18 — instance icons

Every instance has carried an `iconKey` in its `instance.cfg` **since the first wave** — the vanilla
creator writes one, the Modrinth importer writes one, `InstanceSettings.IconKey` reads and writes it —
and nothing had ever read one back or changed it. Every instance in this launcher looked identical.

Three pieces: `Core/IconUtils.cs` (which files count), `Launch/IconList.cs` (what exists and where it
comes from), and the picker.

### Two sources, and the differences matter at every turn

| | |
| --- | --- |
| **built-in** | shipped with the launcher, keyed by name. Cannot be removed or overwritten. |
| **user** | files in `<data>/icons`. Added by dropping a file in, removable, and they **win** on a key clash. |

That last rule is upstream's, and it is the only way somebody can replace a built-in they dislike:
drop a file with the same name into the folder. Listing both would show the same key twice and the UI's
choice between them would be arbitrary.

Two more that are easy to get wrong:

- **A clashing import is suffixed, not overwritten.** Importing `creeper.png` from two different
  folders is a thing people do, and silently replacing the first is how somebody loses an icon they
  were using — every instance keyed to it would change picture at once, with nothing explaining why.
- **An unknown key resolves to the default, not to nothing.** An instance whose icon file was deleted,
  or one copied from a machine that had it, must still draw something. A tile with no picture reads as
  a broken instance rather than a missing image.

`IconList` **does not load images**. Decoding a PNG is a UI toolkit's job, so it resolves a key to a
source and says whether this build can draw it; that keeps all the file-shuffling testable without a
rendering stack, which is the part that actually has rules.

### The built-in set, and the SVG question

Upstream ships two icon sets: a legacy raster one and a modern scalable one. **The modern icons are
SVG-only**, and Avalonia cannot draw an SVG without another dependency — so the 27 PNGs come across
(from upstream's own resources, same licence) and the SVG set does not. 239 KB for a complete,
recognisable set, and no new package.

An SVG in the user's folder is still **listed, and marked unrenderable**. Hiding it would make the
launcher disagree with the folder it is showing — an icons folder shared with an upstream install has
them — so the tile says "cannot preview" rather than being a silently blank square.

The built-in keys are **discovered from the embedded assets**, not listed in code: adding a PNG to
`Assets/icons` is the whole job of adding a built-in icon, and a hand-written list is a second place to
forget.

### A test that passed for the wrong reason

My first `InstanceIcons` tests asserted on decoded bitmaps — `Load(...)` returns non-null, pixel size
is 1×1. They passed. Then the "a corrupt image returns null" test **also** reported a valid 1×1 bitmap,
which is how I found out that **the headless platform fakes the image decoder**: `new Bitmap(anything)`
returns a stub and never throws.

So every bitmap assertion in that file was proving the same thing for a real PNG and for a text file.
They now assert on the **resource stream** instead — that it exists, and that its bytes start with a
PNG signature — which is real, because `AssetLoader` is not stubbed, and which is exactly the half a
build-file mistake breaks. Pointing the csproj glob at `c*.png` fails three of them; the bitmap
versions had noticed nothing.

> The general lesson, and it is not the first time in this port: **a test that cannot fail is worse
> than no test**, because it also stops you looking. What made this one findable was writing the
> negative case — the corrupt file — and being surprised that it passed.

The catch around the decoder stays regardless: on a real platform a truncated PNG throws, and one bad
file in an icons folder must not take the window down.

### Wired through

The instance tiles draw their icons, an **Icon** button on the toolbar opens the picker, and the chosen
key is **written to `instance.cfg` and the list re-read** rather than patched in place — patching would
show a new icon for an instance whose file still said otherwise, and the next restart would silently
put the old one back.


## Wave 19 — getting an instance back out again

The launcher could import a modpack from wave 14 and had no way to produce one. An instance somebody
spent an evening configuring could be moved to another machine only by copying the folder by hand and
hoping.

### Two exports that are not the same thing

Upstream gives them separate dialogs, and the distinction is real:

| | | |
| --- | --- | --- |
| **instance zip** | a **backup** | everything, worlds and screenshots included; restores exactly what was there |
| **.mrpack** | a **share** | mods become links, somebody else's worlds left out, any launcher can install it |

One dialog with a mode rather than two windows, because the choice between them is the first thing the
user has to make and putting it behind two menu entries makes it a guess.

### What the mod downloader made possible

`PackExportTask` is only worth having because of wave 17. Every mod downloaded through the dialog gets
a packwiz `.pw.toml` beside it recording where it came from and its hash; this reads them back, and a
mod with an index entry becomes a **link** in the manifest — a URL and a hash — instead of a megabyte
of jar inside the zip.

A mod qualifies only if its entry has a URL **and** the file is still on disk under the name the entry
records. Both halves matter: an orphaned entry would put a dangling download into somebody else's pack,
and a linked mod must not *also* be copied in or every importer would write the file twice.

The exclusion list is deliberately **narrower than the instance export's**: a pack is for other people,
so saves, screenshots and `options.txt` go too. An instance export is a backup and keeps them.

### The round trip, against the real services

Real mods, downloaded from Modrinth, exported, and imported back:

```
instance folder:  1.59 MB
mrpack export:    linked=2  overrides=1  size=0.9 KB
zip export:       files=8   size=1.33 MB

detected as: Modrinth
reimport ok: True   pack='Round Trip'  files=2  downloaded=2
   lithium-fabric-mc1.20.1-0.11.4.jar    691,483 bytes
   sodium-fabric-0.5.13+mc1.20.1.jar     971,552 bytes
  config restored: True -> {"quality":"fast"}
  world leaked into pack: False
```

**1.59 MB of instance becomes a 0.9 KB pack**, and importing it re-fetches both mods from the real CDN
at byte-identical sizes with the configuration intact. That is the entire value proposition of the
format, demonstrated end to end rather than asserted.

### A byte-order mark, found only by the round trip

The first run of that probe failed:

```
reimport ok: False  modrinth.index.json: Error parsing JSON:
             '0xEF' is an invalid start of a value.
```

`0xEF` is a UTF-8 BOM. `Encoding.UTF8` emits one, and a `.mrpack` whose manifest starts `EF BB BF` is
rejected outright by `System.Text.Json` — and by other launchers' parsers too. The pack was invalid for
everybody, not just for this port.

**Eleven passing tests had not noticed**, because every one of them read the manifest back through a
`StreamReader`, which strips a BOM while decoding. The import path reads bytes.

> The same lesson this port keeps relearning, in a new disguise: **reading your own output back through
> your own abstraction proves the pair agree, not that the file is right.** The fix came with a test
> that reads the first three bytes and asserts they are `{`.

Two smaller things from the same wave. `RunAsync` **reports** rather than throws — `LauncherTask` turns
any escape into a failed state with a reason — and my first two failure tests asserted a throw; the
contract is the task's, not mine. And an exclusion is matched on whole path **segments**, so
`.minecraft/logs` does not also exclude a mod's `logsomething` folder, which a plain `StartsWith` would.


## Wave 20 — keeping an instance up to date

The other half of being able to install mods at all. A modded instance goes stale the week after it is
made — mods get performance fixes and crash fixes — and without this the only way to find out is to
notice a version number on a website.

### One request for the whole folder

Modrinth has two endpoints that look alike and are not: `/version_files` says which version a hash
**is** (that is what `EnsureMetadata` uses, and it was already ported), and `/version_files/update`
says what it should **become**. Only the first existed here, so the second is a small addition to
`ModrinthApi` — and it means checking a hundred mods costs one round trip rather than a hundred.

The loaders and game versions travel with the request as a **filter**, not as a description: without
them the service happily offers a 1.21 build as the update for a mod in a 1.20.1 instance, which
installs cleanly and then refuses to load.

It **hashes the files** rather than trusting the packwiz index. The entry records a hash, but the file
beside it may have been replaced by hand since, and an update check that reports on a file which is no
longer there is worse than one that reports nothing.

### The behaviour the real service forced

Probing `/version_files/update` before writing any test showed the thing that matters most:

> **It answers for every hash it recognises, including ones already current.** Its contract is "what
> this should be", not "what is newer".

Without a same-filename check the dialog offers to reinstall every mod in the folder as an update,
which is worse than useless — it looks like everything is stale. The probe against real data:

```
installing OLD Sodium  0.4.10   →  update offered: 0.5.13
installing OLD Lithium 0.11.2   →  update offered: 0.11.4
updates when already current: 0
updates for a vanilla instance: 0
```

### The ordering the install task exists for

**The new file is fetched and hash-verified before the old one moves.** Replacing a working mod with a
failed download is how an instance stops starting, and the user's next move is to launch it.

**The old file is renamed, not deleted.** A new build with a new crash is one somebody wants back, and
finding the old version on a website is a miserable way to spend an evening. It becomes `.old`, which
the game ignores because it is not a `.jar`.

**A disabled mod stays disabled.** One somebody turned off is one they will turn back on some day;
updating it must not silently put it back into the game.

One mod failing does not abandon the rest, and the failures are **named** — "3 of 5 updated" leaves
somebody guessing which two, and the two that failed are the ones they need to know about.

### Proved end to end, and a bug found by reading the output

An old Sodium installed with metadata, checked, and updated for real:

```
installed OLD: sodium-fabric-mc1.20-0.4.10+build.27.jar
updates found: 1
apply ok=True updated=1 failures=0

mods/  sodium-fabric-0.5.13+mc1.20.1.jar             971,552 bytes
       sodium-fabric-mc1.20-0.4.10+build.27.jar.old  717,030 bytes

updates after updating: 0
```

The packwiz entry moved with it — filename, url and hash all pointing at the new version. **Except one
field.** Reading the file the real update had produced showed `[update.modrinth] version` still naming
the version being *replaced*: every other field had moved on and that one had not.

It is what packwiz itself reads to decide what is installed, so any packwiz tool would have concluded
the old version was still there. Fixed, and pinned by a test that also checks `mod-id` is **not**
touched — the mod is the same mod — and that `hash-format` survives the `hash` rewrite, which it only
does because the rule matches `"hash "` with the space rather than `"hash"`.

> Ten passing tests had not caught it, because I wrote the fixture from what I thought the file
> contained rather than from one the launcher had actually produced. The probe output was the fixture
> I should have started from.


## Wave 21 — two gaps closed

### The silence that wave 20 shipped with

The update check asks Modrinth and nothing else, so a mod from CurseForge — or one built by hand — is
simply **absent from the answer**. That is indistinguishable from "already current" unless it is
counted, and a folder full of CurseForge mods would have reported *"Everything is up to date"*. Not
merely unhelpful: wrong, in the way somebody acts on.

Whether it could even be distinguished was a question about the real service, so it was asked of the
real service:

```
asked about 2 hashes, answer has 1 key(s)
  current mod hash present: True     <- a mod it knows, already current
  unknown jar hash present: False    <- a jar it has never seen
```

So presence in the answer is exactly the line between "current" and "not from Modrinth", and the check
now returns a `ModUpdateReport` carrying the count. The dialog says so:

> *2 mods can be updated. 4 mods were not recognised by Modrinth and could not be checked — mods from
> CurseForge or added by hand are not covered.*

With nothing skipped it keeps the plainer sentence, because qualifying a complete answer makes every
answer look doubtful.

### The game's own settings

`GameOptionsPageViewModel`, over the `GameOptions` parser that has been ported and tested since an
early wave and had never been shown. A modest page that earns its place for one reason: **an instance
whose graphics settings crash the machine on startup can be fixed here without launching it**, which is
otherwise a chicken-and-egg problem.

A flat key-value table, which is upstream's own treatment. `options.txt` holds two hundred keys of
wildly different kinds — booleans, enums, floats, JSON blobs for key bindings — and pretending to
understand them is how a launcher corrupts somebody's settings.

Two decisions that matter more than the page looks:

- **Only what changed is written back.** `options.txt` is full of keys this launcher has never heard of
  — every mod that stores a setting puts one here — and a page that rewrote the file from its own model
  would delete all of them. There is a test that adds `someModSetting` and checks it survives.
- **The view is filtered, not the list.** An option edited and then filtered away by the search box
  keeps its change, because the same object goes back in when the filter clears. Losing an edit by
  typing in a search box is a genuinely infuriating bug.

An instance that has never been launched has no `options.txt` at all. That is the ordinary state of a
new instance, so the page says so rather than showing an empty list somebody has to interpret.


## Wave 22 — installing Java from inside the launcher

`AutomaticJavaDownload` has been a registered, defaulted and (since wave 16) editable setting, and
nothing had ever acted on it. `ArchiveDownloadTask` and `ManifestDownloadTask` were ported and tested
and nothing had ever built one. What was missing between them was **the list of what exists**.

This is the difference between a launcher somebody can use and one they cannot: the game wants Java 17,
the machine has Java 8, and the alternative is a trip to a vendor's website and a guess about which
build.

### Four vendors, and why they are not interchangeable

| package | | |
| --- | --- | --- |
| `net.minecraft.java` | Mojang | what the game is tested against, so first in the list |
| `net.adoptium.java` | Temurin | the usual choice where Mojang publishes nothing for a platform |
| `com.azul.java` | Zulu | builds for platforms the others skip |
| `com.ibm.java` | Semeru | |

One vendor's list being absent is **not** a failure — not every metadata server carries all four, and
refusing to offer Mojang's builds because IBM's list is missing turns a small problem into a total one.

The version document is fetched **directly** rather than through the component machinery, because it is
not a component document: a java version file carries a `runtimes` array where a Minecraft version file
carries libraries and a main class. The version *list* is loaded the ported way, because that part
genuinely is the same shape.

### Three bugs the real metadata found, that no fixture would have

Probing `meta.prismlauncher.org` before writing a single test:

**Only Mojang publishes a version `name`.**

```
mojang   "version": { "major": 21, "minor": 0, "name": "21.0.7", "security": 7 }
azul     "version": { "major": 8,  "minor": 0, "security": 504 }
```

Every non-Mojang entry displayed as *"Java  — Azul (Zulu)"*, and — far worse — they all produced the
**same folder name**, `azul-`. Installing two Azul runtimes would have had the second silently
overwrite the first.

**A vendor and a version still do not identify a build.** `com.ibm.java` carries two different 25.0.2
runtimes for windows-x64 with different downloads. The folder now carries a short checksum suffix,
deterministic so reinstalling reuses its folder rather than accumulating copies.

**The metadata repeats itself.** `com.ibm.java` lists the same java23 build under two GitHub release
tags with an *identical* sha256:

```
.../jdk-23.0.2%2B7_openj9-0.49.0/ibm-semeru-open-jre_x64_windows_23.0.2_7_....zip
.../jdk-23.0.1%2B11_openj9-0.48.0/ibm-semeru-open-jre_x64_windows_23.0.2_7_....zip
```

Deduplicating by url leaves both in the list, and a user comparing two entries that differ in nothing
they can see has been handed a puzzle with no answer. Deduplicating by **checksum** — the truer
identity — is what fixed it. After all three: **247 runtimes, 247 distinct folders, zero collisions**,
majors 8 through 26.

### Proved by installing one

```
installing Java 17.0.20 — Adoptium (Temurin)  (Archive, jre)
  from .../OpenJDK17U-jre_x64_windows_hotspot_17.0.20_8.zip

install ok=True in 10.1s
  316 files, 124.8 MB
  java binary: .../java/eclipse-17.0.20-7fe2324/bin/java.exe
  probe ran=True validity=Valid version='17.0.20' arch=64 vendor='Eclipse Adoptium'
```

A real 124 MB runtime, downloaded, unpacked, and then **probed by the launcher's own JavaChecker** —
which is the only test that says the thing actually works rather than merely arriving. It is offered
from the settings window, beside the Java path box, because "the game wants Java 17" is discovered
while looking at exactly that setting.

Also this wave: `GameOptionsPageViewModel` gained nothing new, but the mod update check now **counts
and reports** what Modrinth did not recognise, closing the CurseForge silence wave 20 shipped with.


## Wave 23 — the launcher finds and fetches its own Java

Wave 22 could download a Java. **Nothing could then find it**, and nothing ever triggered the download
automatically. Both halves are now wired, and both were broken in ways only a real launch showed.

### The runtime it had just installed was invisible

The candidate list came from `JavaUtils.FindJavaPaths`, which looks where a SYSTEM Java lives — the
registry, `/usr/lib/jvm`, the PATH — and not in `<data>/java`. So the download worked, the probe
passed, and pressing Play still said *"no compatible Java installation found"*.

`ManagedJava` searches the launcher's own folder, and its runtimes go **first** in the candidate list:
one fetched for a specific instance is a more deliberate answer than whichever JDK happens to be on the
PATH, and the machine that needed the download is exactly the machine whose system Java was wrong.

The search is **recursive**, because the two download types unpack differently:

```
manifest (Mojang)    java/<name>/bin/java.exe
archive  (Temurin)   java/<name>/jdk-17.0.20+8-jre/bin/java.exe
```

A fixed depth finds one shape and misses the other — and misses three vendors out of four, since only
Mojang uses manifest. Only binaries **inside a `bin` folder** count: a JDK ships more than one thing
called `java`, and `bin` is where the one meant to be run lives.

### The setting was ticked and read as false

With discovery fixed, a launch that could not find Java 17 still refused instead of fetching one. The
reason:

> **`AutomaticJavaDownload` was registered globally, shown in the settings window, defaulted to true —
> and never registered on the instance settings object at all.** Reading it returned null, `GetBool`
> turned that into `false`, and the launcher behaved as though the feature were switched off while the
> tickbox sat there ticked.

Nothing caught it. The global tests asserted the global, the settings-page tests asserted the page, and
**no test had ever asked an instance what it thought the value was**. The new tests do, including one
that walks a list of keys the launch path reads and asserts each is registered — the general form of
the bug rather than the one instance of it. Removing the passthrough again fails all three.

### What a launch now does

Real, against an instance needing a Java this machine does not have:

```
system javas: 21.0.9, 25.0.1        <- no 17 anywhere

No Java 17 found. Downloading one…
Installing Java 17.0.15 — Mojang
Installed Java 17 into .../java/mojang-17.0.15-89ce85c
Java 17.0.15 (Microsoft, 64-bit)    <- the launcher's own probe, on the runtime it just fetched

resolved java: .../java/mojang-17.0.15-89ce85c/bin/java.exe
```

Decisions inside that: the **highest** major the instance accepts (a profile taking 17 or 21 works with
either, and the newer one still gets fixes); a **JRE over a JDK** (a third of the size, and the game
needs nothing a JDK adds); and the download is reached **only after the search has failed**, because
fetching a hundred megabytes while a usable Java sits on the machine would be absurd.

The failure message now names the remedy — *"Install one from Settings, or turn on automatic Java
downloads"* — because "no compatible Java" says what is wrong and nothing about what to do.

**A cost worth stating: that install took 197 seconds.** Mojang's builds use the `manifest` download
type, which fetches hundreds of individual files; Temurin's `archive` type pulled 124 MB in 10. The
launcher prefers Mojang because it is what the game is tested against, and the result is a first launch
that can spend three minutes before the game starts. It reports progress throughout, but this is a real
trade and it is chosen, not accidental.


## Wave 24 — the launcher makes instances that actually run

Setting out to prove the previous wave's last unproved claim — that a downloaded Java could start a
game — turned up a bug that meant **no modern vanilla instance this launcher created could run at all**.

### What the real launch showed

```
Minecraft: Minecraft is missing requirement org.lwjgl3 3.3.1
...
Game exited abnormally with code 1.
```

Resolving the instance confirmed it: **36 libraries, 0 of them LWJGL, 0 natives.** `net.minecraft
1.20.1` requires `org.lwjgl3`, which carries the entire windowing and input layer. The instance had
recorded only what it was told about, the game started, found no LWJGL, and died instantly.

### Why nothing caught it

`ComponentUpdateTask` has two modes. **Launch mode deliberately only reports** unmet requirements —
upstream's reasoning is that resolution "must not start changing versions under someone who is trying
to play", which is right. The other mode, `Resolution`, **was never called from anywhere in this port**.

So the dependency was reported at every launch and added at none of them. And sitting in
`VanillaCreationTask` was a comment asserting the opposite:

> *"NOT RESOLVED HERE, deliberately. Upstream does not either: creation writes what was asked for, and
> the first launch resolves it against the metadata server."*

The first half is right and the second is wrong, and the wrong half is why the bug existed. Upstream
resolves as part of **creating** the instance, which is why it never hits this.

`ComponentResolution` is that pass. It runs when an instance is created and when a pack is imported —
a `.mrpack` names its Minecraft version and its loader and nothing else, so an imported instance was
exactly as incomplete. It is **best effort**: with no client it does nothing and the instance is still
written, which is what the old comment was actually protecting.

### The proof

A freshly created 1.20.1 instance, made the way the New Instance dialog makes one:

```json
{ "uid": "net.minecraft", "version": "1.20.1", "important": true,
  "cachedRequires": [ { "uid": "org.lwjgl3", "suggests": "3.3.1" } ] },
{ "uid": "org.lwjgl3", "version": "3.3.1", "dependencyOnly": true }
```

Libraries went from 36 to **64**, with **28 LWJGL entries**. Launching it:

```
[Datafixer Bootstrap/INFO]: 188 Datafixer optimizations took 222 milliseconds
[Render thread/INFO]: Setting user: PortTester
[Render thread/INFO]: Backend library: LWJGL version 3.3.1 build 7
```

**Minecraft 1.20.1, running, on a Java 17 the launcher downloaded for itself**, from an instance the
launcher created — with no missing-requirement warning anywhere. Every earlier wave's claim about
launching was made against an older Minecraft whose LWJGL lived inside the `net.minecraft` component;
1.20.1 is where it became a separate one, and where this broke.

### A failure that was mine, not the launcher's

Between the two, a run failed with `ClassNotFoundException: net.minecraft.client.main.Main` and looked
like a second launcher bug. It was not. All 65 classpath entries pointed at directories that did not
exist, because **library paths are stored relative and resolve against the process working directory**
— `App.axaml.cs` sets it to the data root, and my probe did not. Setting it made all 65 resolve.

> Worth writing down twice: an investigation tool that does not set up the environment the way the
> application does will produce failures that look exactly like application bugs. The first one here
> was real and the second was mine, and they arrived one after the other.

### Also this wave

`AboutViewModel` and its window, which had no equivalent at all. Version with the **channel** (a "5.1"
develop build and a "5.1" release are different software, and only one is downloadable), the real
runtime rather than the build string (this port runs one build on three desktops, so `BuildPlatform`
cannot answer "which OS"), the data directory, and a copy button — because asking somebody to retype a
commit hash is how bug reports arrive with the wrong one. The **licence is stated, not linked**: this
is GPL-3.0 software and the licence requires the user be told.


## Wave 25 — modded instances, and the second half of the same bug

Last wave ended by naming what had not been proved: *"no modded instance has been launched — the
loader components are exactly where a resolution bug like wave 24's would hide."* It was, and it did.

### What the Fabric launch showed

Creating a Fabric 1.20.1 instance produced a component list that looked right — Minecraft, LWJGL,
fabric-loader, **and** `net.fabricmc.intermediary`, which the new resolution pass had correctly worked
out was needed. Launching it:

```
Failed to load net.fabricmc.intermediary/.json:
  Request to .../v1/net.fabricmc.intermediary/.json failed: HTTP 404 Not Found
```

Note the URL. `net.fabricmc.intermediary/` **`.json`** — the version is empty. The component had been
added with no version at all.

### Why, and what upstream does about it

Fabric's requirement names no version whatsoever:

```json
"requires": [ { "uid": "net.fabricmc.intermediary" } ]
```

Which is correct on Fabric's part: *which* intermediary you need is decided by your Minecraft version,
not by Fabric. Intermediary's own versions each pin one:

```json
{ "version": "1.20.1", "requires": [ { "uid": "net.minecraft", "equals": "1.20.1" } ] }
```

This port took `equals`, fell back to `suggests`, and when a requirement had **neither** produced an
empty string. Upstream handles it — with a small hardcoded table, and it is entirely honest about what
that is:

```cpp
// ############################################################################
// HACK HACK HACK HACK FIXME: this is a placeholder for deciding what version to
// use. For now, it is hardcoded.
if (add.uid == "org.lwjgl")   component->m_version = "2.9.1";
else if (add.uid == "org.lwjgl3") component->m_version = "3.1.2";
else if (add.uid == "net.fabricmc.intermediary" || add.uid == "org.quiltmc.hashed") {
    component->m_version = minecraft->getVersion();
}
```

Ported as it stands, banner and all. The alternative is inventing a constraint solver upstream does not
have and then disagreeing with it about what an instance contains — and an unknown uid still gets an
empty version rather than a guess, because guessing on upstream's behalf is worse than reporting.

### The proof

```
Loading Minecraft 1.20.1 with Fabric Loader 0.15.7
Fabric is preparing JARs on first launch, this may take a few seconds...
SpongePowered MIXIN Subsystem Version=0.8.5 Service=Knot/Fabric Env=CLIENT
Setting user: PortTester
Backend library: LWJGL version 3.3.1 SNAPSHOT
```

**A modded Minecraft instance, created by this launcher, resolved by it, running on a Java it
downloaded** — with Fabric Loader and the Mixin subsystem live. That is the last of the three
end-to-end claims this port had been unable to make.

### The guard that would have caught both

Six regression tests, and one of them is deliberately not about Fabric:

> `NoComponentEverEndsUpWithAnEmptyVersionForFabric` — after resolving, **every** component has a
> version. One without is a request for `<uid>/.json` waiting to happen.

Reverting the table fails three of the six, that one included. Wave 24's bug and wave 25's were the
same bug wearing different clothes — a component the resolver knew about but could not name a version
for — and a test phrased as "no component is ever versionless" catches the whole family rather than the
two members of it that happen to have been found.

> Two waves running, the thing that found the bug was **launching the game and reading what it said**.
> Nothing in 2,600 tests noticed either one, because every one of those tests was built from a fixture
> written by the same person who wrote the code.


## Wave 26 — Forge runs, and a helper I wrote wrong before reading the source

Last wave named Forge as the remaining place a resolution bug could hide, because its chain is the
most complicated of the four: it carries an installer and patches the jar rather than merely adding
libraries.

### Forge works

Creating a Forge 1.20.1 instance produced three components, all versioned, and the launch showed
Forge's installer tooling doing real work before the game started:

```
MainClass: net.minecraftforge.installertools.ConsoleTool
  Args: --task, MCP_DATA, --input, .../mcp_config-1.20.1-20230612.114412.zip, ...
  Args: --task, DOWNLOAD_MOJMAPS, --version, 1.20.1, --side, client, ...
```

Then:

```
ModLauncher running: args [--username, PortTester, --version, 1.20.1, ...,
  --launchTarget, forgeclient, --fml.forgeVersion, 47.4.10, --fml.mcVersion, 1.20.1,
  --fml.forgeGroup, net.minecraftforge, --fml.mcpVersion, 20230612.114412]
Setting user: PortTester
Backend library: LWJGL version 3.3.1 build 7
```

`--launchTarget forgeclient` with the complete `--fml.*` set, three post-processing tasks run, and
Minecraft up. **All three loader paths this port supports now start a game**: vanilla, Fabric, Forge.

A small thing worth noticing in that line: `--accessToken, ????????`. The launcher masks the token in
its own log, which matters because a log is the thing people paste into bug reports.

### And a mistake worth recording

The first version of `ExportToModList` I wrote built each line by substituting a mod into a per-format
template and then *deleting* the fields nobody had asked for. It compiled, it looked reasonable, and it
was not what upstream does.

Upstream appends field by field, per format, and **skips a field the mod has not got**:

```cpp
if (extraData & Url) {
    auto url = mod->metaurl().toHtmlEscaped();
    if (!url.isEmpty())
        modName = QString("<a href=\"%1\">%2</a>").arg(url, modName);
}
```

The difference shows on the first mod without a url: upstream writes `Handmade Mod`, and a template
with an empty substitution writes `Handmade Mod () []`.

There were three more differences underneath, none of which a shared template can express:

- each format **escapes differently** — HTML entity-escapes, Markdown backslash-escapes eighteen
  specific characters, CSV quotes and doubles, and JSON is *built* rather than printed;
- Markdown escapes the **name** but not the **url**, because the url goes inside the link target where
  a backslash would become part of the address;
- CSV still emits a cell for a field the mod lacks, because rows of different widths are not a CSV —
  it is the one format where "skip the field" cannot mean "skip the comma".

> I had the source open in the next pane and wrote the helper from the shape of the problem instead.
> The rule this port keeps rediscovering is that **the Qt source is the contract**, and it is cheapest
> to read it first.

Rewritten as five builders. Twenty-two tests, most of them about escaping, because a mod name with a
bracket in it is the whole difference between a working markdown link and a mess.


## Wave 27 — NeoForge, and the mod list gets a window

### All four loaders now start a game

NeoForge was the last untested path. It shares Forge's shape, which made it the likeliest of the
untested ones to work — and "likeliest to work" is not the same as knowing:

```
ModLauncher running: args [..., --launchTarget, forgeclient,
  --fml.forgeVersion, 47.1.106, --fml.fmlVersion, 47.2.2, --fml.mcVersion, 1.20.1, ...]
Setting user: PortTester
Backend library: LWJGL version 3.3.1 build 7
```

Note `--fml.fmlVersion 47.2.2`, which Forge's own launch does not carry: the two are close but not
identical, and only running it shows that.

**Vanilla, Fabric, Forge and NeoForge**: created by this launcher, resolved by it, running on a Java
it downloaded. That closes the whole family of end-to-end claims this port has been unable to make.

### The mod list export gets a UI

`ExportToModList` rendered five formats and nothing called it — the same gap every wave starts from.
It is reached from the mods page now, with a **live preview**, which is the reason the dialog exists:
five formats and four optional fields is twenty combinations, and nobody can picture "markdown without
authors" without looking at it.

Two deliberate choices:

- **Markdown is the default**, where upstream defaults to HTML. HTML is the format whose output is
  least readable in a preview pane, and the places people actually paste a mod list — issue trackers,
  wikis, chat — take markdown.
- **Disabled mods are included.** A list of what is in the folder is more useful than a list of what
  is switched on, and the file name gives it away anyway.

Exposing it needed `Mod.Authors` and `Mod.HomeUrl` on the row, both of which the jar parser had been
reading and discarding since the wave that wrote it.

### A test that failed for the right reason

`TheFileNameFieldIsOffToBeginWith` asserted `sodium.jar` appeared in the markdown preview once the box
was ticked. It failed — because a dot is one of the eighteen characters upstream escapes, so it renders
as `sodium\.jar`.

The escaping was right and the assertion was naive. Rather than loosening it, the test now checks the
plain-text preview where no escaping applies, and a **second** test pins the markdown behaviour
explicitly. A test that fails because the code is more careful than the test is worth keeping the
careful version of.


## Wave 28 — the whole circle, and the escape hatch

### A game running with mods the launcher installed

The last unproved claim, and the one that ties every other wave together:

```
Loading Minecraft 1.20.1 with Fabric Loader 0.15.7
Loading 11 mods:
	- fabricloader 0.15.7
	- java 17
	- lithium 0.11.4
	- minecraft 1.20.1
	- sodium 0.5.13+mc1.20.1
Setting user: PortTester
Backend library: LWJGL version 3.3.1 SNAPSHOT
```

Fabric names **sodium 0.5.13** and **lithium 0.11.4** — the exact mods this launcher searched for on
Modrinth (wave 17), downloaded and hashed, wrote packwiz metadata for, and listed on its mods page.
The `java 17` line is the runtime it fetched for itself (wave 22). The instance was created and
resolved by it (waves 24–25).

That is the launcher proved end to end in one run: **search → download → install → resolve → fetch a
Java → launch → the loader finds the mods.**

### The escape hatch

Upstream's main window carries fourteen separate `View*Folder` actions over one mechanism, and this
port had none of them. However complete a launcher's UI gets, somebody eventually has to look at a
file — a crash report to attach, a config nothing edits, a jar to drop in by hand — and a launcher
that cannot show them where its files are turns a click into a search.

A **Folders** menu now opens the selected instance, the launcher root, instances, logs, java and
icons. Two details worth keeping:

- **The folder is created if it is not there.** Several are made lazily — the icons folder does not
  exist until an icon is imported — and opening a file manager on a path that does not exist either
  fails silently or shows an error box, both of which read as the launcher being broken.
- **The view model asks by NAME, never by path.** `LauncherPaths` stays the single place that decides
  where anything lives, which is the rule the whole port has kept.

### Rename and group

Both were upstream actions with no equivalent here, and both turn on the same distinction:

> **Null and empty are different answers.** Null is "cancelled". Empty is something the user actually
> typed — and it means opposite things in the two cases. An empty NAME is refused, because an instance
> with no name is a blank tile nobody can find again. An empty GROUP is honoured, because it is the
> only way to leave one, and refusing it would trap an instance in whichever group it was put in.

The group prompt also lists the groups that already exist, since a name differing by one capital
letter makes a second group nobody wanted.


## Wave 29 — dropping a file on the launcher

Six validators for six kinds of packed resource -- mods, resource packs, texture packs, data packs,
shader packs, world saves -- were ported waves ago and **nothing ever asked them a question**.
`LocalResourceParse` is the twenty lines that do, and drag-and-drop is what it is for: somebody
downloaded a zip in a browser and wants it in their instance.

### The order of the checks is the whole trick

Upstream comments the first branch, and the comment is the reason the function is shaped as it is:

```cpp
if (ModUtils::validate(file)) {
    // mods can contain resource and data packs so they must be tested first
```

A mod jar routinely carries a `pack.mcmeta` and an `assets/` tree, so a resource-pack test claims it
happily. Swapping those two branches makes the launcher file mods as resource packs -- an instance
that starts with none of its mods loaded, and nothing on screen suggesting the launcher did it.

Proved by doing it: reordering the two checks fails `AModThatAlsoContainsAResourcePackIsStillAMod`
and nothing else.

### Three decisions in the importer

- **Copied, not moved.** The file is somebody's own, sitting in their downloads folder. A launcher
  that makes it vanish from where they put it has done something they did not ask for.
- **A clashing name is suffixed, not overwritten.** Dropping a newer build whose file name has not
  changed is a real thing people do; silently replacing the old one removes something they might have
  wanted and leaves no sign it happened.
- **Unrecognised is refused, not filed somewhere plausible.** Dropping an unknown zip into `mods/`
  produces an instance that will not start, with nothing explaining why -- and the user, who knows
  what they dropped, cannot tell it was misfiled.

A **texture pack** goes to `texturepacks`, not `resourcepacks`: it is the pre-1.6 format read from a
different folder, and putting one in with the resource packs makes it invisible, which reads exactly
like the import having failed.

### Where the drop is accepted

**On the window, not on the mods page.** A file dropped anywhere in an instance window is meant for
that instance, and making somebody hit a particular pane with the mouse is a precision the gesture does
not need. The page it lands on does not decide anything either -- a jar dropped while the shader-pack
tab happens to be open is still a mod. **The file decides.**

The event handling stays in code-behind: Avalonia delivers a drop through `DragOver` and `Drop` with a
platform payload, neither of which belongs in a view model that has to run headlessly. What crosses
into the view model is a list of paths.


## Wave 30 — a shortcut that starts one instance

`--launch <id>` has been a supported, tested command line since wave 10. A desktop shortcut is only a
file that types it — which is what makes this worth having rather than a novelty: the difference
between "open the launcher, find the instance, press play" and "double-click the thing on the desktop".

### Three platforms, three entirely different files

| | |
| --- | --- |
| Linux | a `.desktop` entry, marked executable |
| macOS | a `.app` bundle — directories, an `Info.plist`, a `Run.command` inside |
| Windows | a `.lnk`, a binary format nothing writes by hand |

Upstream's shape exactly, including the details that are easy to skip:

- The `.desktop` **Categories** line, which puts the shortcut under Games rather than in the "Other"
  bucket nobody looks in.
- The **execute bit**, without which every modern desktop refuses a `.desktop` file with a warning
  about untrusted launchers — reading as though this launcher produced something suspicious.
- Arguments **single-quoted** in `Exec`, because an instance id or a data directory can contain
  spaces.
- `--dir` alongside `--launch`, because a portable install keeps its data beside the executable and a
  shortcut that omitted it would start a launcher pointing at an empty data folder.

macOS gets the full bundle rather than a bare `.command` script: a script does not appear in Launchpad,
does not take an icon, and does not behave like an application when double-clicked, which is the entire
point of making one.

### Why the Windows one shells out

Upstream uses `IShellLink` directly. In C# that means P/Invoke against a COM interface — and **an
earlier wave of this port hand-rolled a P/Invoke for `SHFileOperation` that crashed the test host
outright with an access violation**, because the struct packing was wrong on x64.

`WScript.Shell` does the same job through a mechanism that cannot corrupt this process's memory, and
the cost is one short-lived subprocess on a button nobody presses twice. The script is fixed text with
every value passed as a single-quoted PowerShell literal and any quote doubled, so a path containing
one cannot end the string and become script — an instance called `Bob's World` is entirely ordinary,
and there is a test for it.

### Read back through the shell, not the bytes

The Windows test writes a `.lnk` and then asks **WScript.Shell** what it points at:

```
target:    ...\launcher.exe
arguments: --dir ... --launch one
```

Parsing the binary here would prove this port can read its own output. Asking the shell proves the
shell can — and a `.lnk` this port could parse and Explorer could not is exactly the failure worth
catching.

The Linux and macOS tests are **skipped rather than faked**. A `.desktop` file and a `.lnk` have
nothing in common, and an abstraction that let all three be tested everywhere would be testing the
abstraction.


## Wave 31 — a theme, and a fourth kind of setting

`ApplicationTheme` is a registered setting upstream. Here the theme was hardcoded to `Default` in
App.axaml and there was no way to change it — which is the first thing anybody notices about a window.

**One setting, not a theme engine.** Upstream carries a whole ThemeManager with widget themes, icon
themes and cat packs read from disk; none of that is ported and this is not it. What this does is the
part every user sees on the first run: whether the window is light or dark.

**Applied live.** `RequestedThemeVariant` is a property on the application, so changing it repaints
every open window — and it is *subscribed* rather than read once, so the settings window does not need
to know a theme exists. It writes a value like any other setting and the window repaints itself. A
theme picker that needed a restart would be worse than none.

Anything unrecognised — including the empty string — falls back to following the system. A config
edited by hand, or written by a newer version with more themes, must still produce a usable window.

### The choice editor

The settings machinery had three editors: text, number, toggle. A theme is none of those. **"drak"
typed into a text box is a setting that silently does nothing**, and the launcher has no way to tell
the user so.

`ChoiceSettingViewModel` is the fourth, and it separates the **id** from the **label** deliberately:
what goes in the config file is a compatibility surface and must not change when the wording does.
"Follow the system" is a label; `system` is the value, and there is a test asserting the file contains
the latter and not the former.

### A coverage test that had quietly stopped covering

Wave 16 left a guard called `EverySettingGetsARealEditorControl` — it counts the TextBoxes,
NumericUpDowns and CheckBoxes in the settings window and compares each against the model. It is the
only test that can see a `DataTemplate` matching nothing.

When the choice editor arrived, **that test still passed with no ComboBox template at all**. A
`ChoiceSettingViewModel` is none of the three types it counted, so nothing asserted the new setting had
an editor.

It now counts pickers too, and — the part that makes it durable — asserts the four counts **sum to the
total number of settings**. A fifth editor type added without a template will fail on that line even if
whoever adds it forgets to extend the list above it.

> A coverage test that only covers the types that existed when it was written stops being a coverage
> test the moment a new one is added. The fix is to make it assert on the total, not on a list.

Removing the choice template fails it; the version before this wave did not notice.


## Wave 32 — the help menu, and the support action of last resort

Two upstream toolbar actions with nothing behind them here, both small enough to do together.

### Links, built from what this build actually has

Upstream's toolbar carries Discord, Matrix, Reddit, the wiki and a bug tracker. Its CMakeLists leaves
**most of those URLs empty by default** for a fork to fill in, and this fork has filled in two of them.

So the menu is built from what is configured rather than from a fixed list:

> An entry that opens nothing looks like the launcher is broken. An absent entry looks like the fork
> has no Discord — which is the truth.

The scheme is still checked before opening, even though these URLs come from `BuildConfig` — a
compile-time constant of this build rather than anything from the network or from a user. A fork can
put whatever it likes in that config, and *"it came from our own settings"* is a weaker guarantee than
*"it is http or https"*. `UseShellExecute` hands whatever it is given straight to the desktop.

### Clearing the metadata cache

The support action of last resort, and upstream has it for a reason this port has now hit twice: a
stale or half-written metadata cache produces resolve failures that look exactly like the launcher
being broken, and the fix is to throw it away and fetch again.

- **It asks first.** Nothing is lost — every byte is re-downloadable — but it is still a delete, and a
  menu item that silently throws something away is one people learn to be afraid of.
- **It says what it removed.** "Done" tells somebody nothing about whether the thing they were trying
  to fix was even cached.
- **The folder is removed and remade**, not emptied file by file: the cache has a directory per
  package, and leaving the empty shells behind would have the next fetch write into a tree that looks
  populated.

### A static that needed putting back

`BuildConfig.Instance` is a settable static — which is what lets `BuildConfigOverrides` supply
credentials at startup. Testing "an unconfigured link is hidden" means changing it, and a test that
changes a static without restoring it leaks into every test that runs after it in the same assembly.

The fixture captures the original in a field and restores it in `Dispose`. Worth writing down because
the failure mode is not a failing test — it is a *different* test failing later, for a reason that has
nothing to do with what it is testing.

### Still not proved

- **No link has been opened and no cache cleared for real.** Both go through a stub in the tests;
  the browser hand-off and the recursive delete have only ever run against fakes.
- **Nobody has seen the dark theme.** The setting is read, applied and followed, and the app starts
  clean with it configured -- but the headless platform draws nothing, so whether the window actually
  looks right in either variant is unverified.
- **No shortcut has been double-clicked.** The Windows .lnk is written and read back through the
  shell with the right target and arguments; nobody has run one and watched an instance start. The
  Linux and macOS shortcuts have not been written at all -- those tests skip off-platform.
- **Nothing has ever been dropped on the window.** The importer is proved against real zips, but
  the drag-and-drop plumbing itself -- DragOver, Drop, the platform's file payload -- cannot be
  driven headlessly and has never run.
- **No folder has ever actually been opened.** The opener is covered by a stub; whether a real file
  manager appears is unverified on every platform, and it is one of the few things that genuinely
  differs between the three.
- **No instance with mods installed has been launched.** Mods download and the page reads them back;
  no game has started with one loaded, so nothing has proved the mods folder is even on Fabric's
  search path.
- **The resolution pass has never run offline.** It skips itself without a client, and an instance
  created that way stays unrunnable until something resolves it -- nothing yet does that later.
- **A first launch on a machine with no suitable Java takes about three minutes**, because Mojang's
  builds download per-file. Nothing offers the faster archive vendors when speed matters more than
  matching what Mojang tests against.

- **Only Modrinth mods can be updated.** A CurseForge mod has no hash the update endpoint knows, so it
  cannot be checked. It is now COUNTED and reported rather than silently treated as current (wave 21),
  but updating one still needs the Flame API and a key this build does not have.
- **The game options page has never been edited by a human.** Writing back is proved against the file,
  including that unknown keys survive; whether two hundred rows are navigable is unknown.
- **An updated instance has never been launched.** The new jar is on disk with the right bytes; nothing
  says the game starts with it.
- **No exported pack has been opened by another launcher.** It round-trips through this port's own
  importer and `PackTypeDetector` recognises it, but Prism, MultiMC and the Modrinth app have never
  been handed one -- and the BOM bug is exactly the kind of thing only that would have caught sooner.
- ~~**CurseForge export is not implemented.**~~ *Done in wave 54: an export format offered beside
  Modrinth, mods it recognises linked by id and the rest carried, round-tripped through the port's own
  Flame reader.*
- **The save dialog has never been opened**, like every other file picker in this port.
- **A large instance has never been exported.** The round trip was 1.59 MB; nothing says how a 5 GB
  instance with worlds behaves, and the whole zip is built in one blocking call.
- **Nobody has seen an instance icon.** The 27 built-ins are proved to exist as embedded resources
  with valid PNG headers, and the picker is covered headlessly -- but the headless platform fakes the
  image decoder, so whether a tile actually draws is unverified on every platform.
- **The icon file picker has never been opened**, for the same reason the pack importer's has not: a
  real StorageProvider dialog cannot be driven from a headless test.
- **The modern SVG icon set is not ported at all.** Upstream ships both; only the raster set came
  across, because drawing an SVG needs a dependency this port has not taken.
- **CurseForge search has never run.** The code path is there and refuses itself politely without a
  key; not one request has ever been sent to it, so its response shapes are unverified against the
  real service -- unlike Modrinth's, which are now proved end to end.
- **No downloaded mod has been launched.** Two real mods land on disk with correct packwiz metadata
  and the mods page reads them back, but no game has been started with them installed.
- **Resource-pack and shader-pack downloading is untested against the real API.** The loader filter is
  deliberately dropped for those, which is the part most likely to be wrong, and only the mod path has
  been run for real.
- **Neither new window has been seen by a human.** Both are covered headlessly -- every setting has a
  real editor control, Save writes the file, Cancel reverts -- but whether ten scrolling sections are
  navigable, or the version list readable, is unknown.
- **Change version has never been used end to end.** The list is proved against the live metadata
  server and the write is proved against `mmc-pack.json`, but no instance has been upgraded from one
  Minecraft version to another and then launched, which is the only test that counts.
- **Adding a loader has never been resolved or launched.** Writing `net.minecraftforge 47.4.10` into a
  pack file is not the same as the instance then starting.
- **No sign-in has ever been completed.** The device-code request reaches the real Microsoft endpoint
  and returns a real code; everything after that — the Xbox exchanges, entitlements, profile and skin,
  against the live services — has only ever run against a stub. Completing one needs a human to
  authorize in a browser. **This is the single largest remaining gap between "tested" and "known to
  work".**
- **No token has ever been refreshed for real.** `MSARefreshStep` is tested against stubbed responses
  in every branch, but the actual exchange with Microsoft has never happened, and neither has the
  case it exists for: a launcher opened the day after signing in.
- The accounts window has **never been seen by a human**. Its structure, bindings and file effects are
  covered headlessly; whether the code is legible at that size, or the layout sensible, is unknown.
- The game has been started but never **played** — no world loaded, no server joined. `--server` and
  `--world` are wired through `LaunchTarget.Parse` and unit-tested, but never used against a real one.
- Nothing here has run on Linux or macOS.
- The window's own appearance is only known from a screenshot: instance tile, toolbar, search box, sort
  box and status bar all render, and the status bar showed the failure correctly.


## Wave 33 — going through a proxy

For a lot of people this is the difference between a launcher that works at all and one that cannot
reach anything. A launcher does nothing but talk to the network — metadata, mods, assets, Java,
sign-in — so on a network that requires a proxy, none of it works and every failure looks like a
different bug.

`ProxyFactory.CreateHandler(settings)` builds the handler `App.axaml.cs` gives its one `HttpClient`,
and a Proxy section in the global settings window edits the five keys upstream registers.

### The default is the surprising part, and it is upstream's

`Application.cpp:631` registers `ProxyType` with the default `"None"` — **no proxy, even if the
machine has one configured**. Not "follow the system". That cuts both ways, and both ways are real:
somebody behind a corporate proxy gets nothing until they come to the settings and pick `Default`,
and somebody with a stale WPAD entry (a common way for Windows to break every application at once) is
unaffected by it.

This is a **divergence from what this port did before the wave**. A plain `new HttpClient()` follows
the system settings, so until this file existed the port behaved as if `Default` were selected. The
test that pins it — `AFreshInstallConnectsDirectlyRatherThanFollowingTheSystem` — was originally
written the other way round, asserting the system default, because that is what I assumed a sensible
launcher would do. The source said otherwise. It is the ground rule doing its job: I would have
shipped a launcher that quietly disagreed with upstream about the one setting nobody looks at until
it matters.

The four values are spelled upstream's way — `Default`, `None`, `HTTP`, `SOCKS5` — because
**extremelauncher.cfg is a shared format**. Upstream compares them case-sensitively, so writing
`socks5` would leave a config this launcher honours and Prism silently ignores.

### A handler, not a proxy

`CreateHandler` returns an `HttpMessageHandler` rather than an `IWebProxy`, because the two answers
"use the system proxy" and "use no proxy at all" are not both expressible as a proxy object: the
first is .NET's default and the second needs `UseProxy` turned off. Returning the handler keeps that
decision in one place instead of splitting it across a factory and its caller.

It is read **once**, when the client is built. Upstream re-applies live because Qt has an
application-wide proxy; a live `HttpClient` cannot have its proxy changed. So an edit here applies
from the next start — which the settings window now says on screen rather than leaving somebody to
wonder why nothing happened.

### The fallback cases are the point

A proxy setting that is wrong in a way nobody notices takes every feature down at once, and the
settings most likely to be wrong are the half-filled ones:

- **An empty address with the type set to HTTP** — what you get from picking the radio button and not
  filling the form in — falls back to the system settings rather than building a proxy from nothing.
  A proxy with an empty host breaks every request with an error that says nothing about the cause.
- **An impossible port** does the same. This one is not merely defensive: `Application.cpp:816` reads
  `ProxyPort` through `value<qint16>()`, a **signed** 16-bit read, while `ProxyPage` writes and reads
  the same number as `uint16_t`. Upstream's settings window therefore shows the right port while the
  connection uses a negative one for anything above 32767. Here an impossible port is refused
  outright and the description says so.
- **An unrecognised type does not turn networking off.** It lands in the same branch as `Default`,
  which is upstream's behaviour — its final `else` calls `setUseSystemConfiguration(true)`. Worth
  noticing that this differs from the *registered* default: no `ProxyType` at all reads as `None` and
  connects directly, while a value nobody recognises uses the system settings.
- **Credentials are attached only when there is a user name.** An empty credential is not the same as
  no credential, and some proxies refuse an anonymous bind that arrives carrying an empty user.

`Describe(settings)` returns the sentence for the log and the window, because three of the four values
look identical in a config file and behave completely differently — and because a half-filled form
otherwise does nothing at all, silently.

### The synonym is a file-format thing, not a lookup key

`ProxyAddr` was called `ProxyHostName` once, and upstream registers the two as synonyms. My first test
called `Set("ProxyHostName", …)` and failed — and the test was wrong, not the port.
`SettingsObject.cpp:68` inserts only `synonyms.first()` into the map, so the old spelling is not a
lookup key at all; it is only ever read out of an **existing config file**. The test now writes one and
reads through it, which is the only path that can really carry an old spelling.

### The password field is masked

Upstream's `ProxyPage.ui` sets `QLineEdit::Password` on the password box, so `TextSettingViewModel`
grew a `MaskCharacter` and both editor templates bind `PasswordChar`. It buys nothing against anybody
holding the config file — the value is plain text in there either way, which the section says out loud
— but that is not who it is for: it is for the person standing behind you while you type a work
credential into a Minecraft launcher.

The UI test counts masked boxes **against the model** rather than looking for one particular field, the
same shape as the every-setting-has-an-editor guard, so a second masked setting cannot quietly go
unmasked. Verified by deleting the binding and watching it fail.

### Proved for real, against a real proxy

A probe stood up an actual HTTP `CONNECT` proxy on a loopback port — including a `407` challenge with
`Proxy-Authenticate: Basic` — pointed the factory at it through a real config file, and fetched real
metadata:

```
proxy listening on 127.0.0.1:15368
describe: Connecting through the HTTP proxy at 127.0.0.1:15368.
fetched 2455 bytes through the proxy

what the proxy saw:
  CONNECT meta.prismlauncher.org:443 HTTP/1.1   (no credentials)
  CONNECT meta.prismlauncher.org:443 HTTP/1.1   Proxy-Authorization: correct Basic credential

describe: Connecting directly, ignoring any proxy this machine is configured with.
fetched 2455 bytes directly
proxy saw 0 connections (want 0)

describe: No usable proxy address is set, so the system settings are being used instead.
fetched 2455 bytes with a half-filled proxy form

dead proxy says: No connection could be made because the target machine actively refused it.
```

So the whole chain is proved end to end: the handler carries real traffic through a real proxy, the
credential path survives the challenge-and-retry that .NET only performs after a `407`, `None` really
bypasses (the proxy saw nothing), and a half-filled form still fetches. The app itself was started
twice and logged `Connecting directly, ignoring any proxy this machine is configured with.` and then
`Connecting through the HTTP proxy at proxy.example.invalid:3128.` from a config file.

Three guards were verified by breaking them: removing the half-configured fallback (4 tests fail),
turning the unrecognised-type branch into `UseProxy = false` (`DefaultMeansTheMachinesOwnSettings` and
`AnUnrecognisedTypeDoesNotTurnNetworkingOff` fail), and always attaching credentials
(`NoUserNameMeansNoCredentials` fails).

### Still not proved

- **No SOCKS5 proxy has been connected through.** The HTTP path is proved against a real proxy; the
  SOCKS5 path is proved only as far as the URI scheme .NET is handed. `SocketsHttpHandler` supports
  `socks5://`, but nothing here has carried a byte over one.
- **No proxy that needs Digest or NTLM has been tried**, only Basic. A Windows corporate proxy is as
  likely to want NTLM, and `NetworkCredential` alone may not be enough for it.
- **Minecraft still ignores all of it.** That is upstream's behaviour and the section says so on
  screen, but it means the most common reason somebody sets a proxy — getting the *game* onto a
  network — is not something this can do.
- **A proxy that fails mid-session is not recovered from.** The handler is built once at startup, so a
  proxy that goes away leaves every subsequent request failing until the launcher is restarted; there
  is no re-read and no message saying that is what happened.
- **The settings section has not been seen by a human**, like every other window in this port.

## Wave 34 — the news feed

The toolbar's newest headline, the window behind it, and the feed both come from. Upstream:
`news/NewsChecker.cpp`, `news/NewsEntry.cpp`, `ui/dialogs/NewsDialog.cpp` and
`MainWindow::updateNewsLabel`.

Worth doing early in the UI backlog because it is the one remaining toolbar control with *nothing*
behind it, and because this fork has a live feed to prove it against — 23 real articles, updated the
day this was written.

### It is called RSS everywhere and it is Atom

Upstream names the download job `"News RSS Feed"`, the config key `Launcher_NEWS_RSS_URL`, the
handler `rssDownloadFinished`. And then `NewsEntry::fromXmlElement` reads `<title>`, `<content>` and
`<id>`, which are Atom, not RSS — RSS has `<item>`, `<description>` and `<link>`. The fork's server
serves Atom. The name is the only thing that is wrong, and the port keeps the name because the config
key is a compatibility surface.

### The namespace would have made this silently empty

Qt's `elementsByTagName` matches the **local** name and ignores namespaces, which is why upstream
never had to think about the feed's `xmlns="http://www.w3.org/2005/Atom"`. `XDocument` is not
namespace-blind: `Descendants("entry")` on that document finds **nothing**.

Nothing throws. Zero entries is a legitimate state — it is what "No news available." means — so this
would have shipped as a toolbar that never showed a headline and a window nobody could open, with no
error anywhere to explain it. The parser matches on `LocalName` throughout, and a test pins the count
at 23 against the real capture.

### The fork's feed carries servers, and something writes them into your game

`<serverlistentry>` is not an Atom element. It is this fork's own extension, and the current feed
holds two:

```
extremelauncher.extremecraft.net
extremelauncher.strongcraft.org
```

Upstream's `NewsChecker` collects them into a global `serverList`, and `CreateGameFolders.cpp` — a
block the fork's own source marks `// uncommited - start` — merges them into **every instance's
`servers.dat` at every launch**. So the news feed can put entries in the player's multiplayer list.

The parser reads them and `NewsChecker` exposes them. **Nothing consumes them yet**, deliberately:
writing `servers.dat` needs its own wave, and there is a real bug waiting in that code —
`parseServersDat` returns null on any exception, and the code then writes a fresh list containing only
the fork's servers, losing everything the player had. When that wave happens it must refuse to write a
list it failed to read.

### Avalonia has no QTextBrowser

An entry's `<content>` is HTML, and upstream hands it to a rich-text control. There is no such control
here, so the choice was: show the markup raw (unreadable), flatten to text (this feed is *mostly*
headings and lists — flattening loses the structure entirely), or lay it out. `NewsHtml` does the
third, turning markup into blocks of styled runs.

Built against **the feed that exists**, which was surveyed first rather than guessed at. All 23
articles use exactly:

```
p (128)   li (29)   strong (28)   ol (4)   ul (3)   em (2)
&rsquo;   &ndash;   &nbsp;   &amp;
```

No anchors, images, tables or `<br>` anywhere. Those six are handled properly, a few neighbours
(`a`, `br`, `h1`–`h3`) because the next post might use them, and everything else is dropped —
formatting lost, text kept. The rule is: **never show a user angle brackets, and never lose the
words**.

Three details that only real data produces:

- **`<p>&nbsp;</p>` is a spacer.** Decoded, a non-breaking space is U+00A0, which is not whitespace to
  `IsNullOrWhiteSpace` — so without converting it, every one of those renders as a blank row with no
  text and no explanation.
- **Entities are decoded once, not twice.** The content is escaped twice over (`&amp;nbsp;` in the XML
  is `&nbsp;` after the XML parse). A second pass over decoded text would turn a post *writing about*
  `&lt;p&gt;` into a real tag and swallow it.
- **A `<` only starts a tag when a name follows.** This is the browser rule, and the first version did
  not have it: `<p>5 < 6 and that is the end</p>` scanned to the next `>` — the closing `</p>` — and
  ate the rest of the paragraph. One unescaped character in a hand-written post would have silently
  dropped an article body.

### A bug the async shape produced, caught by a test that could not have been guessed

`NewsChecker.ReloadAsync` publishes its in-flight task so a second call returns the first instead of
fetching twice. The first version stored it in a field and returned the field:

```csharp
_running = signal.Task;
_ = LoadAsync(signal);
return _running;      // <- null, sometimes
```

An async method runs **synchronously to its first await**, and an `HttpClient` over a handler that
answers immediately never yields at all. The whole load — fetch, parse, `finally` — therefore finished
*inside* the `_ = LoadAsync(...)` line, nulling `_running` (the lock is reentrant, so it did not even
block), and the method returned null. The caller awaited null and got a `NullReferenceException` with
no frames in it.

Only the stub handler triggers it. A real network never completes that fast, so this would have been
a crash that appeared under some future faster transport, or in a cache hit, and never on the machine
it was written on. Returning the local fixes it.

### Small things upstream drops

- **The window keys its entries by title.** `NewsDialog` holds a `QMap<QString, NewsEntryPtr>` and
  looks the article up by the text of the selected row, so two posts called "Hotfix" collide and one
  becomes unreachable. Here the selection *is* the entry.
- **The error string is never shown.** `updateNewsLabel` ignores `getLastLoadErrorMsg()` entirely, so
  a feed that is down and a feed that is empty look identical on the toolbar. Here it goes on the
  tooltip, including the case where a *refresh* failed and the headline on screen is stale.
- **`Launcher_NEWS_OPEN_URL`** is defined in CMake, described as "URL that gets opened when the user
  clicks 'More News'", and read by nothing at all. Not carried over.
- **Clicking the headline collapses the article list**, which upstream does (`newsButtonClicked` calls
  `toggleArticleList` before `exec`) and which is easy to drop: clicking one story means "show me that
  story", not "show me the index".

### Proved against the live feed

A probe ran the real URL through the real checker, view model and formatter:

```
feed: https://extremelauncher.net/_api/news.xml
loaded in 420 ms
toolbar label: "ExtremeLauncher 5.1.0.7"
clickable: True
entries: 23
server list from the feed: extremelauncher.extremecraft.net, extremelauncher.strongcraft.org

  [Paragraph B       ] Choose the Mod Loader Version You Want
  [Paragraph .       ] Installing mod loaders is now more flexible. Instead of automatically select...
  [Paragraph B       ] Supported installers
  [Bullet    .       ] • OptiFine
  ...
  [Numbered  .       ] 1. Open the Minecraft versions list.
  [Numbered  .B.     ] 4. Choose the loader version and click Install.

  23 articles, 0 problems
  block kinds: Paragraph=124, Bullet=6, Numbered=4
```

Every article produced blocks, none came out empty, and no `<` or undecoded entity reached the text.
The launcher itself was then started and logged `Loaded 23 news entries.`

The probe also found a rough edge worth fixing: asking the real server for a path that does not exist
answers **200 with a body that is not XML**, and the parse error read *"Root element is missing. at
line 0, column 0."* — a position in a document nobody has, offered to somebody with nothing to open.
That case now says what happened instead of where.

### Still not proved

- **Nobody has looked at the news window.** The article body is the only part of any window in this
  port built in code rather than by a binding, and the headless tests prove the runs, the weights and
  the marker column exist — not that a 124-block article is pleasant to read, or that the 240-pixel
  list is wide enough for real titles.
- **No feed with an anchor or an image has been rendered.** Links are parsed and carry their href, and
  nothing in the current feed uses one, so that path has run only against invented markup. Images are
  dropped entirely and no post has had one.
- **The feed is fetched once, at startup, and never again.** Upstream is the same. A launcher left
  open for a week shows a week-old headline.
- **The server list goes nowhere.** It is parsed and exposed and nothing writes it, which is
  deliberate — see above — but it means this wave ports the *reading* of a fork behaviour whose
  *writing* half is still missing.
- **No news entry has ever been clicked through to its post.** `OpenLinkAsync` goes through the same
  opener as the help menu, which has never opened a real browser either.

## Wave 35 — writing servers.dat, and the two times it must refuse

The Servers page could read the multiplayer list and say, in a tooltip, that this launcher did not
edit it. Now it adds, removes, reorders and edits, which is what upstream's `ServersPage` does — and
most of the work went into the cases where it *does not* write.

### Why this file is different

`servers.dat` holds the one thing in an instance that cannot be reconstructed. A world can be
restored from a backup, a mod re-downloaded, a version re-resolved. Nobody remembers the address of
the server they joined once in 2019. So the writer is the most dangerous thing in this port so far,
and two guards matter more than the whole feature:

**1. Never write a list that was not read.** `ServerList.Load` returns an empty list for a file that
will not parse — right for showing a page, catastrophic for saving one, because it turns *"I could
not read your forty servers"* into *"you now have none"*. A new `TryLoad` keeps the distinction that
`Load` throws away, and the page refuses to edit or save when the read failed, saying so on screen.

Upstream has exactly this guard as a bool on its model, and its log line is the clearest statement of
it anywhere in that file:

> `Server list should never save if it didn't successfully load`

A **missing** file counts as loaded, deliberately: an instance that has never been played has no
`servers.dat`, and that is an empty list rather than a failure — adding the first server to it has to
work.

**2. Locked while the game is running.** The game reads `servers.dat` at startup and **rewrites it
whole on exit**. An edit made while it is up is not merged; it is replaced, silently, and the player
has no reason to suspect it. Upstream locks its model from `runningStateChanged` and so does this,
through a new `ILocksWhileRunning` that the instance window drives from the launch coordinator —
including for a page added to a window whose game is *already* running, which the window can be
opened on at any time.

### The field set is smaller than the reader accepts

`Server::serialize` writes `name` and `ip` always, `icon` only when there is one, and `acceptTextures`
only when it is not "ask". Both omissions are load-bearing:

- an empty `icon` string is legal NBT and the game would try to decode it as a PNG;
- **`acceptTextures: 0` does not mean "ask", it means "never"**. Writing a byte for every server
  would answer, on the player's behalf, a question they have never been asked — for every server in
  the list, the first time they touched this page.

The icon is also carried *through* the view model without being shown or edited, for the same class
of reason: the game writes it after connecting once, and a row that dropped it would strip every icon
in the list off the first time somebody renamed a server. Visible only the next time the multiplayer
screen was opened.

### Proved against two implementations that are not this one

A round trip through this port's own reader proves only that the reader and writer agree with each
other. So the file the launcher wrote was checked twice from outside:

**An NBT reader written from the format spec**, sharing no code with the port:

```
185 bytes
first bytes: 0a 00 00 09 00 07 73 65
root tag: compound
root name: '' (must be empty)
root keys: ['servers']
consumed the whole file exactly
3 servers:
  'Hypixel' at 'mc.hypixel.net'  fields=['ip', 'name']
  'Ünicode Сервер' at 'unicode.example.invalid:25566'  fields=['acceptTextures', 'ip', 'name']
  'Asked' at 'asked.example.invalid'  fields=['ip', 'name']
```

Uncompressed, unnamed root compound, whole file consumed with nothing trailing, modified UTF-8 intact
through Cyrillic, and `acceptTextures` present on exactly the one server that answered.

**And `nbtlib`, a third-party library**, in both directions — it read the launcher's file with the
same result, then wrote one of its own that the launcher read back and rewrote losslessly:

```
TryLoad returned True, 2 servers:
  "Written by nbtlib" at "nbtlib.example.invalid:25565" textures=Never
  "Second" at "second.example.invalid" textures=Prompt
```

Note the first row: `acceptTextures: 0` written by a foreign tool comes back as **Never**, not as
Prompt — the three-state distinction survives a foreign file, which is the case a two-state reader
would have quietly got wrong.

### Smaller decisions

- **Add inserts after the selection**, not at the end. Upstream's `addEmptyRow(currentServer + 1)`,
  so somebody grouping servers can put one next to its neighbours.
- **Remove selects whatever took its place**, so a run of removals does not need a click between each.
- **Remove asks first**, destructively flagged. Upstream's own wording is right about why.
- **The editor sits under the list**, not in it: a list of text boxes is unreadable at a glance, and
  reading the list is the whole reason to open the page.
- **Selecting a row is not an edit.** Otherwise the window asks about unsaved changes for somebody who
  only looked.
- **Select-by-address takes the first match only.** Two rows can hold the same address — trivially,
  two new ones — and selecting both leaves `Selected` returning one row while another is highlighted.
- **The write goes through a temporary and is moved into place.** A half-written `servers.dat` reads
  as an empty list: a silent total loss, which is worth one extra syscall to avoid.

### Still not proved

- **No Minecraft client has read a file this port wrote.** Two independent NBT implementations agree
  on the bytes, which is strong, but the game itself is the only authority on whether it will show
  these servers — and it has not been asked.
- **Nobody has joined a server from this page.** Upstream has a Join action that launches straight
  into an address; this has no equivalent, so the page edits the list and cannot use it.
- **The icon is never shown.** It round-trips, and the list is text-only, so the 64-pixel server icons
  upstream draws are absent.
- **No conflict with a running game has been observed.** The lock is asserted through the view model
  and the window's launch coordinator; nothing has actually started a game, edited the page and
  watched the write be refused.
- ~~**Nothing writes the fork's news-feed server list yet.**~~ *Done in wave 36, using `TryLoad`
  exactly as this entry required.*

## Wave 36 — the fork's servers, and the bug in the patch that adds them

The other half of wave 34's finding. `CreateGameFolders.cpp` merges the news feed's
`<serverlistentry>` addresses into **every instance's `servers.dat`, at every launch** — so the
launcher puts entries in the player's multiplayer list, and what it puts there comes off the network.
Upstream's own source brackets the block with `// uncommited - start` and `// uncommited - end`.

Ported, because it is this fork's behaviour and parity is the goal. Three things are different.

### It refuses to write a list it could not read

Upstream's `parseServersDat` returns null on **any** exception — a `servers.dat` in a shape it does
not expect, a locked file, anything — and the code carries straight on and writes a fresh list
containing only the fork's servers. That is a silent, total replacement of the player's multiplayer
list, on launch, with no error anywhere.

`ServerList.TryLoad` from wave 35 is exactly the guard for it. Verified by reintroducing upstream's
version and watching `AListThatWillNotParseIsLeftEXACTLYAsItIs` fail: the fixture's file really is
overwritten without it.

> Adding an advertisement is not worth losing somebody's server list over.

### It does not reorder what is already there

Upstream appends the new servers **first** and re-adds the player's own afterwards, so the first
launch after this feature silently rearranges a multiplayer list somebody has arranged. Here the
player's entries keep their order and anything new goes after them.

### It says what it did

Upstream's version is invisible: it edits the file and logs nothing. This logs at startup —

```
[08:04:38] [INFO] Loaded 23 news entries.
[08:04:38] [INFO] The news feed lists 2 server(s), which will be added to each instance's
                  multiplayer list when it launches: extremelauncher.extremecraft.net,
                  extremelauncher.strongcraft.org
```

— and again in the launch log when a merge actually happens, which is the log people paste into bug
reports.

### Smaller decisions

- **Matched on address**, which is what upstream compares, so a player who renamed one of these keeps
  their name and does not get a duplicate on every launch.
- **Nothing is written when there is nothing to add**, including the common case of a launch before
  the news has arrived. Rewriting `servers.dat` for no reason is precisely what wave 35 was careful
  about.
- **A launch never waits on the feed.** `LauncherService.FeedServers` is settable and starts empty,
  which is upstream's shape too (a global in `Application.h`); an empty list simply means the news has
  not landed yet and the next launch will do it.
- **Failure here is never fatal.** A launch that could not be started because an advertisement could
  not be written would be absurd.

### Worth a decision that is not mine

This is the fork's own product choice and it is ported faithfully, but it is worth stating plainly:
**the launcher adds two servers of its own to every instance a player launches.** There is no way to
turn it off, because upstream has none. If an off switch is wanted, the setting hook is one line in
`GlobalSettings.Register` and one check in `CreateGameFolders`.

### Still not proved

- **No game has read a merged list.** Same gap as wave 35: two independent NBT implementations agree
  on the bytes, and Minecraft has never been asked.
- **The merge has never run during a real launch.** The step is tested directly and the app logs the
  intent at startup; no instance has actually been started with a feed loaded and had its list
  changed.

## Wave 37 — uploading a log, and one of the four services being dead

The single most useful thing somebody asking for help can do is hand over their log, and the single
most annoying way to do it is paste 4,000 lines into a chat window. Every Minecraft support channel
runs on paste links, and this port had a Copy button and nothing else.

`PasteUpload` ports upstream's four services, `LogUpload` ports the careful flow around them, and the
Upload button on the instance's log page is wired to both.

### Four services that agree about nothing

Not one field is shared between them. That is the whole complexity of the file:

| Service | Request | Where the link comes from |
|---|---|---|
| 0x0.st | multipart form upload | the response body, as plain text |
| hastebin | the raw text as the whole body | built from `key` in the JSON |
| paste.gg | a JSON envelope with a files array | built from `result.id` |
| mclo.gs | form-urlencoded `content=` | `url` in the JSON — **behind a success flag** |

**mclo.gs answers 200 with `"success": false`** when it rejects a paste. Trusting the status code
alone — the obvious way to write this — hands somebody a link to nothing and tells them it worked.
Its live `/1/limits` endpoint says why that path is reachable: 10 MB and 25,000 lines are the caps,
and a modded 1.20 crash log gets close.

It is also the right default, and upstream's: it parses Minecraft logs, folds the stack traces and
highlights known errors, so the person helping gets more than raw text.

### paste.gg is gone

Checked against the real internet while writing the tests: **`paste.gg` has no DNS record at all** —
not a 404, no domain. `0x0.st`, `hst.sh` and `api.mclo.gs` all answer. Upstream still offers it as one
of four equal choices, so anyone picking it gets a resolver failure that reads like their own network
being broken.

It is handled without lying and without breaking anything:

- **Still in the list**, because the enum's numbers are on-disk format — a config out there says
  `PastebinType=2`, and removing the entry would renumber mclo.gs.
- **Still offered in the picker**, because vanishing from it would silently switch anyone who had it
  selected to a service they never chose.
- **Labelled `paste.gg (no longer available)`**, which is the whole fix.

### Ask before publishing somebody's log

A Minecraft log is not neutral text: it carries the player's user name, their home directory, the mods
they run, sometimes a server address, and on a bad day a session token. Uploading it is irreversible —
the link is public and most of these services have no delete.

Upstream asks first and names the host, and that shape is kept and sharpened:

- **The question names the file and the host**, not the URL. "Upload to `https://api.mclo.gs/1/log`?"
  invites nobody to think; "upload latest.log to api.mclo.gs?" does.
- **It says what a log contains** rather than upstream's "you should double-check for personal
  information" — "your user name, your file paths, the servers you have joined" is concrete where
  "personal information" is abstract — and that it **cannot be taken back**.
- **The affirmative is flagged destructive**, so it is not the button a stray Return lands on. Not a
  delete, but the same class of irreversible, and this port has that rule everywhere else.
- **No dialog service means no upload.** Guarded explicitly, because publishing somebody's log because
  a window was unavailable is not a failure anybody could recover from.

The flow lives in one place, shared with the launch console, so the careful part cannot be
reimplemented slightly worse in the second caller.

### A setting stored as an integer, and a migration off a dead one

`PastebinType` is stored as **the raw numeric value of upstream's enum**. That is a poor way to store
a setting and it is not this port's to change — the config file is shared, so the numbers are format.

Upstream's own comment on the migration block is *"HACK: This code feels so stupid is there a less
stupid way of doing this?"*. It is doing two real jobs and both are kept:

- **`PastebinURL` → `PastebinCustomAPIBase`.** The old single-service setting, from when 0x0.st was
  the only option. A **non-default** value means somebody deliberately pointed the launcher at their
  own instance, so it is carried over; the default value means they never expressed a preference, so
  they get mclo.gs like everybody else. Proved on a real app start:

  ```
  before:  PastebinURL=https://paste.mycompany.invalid
  after:   PastebinCustomAPIBase=https://paste.mycompany.invalid
           PastebinType=0
  ```

- **An out-of-range type resets both settings**, because a custom base belongs to a service and means
  nothing without one.

> **`GetInt` hides a parse failure behind the default.** My first version range-checked
> `GetInt("PastebinType", 3)`, so `PastebinType=banana` read as 3, passed the check, and left a custom
> API base attached to a service nobody chose. Upstream distinguishes the two through `toInt(&ok)`,
> and a `[Theory]` case caught it. The port now tells "not a number" apart from "a number out of
> range".

### The coverage guard, and an exemption that had to be earned

`PastebinURL` is registered only so it can be read once and erased, which broke the wave-16 guard that
every registered setting has an editor — correctly, since the guard cannot tell "forgotten" from
"deliberately hidden".

Rather than weaken it, there is now a **documented exemption list** on the view model, with
`PastebinURL` as its only entry and a reason attached. A second test pins the list's contents, so the
easy fix for a failing coverage test — adding a key here — is a deliberate, reviewable act. Verified
by registering a `SomeForgottenSetting` and watching both guards still fail on it.

### Still not proved

- **No real paste has ever been created.** The request and response shapes are pinned from upstream's
  parser through a stub handler; the three live services were reached read-only (`/1/limits` on
  mclo.gs, a `GET` on the other two) to confirm the hosts and paths, but nothing has been posted to
  any of them. Publishing test junk to a public paste bin on every test run would be rude, and doing
  it once by hand is a decision worth making deliberately rather than in passing.
- **hastebin's response shape is unverified against the live service.** `hst.sh` answers, but whether
  it still returns `{"key": …}` is taken from upstream's parser.
- ~~**The launch console has no Upload button yet.**~~ *Done in wave 38.*
- **The confirmation dialog has never been seen by a human**, like every other window in this port.

## Wave 38 — an instance remembering it was a modpack

Six `ManagedPack*` settings were registered in the instance-settings wave with the note *"populated by
the modplatform wave"*. **The modplatform wave never populated them.** So every pack this port has
ever imported forgot it had been a pack the moment the import finished — the three instances sitting
in the dev build's folder from earlier waves have no `ManagedPack` line at all.

The gap matters because the two facts answer different questions. `Minecraft 26.2, Fabric 0.19.3` is
what the instance *is*; `Fabulously Optimized 14.0.0-beta.6` is what the person actually installed,
and the only name they know it by.

### What the import writes now

`ModrinthImportTask` sets the six fields, ported from `ModrinthInstanceCreationTask.cpp:237`. The
pack's own name goes in, **not the instance's** — somebody who renamed their instance to `modded 1.20`
still wants to know what is underneath it.

Proved by importing the real pack from Modrinth:

```
--- instance.cfg ---
ManagedPack=true
ManagedPackID=
ManagedPackName=Fabulously Optimized
ManagedPackType=modrinth
ManagedPackVersionID=
ManagedPackVersionName=14.0.0-beta.6
name=Fabulously Optimized
```

### The empty id is the interesting field

A `.mrpack` on disk **does not carry its own project id**. Only a launcher's pack browser knows that,
and this port has no pack browser — so a file-imported instance has a name and a version and nothing
to look a newer version up by.

Upstream is confused about this. `ModrinthInstanceCreationTask` carries the comment

> `// Don't add managed info to packs without an ID (most likely imported from ZIP)`

and then its `else` branch calls `setManagedPack("modrinth", "", name(), "", "")` **anyway**, which
sets `ManagedPack = true`. Since `ManagedPackPage::shouldDisplay()` is just `isManagedPack()`, the
managed-pack page appears for a ZIP import and has nothing to look the pack up by.

So the port splits the one flag into two questions:

- `IsManagedPack` — did this come from a pack? Used for the provenance line.
- `CanCheckForPackUpdates` — is there an id to ask about? False for every import this port can
  currently do.

The version page shows the first and, when the second is false, says why rather than leaving somebody
waiting for an update button that is never going to appear:

> This pack was imported from a file, so it carries no link back to the platform and cannot be checked
> for newer versions.

### A byte order mark, found by a test fixture

The `.mrpack` fixture in the new tests is written with `Encoding.UTF8`, which emits a BOM, and the
import failed:

```
modrinth.index.json: Error parsing JSON: '0xEF' is an invalid start of a value.
```

The tempting read is "my fixture is wrong". It is not: **`QJsonDocument::fromJson` skips a BOM and
`JsonNode.Parse` refuses one**, so a pack upstream imports happily was unreadable here. Hand-authored
manifests really do have them — the inherited `pack.mcmeta` fixture from wave 0 starts `EF BB BF`
because whoever made it used an editor that adds one — and the person who wrote the file cannot see
the difference or guess why one launcher calls their pack corrupt.

Fixed in `Json.RequireDocument`, so every caller in the port benefits, not just this one.

> This does **not** contradict the export wave's finding that a BOM this port *wrote* was a bug. The
> writer must not emit one and the reader must not care; those are the same rule from the two ends.
> Lenient in, strict out.

### Also this wave

The launch console got its **Upload log** button, which wave 37 had left for later. It is the more
useful of the two: this is the log of the launch that just went wrong, which is the one somebody has
open when they decide to go and ask for help. It shares `LogUpload`, so the careful part — asking
first, naming the host, saying what a log contains — is not written twice.

### Still not proved

- ~~**No pack has ever been updated**, and nothing can be until something knows a project id.~~ *The
  browser landed in wave 39 and `CanCheckForPackUpdates` is now reachable; the update itself is still
  not written.*
- **The provenance line has never been seen by a human.** It renders in the headless tree; whether it
  reads well above the component list is unknown.
- **Only Modrinth records provenance**, because only Modrinth packs import. CurseForge, Technic, FTB
  and ATLauncher are parsed and have no creation task, so `ManagedPackType` can only ever say
  `modrinth` today.

## Wave 39 — finding a modpack without leaving the launcher

Until now the only way to get a modpack in was to find it in a web browser, download the `.mrpack` by
hand, and import the file. That works — and loses the one thing that matters afterwards, because a
file carries no project id and nothing can ever tell you a newer version exists.

`CanCheckForPackUpdates` has been in the code since wave 38 and had never once been true.

### Most of it already existed

The API layer had `ResourceType.Modpack` and `CreateFacets` already emitted `project_type:modpack`,
from the wave that ported the search. A probe through the port's own `ResourceSearchSource` — no new
code — went straight through to the live service:

```
results: 25
  1KVo5zza   Fabulously Optimized      Beautiful graphics, speedy performance and fam
loading versions for Fabulously Optimized (1KVo5zza) ...
versions: 463
  version=14.0.0-beta.6 for 26.2
    fileName = Fabulously.Optimized-v14.0.0-beta.6.mrpack
```

So the wave was download, install, and a window — not a new platform integration.

### 463 versions is the actual design problem

That probe also settled the hardest question in the window. **Fabulously Optimized's four newest
releases are all betas for a Minecraft snapshot**, so an unfiltered version list opens on releases
almost nobody wants, and the stable build somebody is looking for is well down the page.

- **"Stable releases only" is ticked by default.** A checkbox rather than a hard rule, because pack
  authors do ship long-lived betas and somebody hunting one should not conclude the launcher is
  hiding it.
- **A pack with no stable release shows everything anyway.** An empty list under a ticked box reads
  as "this pack has no versions", which is a much more alarming statement than "they are all betas".
- **The row says which Minecraft it is for.** `14.0.0-beta.6` tells nobody that; the pack's own
  numbering has no relationship to the game's, and which Minecraft it runs on is the only question
  most people are actually asking.
- **The newest surviving version is picked for you**, and a version stays picked across a filter
  change when it survives one — otherwise ticking the box to peek at betas silently moves a choice
  somebody had already made.

### One install path, not two

`ModpackInstallTask` downloads the `.mrpack` to a temp file and hands it to **the same
`ModrinthImportTask` a file import uses**. The awkward parts — component resolution, overrides,
optional files, the byte order mark from wave 38 — stay solved once and cannot drift apart.

The only difference is what it passes in: `managedId` and `managedVersionId`. That pair is the entire
reason the browser exists as a separate path.

The temp download is deleted in a `finally`. Left behind it would be a 170 MB file nothing ever looks
at again, and on a *failed* install one the user never asked for and could not find.

### Proved end to end against the live service

Searched, picked the newest stable release the way the window's default does, and installed it:

```
pack: Fabulously Optimized (1KVo5zza)
versions: 463
chosen:  13.3.0  [Release]  mc=26.1.2
ok: True
downloaded: 48 files

--- what the instance knows ---
IsManagedPack:      True
ManagedPackName:    Fabulously Optimized
ManagedPackID:      1KVo5zza
ManagedPackVersion: 13.3.0
ManagedPackVerID:   Jng8txuM
CanCheckForUpdates: True

mods on disk: 46
temp .mrpack files left behind: 0
```

`CanCheckForUpdates: True` for the first time in this port. The release filter did its job too —
left to itself the list would have opened on `14.0.0-beta.6` for a snapshot, and this picked `13.3.0`
for 26.1.2.

### Still not proved

- ~~**Nothing checks for an update yet.**~~ *The check landed in wave 40. Acting on the answer --
  reconciling a new pack against files the player has since added, changed and deleted -- is still
  the harder half and is not written.*
- **`ShouldOverride` is always false** — installing always makes a new instance. Updating in place is
  what that flag is for, and claiming to support it before the reconciliation exists would be worse
  than not offering it.
- **The installed pack has never been launched.** 46 mods are on disk and `mmc-pack.json` resolved to
  Minecraft 26.1.2 with Fabric 0.19.3; no game has started from it.
- **Only Modrinth.** The window has no provider picker, because CurseForge modpack installs need the
  Flame API and a key this build does not have — and its packs name files by project and file id
  rather than by URL, so the download path would be different too.
- **The window has never been seen by a human**, like every other window in this port.

## Wave 40 — is there a newer version of this pack?

Answerable at all only since wave 39, because it needs the project id that browsing records and
importing a file cannot. The button appears on the Version page for a pack that has one, and nowhere
else.

### The comparison is by position, not by parsing version strings

A pack's numbering is whatever its author felt like — `5.9.2`, `v14.0.0-beta.6`, `1.20.1-4`,
`Release 12`. Ordering those with a version comparer means inventing a rule the author never agreed
to. Modrinth already returns a project's versions newest-first, so **position in the list is the
answer**, and a test asserts it with names that sort the wrong way round on purpose.

### What NOT to offer is the whole decision

Three rules, each stopping a specific piece of bad advice:

- **A beta is not an update for somebody on a stable release.** The live data is why: Fabulously
  Optimized's five newest versions are all betas for a Minecraft snapshot. Calling those an update
  would push a player on 13.3.0 onto a pre-release build of a game version they do not have. Somebody
  *already* on a beta opted into that channel, so for them anything newer counts.
- **A withdrawn current version is inconclusive**, not "up to date" and not "update available".
  Neither guess is honest — the installed build might be older than everything or newer than
  everything.
- **A move to a different Minecraft version is still offered, but said out loud.** This is the one
  place where being conservative would be wrong: a pack moving to a new Minecraft version *is* the
  update people wait for. But it changes what the instance is, and their worlds and other mods are on
  the old one, so it must not slip past inside a one-line "update available".

Overlapping Minecraft support does not count as a move — a pack that supported 1.20.1 and 1.20.4 and
now supports only 1.20.4 has not moved for somebody on 1.20.4, and warning them would be noise.

### Checked against the live version list

All five branches, against Fabulously Optimized's real 463 versions:

```
[on the newest stable release]
  You are on the newest release (13.3.0 for 26.1.2). There are 5 newer pre-release
  versions, which this does not offer.

[on the newest version of all]
  This is the newest version of the pack (14.0.0-beta.6 for 26.2).

[on the previous stable release]
  13.3.0 for 26.1.2 is available (you have 13.2.2 for 26.1.2).

[twelve releases behind]
  13.3.0 for 26.1.2 is available (you have 12.2.1 for 1.21.11). Note that it is for
  Minecraft 26.1.2 rather than 1.21.11.

[on a version that has been withdrawn]
  The installed version (9.9.9) is no longer listed on the platform, so there is nothing
  to compare against. The newest available is 14.0.0-beta.6 for 26.2.
```

The first case is the one the rules exist for, and the fourth shows the Minecraft-move warning firing
on real data rather than on a fixture built to trigger it.

### Where the split falls

`PackUpdateCheck.Evaluate` takes a version list and returns a verdict — no network, no settings
object, nothing to stub. Deciding what counts as an update is the part worth testing exhaustively,
and it is now fifteen tests over a pure function. `AppPackUpdateChecker` is the part that cannot be
tested without a network, and it is nine lines.

The check runs **on a button, not on opening the page**. Upstream's managed-pack page fetches as soon
as you look at it; the Version page here is mostly read — people open it for the component list — and
it should not cost a request every time.

### Still not proved

- **Nothing installs the update.** The answer is a sentence; acting on it means reconciling a new
  pack against files the player has added, changed and deleted since, which is the harder half and
  the reason `ShouldOverride` is still always false.
- **No pack has been checked from inside the running app.** The rule is proved against the live list
  through a probe, and the button is wired and covered headlessly; nobody has pressed it.
- **Only Modrinth.** A CurseForge pack would need the Flame API and a key this build does not have.

## Wave 41 — the ledger, and what an update would delete

The hard half of updating a modpack is not downloading the new version. It is working out which of
the files already in the folder belong to the pack and may be replaced, and which belong to the
player and must not be touched.

**Nothing about a file on disk says which it is.** Upstream solves this by keeping a record of what
the install put there, in `<instance>/mrpack/`:

```
modrinth.index.json    the manifest, to diff against later
overrides.txt          every path the pack's "overrides" folder wrote
client-overrides.txt   the same for "client-overrides"
```

**This port never wrote any of it** — the same shape of gap as the `ManagedPack` settings in wave 38,
found the same way: by trying to build the thing that needed it. `PackLedgerStore` writes it now, in
upstream's exact layout and file names, so an instance made here can be updated by Prism and the
other way round.

The manifest is **copied, not re-serialised**. Writing it back through this port's own model would
quietly drop any field the model does not know about, and the entire value of the ledger is being
able to compare against what the author actually published.

### No ledger, no plan

`PackUpdatePlanner.Create` refuses outright when there is no ledger:

> This instance has no record of which files came from the pack, so an update cannot tell them apart
> from files you added yourself. Installing the newer version as a separate instance is the safe way
> to move.

A **divergence from upstream**, which carries on after a *"this may cause some of the files to be
duplicated"* warning. That undersells it: without the old manifest it cannot remove the old version's
mods at all, so the instance ends up running two copies of half its mod list — a crash, and a
confusing one. Every pack this launcher installed before this wave lands here, and so does one
imported from a file.

A ledger that will not parse is treated as **absent**, not as an empty pack. The difference matters: an
empty pack would plan the removal of every file the instance has.

### Matching is by hash

Upstream's rule and the right one. Pack authors re-path and rename files between releases constantly —
a version bump inside a filename is the normal case — so matching on paths would re-download and
re-delete the entire mod list every single update. A file with no hash at all is treated as unmatched,
which errs towards downloading it again rather than towards removing something.

### Proved by installing one real release and planning the next

```
installing the OLDER release: 13.2.2 for 26.1.2
installed, 47 files

ledger found:      True
files recorded:    47
overrides recorded:46

planning the update to 13.3.0 for 26.1.2 ...
summary:   This would download 26 files and remove 71 files. 22 files would be left alone.

a few that would be downloaded:      a few that would be removed:
    BetterGrassify-1.8.7                 BetterGrassify-1.8.6
    ConfigManager-fabric-26.1_26.2-1.1.3 ConfigManager-fabric-26.1-1.1.2
    CrashAssistant-fabric-26.1-1.11.11   CrashAssistant-fabric-26.1-1.11.9
```

The pairs are the point: each removal is the older build of a mod whose newer build is in the
download list, which is the hash matching doing its job. The arithmetic checks out too — 47 old files
minus 22 unchanged is 25 replaced, plus 46 override paths, is 71.

### The uncomfortable rule, kept

Every override the old version wrote is removed. An override is usually a config file and the player
may well have edited it since — but the alternative is worse in a way that is harder to see: a pack
that changes a config's format leaves the instance running the old file, and *that* failure looks
nothing like "my edits were kept".

The mitigation is that the plan **says how many**, and says it alongside the number left alone:
"remove 71 files" on its own sounds like most of the instance is going; "22 files would be left alone"
is what makes it readable. Nothing here touches the disk — it produces a plan, and something else has
to show it to somebody first. That split is the whole design.

### Still not proved

- ~~**Nothing carries out a plan.**~~ *Done in wave 42, download-before-delete.*
- **No plan has been shown to a human.** It is a record with a summary sentence and no window.
- **The override list has never been read back by upstream.** The format is upstream's and the file
  names match, but no Prism build has been pointed at an instance this port installed.
- **A pack with a huge override tree is untested.** Fabulously Optimized records 46 override paths;
  a kitchen-sink pack records thousands, and the list is read into memory whole.

## Wave 42 — carrying out the update

Wave 41 worked out what an update would do. This does it, in a folder somebody has been playing in.

### Download before delete

The order is the whole design:

```
1. fetch everything the new version needs, into a temporary folder
2. only then remove what the old version left
3. move the fetched files into place
4. lay down the new overrides
5. write the new ledger and version
```

A network that dies half way through step 1 leaves the instance **exactly as it was and still
playable**. The reverse order leaves somebody with an instance missing half its mods and no way back.
It costs disk — the new files exist twice for the length of step 3 — and that is a good trade.

The failure message says so, because "update failed" otherwise leaves somebody wondering whether
their instance is now broken:

> Could not download the new version's files (…). Nothing has been changed — the instance is exactly
> as it was.

Proved by mutation: swapping steps 1 and 2 makes
`AFailedDownloadLeavesTheInstanceExactlyAsItWas` fail, and the fixture's mods really are gone.

### The delete side of upstream bug #12

Every path is validated **including the ones being deleted**. The removal list comes out of a ledger
file — text on disk that another launcher wrote, or that somebody edited — so a path of
`../../something` must not be followed out of the instance.

This is not theoretical. Removing the guard and re-running the tests **actually deletes a file
outside the instance folder**; the test that catches it is the one that writes
`../../not-part-of-the-instance.txt` into `overrides.txt` and then asserts the file still exists.

The recorded version is written **last**, so a failure anywhere above leaves the instance still
claiming to be the old version — which is both true of most of its files and the state a retry needs.

### The number a person reads before agreeing

Running a real update is what caught this. The first working version reported:

```
This would download 26 files and remove 71 files. 22 files would be left alone.
```

Of those 71, **46 were the pack's own config files, about to be re-written a second later**. Anybody
reading that before agreeing would reasonably think they were losing something. Worse, re-applying
the version already installed reported "remove 47 files" — for an operation that changes nothing.

So the plan now tells a **refresh** from a **loss**, by listing the new pack's override entries out of
the zip and checking which of the old ones survive:

```
This would download 26 files, refresh 44 of the pack's own config files and remove 27 files.
22 files would be left alone.
```

and re-applying the same version says only *"refresh 47 of the pack's own config files. 48 files
would be left alone."* — no removals at all, which is the truth.

Both groups are still deleted on disk. They are counted apart because **the numbers mean different
things to a person**, and the whole point of the summary is that somebody reads it first.

> A caller that cannot open the new pack gets the reassuring answer — everything counts as a refresh
> — so that answer has to be earned. `PrepareAsync` always supplies the list.

The three-clause sentence also needed fixing: a naive join gave "download 26 files and refresh 44
files and remove 27 files", which reads like a child listing things.

### Proved on a real pack, with a file of my own in the way

```
1. installing 13.2.2 for 26.1.2        → 45 mods on disk
2. added my-own-mod.jar and options.txt by hand
3. updating to 13.3.0 for 26.1.2
   downloaded: 26   removed: 71

--- afterwards ---
mods on disk:            47
my own mod survived:     True
my options.txt survived: fov:90
recorded version after:  13.3.0  (Jng8txuM)
ledger now says:         13.3.0, 48 files, 47 overrides
updating again:          This would refresh 47 of the pack's own config files.
```

The last line matters as much as the rest: the update rewrites the ledger, so a **second** update
diffs against the version actually installed. Without that, updating would work exactly once.

### Still not proved

- ~~**No human has been shown a plan.**~~ *Done in wave 43: a confirmation naming what would be
  deleted, and an Update button that names the version.*
- **The updated instance has never been launched.** 47 mods are on disk with the right bytes; no game
  has started from one.
- **An interrupted update has never been resumed.** Step 1 is safe to fail; a crash between steps 2
  and 3 leaves an instance missing files, and nothing detects or repairs that.
- **A locked file is stepped over, not retried.** Updating while the game is running will leave the
  old jar behind and say so in the status, which is the best available answer but not a good one.

## Wave 43 — putting the plan in front of somebody

Wave 42 ended with the feature correct and unusable: `PackUpdateTask` was deliberately split so the
plan could be shown before anything happened, and nothing did the showing. This is the join.

### The confirmation is the feature

An update deletes files in a folder somebody has been playing in, so the dialog is the part that
matters more than the task:

```
Updating Fabulously Optimized from 13.2.2 to 13.3.0.

This would download 26 files, refresh 44 of the pack's own config files and remove 27 files.
22 files would be left alone.

These files will be deleted:
    mods/BetterGrassify-1.8.6+fabric.26.1.2.jar
    …
    … and 19 more

The pack's own config files will be replaced with the new version's. If you have edited any of
these, your changes will be lost:
    config/modpack_defaults/config/fabrishot.properties
    … and 36 more

Anything you added yourself will be left alone.
```

Three decisions in that text:

- **Eight paths and a count**, not twenty-seven. A confirmation nobody reads is not a confirmation;
  a wall of paths is something people click past, half a dozen and a number is something they check.
- **The config warning is stated as a loss**, because for somebody who edited one it is. The refresh
  wording from wave 42 is right for the summary line and wrong here.
- **The last line is the one people actually want** — their own additions are in neither list because
  the pack never knew about them, and saying so is more reassuring than any count.

The affirmative is flagged destructive, so a stray Return cannot start it.

### The button only exists after a check said something

`Offered` holds what the last check found, and the button is bound to it rather than to a boolean:

- **It names the version** — "Update to 13.3.0", not "Update". Pressing it deletes files; nobody
  should have to click to find out what it means.
- **The offer is spent when taken.** Otherwise Update stays on screen pointing at the version now
  installed, and pressing it again diffs the pack against itself.
- **A failed update keeps the offer**, because a download that timed out is exactly when somebody
  wants to press it again.
- **Cancelling reports nothing.** Somebody who read the plan and decided against it has not had an
  error, and a status line saying so would suggest otherwise.
- **Opening another instance forgets it.** `LoadProvenance` clears the offer, so an answer checked
  for one instance can never be applied to a different one — the worst outcome this feature has
  available.

### Bound to one instance on purpose

Unlike the checker, which is one object for the whole launcher, `AppPackUpdater` is built per window
and holds that instance's paths. An updater that could be pointed at the wrong folder is a class of
bug this feature cannot afford, and constructing it per instance makes that impossible rather than
merely unlikely.

A build with no dialog service gets **the check but not the update**: reading is harmless and knowing
is useful, but deleting files without being able to ask is not something to fall back to.

### Still not proved

- **Nobody has pressed the button.** The flow is covered headlessly and the task is proved against a
  real pack, but the dialog has never been on a screen — so whether that message is readable at the
  size Avalonia gives it is unknown, and it is a long message.
- ~~**The version page does not reload after an update.**~~ *Fixed straight after: the page re-reads
  mmc-pack.json, and keeps showing what it had if that re-read fails -- a page that empties itself
  because of a bad read is worse than one briefly out of date.*
- **The updated instance has still never been launched.**
- **An update during a launch is not prevented.** The task steps over locked files and says so, which
  is the best available answer and not a good one; nothing stops somebody starting an update while
  the game is running.

## Wave 44 — something to look at while it works

Four things in this launcher took minutes and showed **nothing at all**: importing a modpack,
installing one from the browser, updating one, and downloading a Java runtime. A 170 MB download
behind a window that does not move is indistinguishable from a launcher that has hung — and the usual
response to that is to kill it, half way through writing files.

The last two waves made this worse by adding two more long downloads, so it was time.

### One window, three callers

`TaskProgressViewModel` plus `ProgressWindow`, wired into the pack importer, the pack browser and the
pack updater. One implementation rather than three, because the alternative is three progress bars
that behave slightly differently and the one that behaves worst is the one somebody hits first.

`ProgressWindow.RunAsync(owner, task, title)` is the whole API. It also owns the **thread
marshalling**, which is the part that would otherwise be got wrong in three places: a `LauncherTask`
raises its events on whatever thread it is running on, and touching a bound property from there is the
classic way to produce a crash that only happens on somebody else's machine. The adapter posts every
one to the UI thread.

### The edges are the design

- **Indeterminate until a total arrives.** A bar sitting at 0% for the twenty seconds before the
  first byte count looks like a stuck one, and a stuck bar is exactly what makes people kill a
  launcher mid-write.
- **The percentage is clamped.** A download that turns out smaller than announced must not report
  140%.
- **Cancel says "Stopping…" rather than closing.** Cancelling a download is not instant — the request
  in flight has to come back — and a window that vanished on the first press would invite somebody to
  start the same work again while the first is still winding down. The button also disables itself,
  so it cannot be pressed twice.
- **A task that cannot be aborted cannot be abandoned either.** Closing the window is refused while
  such a task runs, because closing it would leave the task writing files with nothing on screen,
  which is the exact situation this window exists to prevent.
- **A Cancel button is only there for work that can actually stop.** One that does nothing is worse
  than none, because somebody will press it and then press it again.
- **A failed task closes the window too.** A naive "close on success" leaves it up forever on the one
  occasion somebody most needs to read the reason.

### The bar being bound is the thing only a UI test can see

A `ProgressBar` wired to nothing renders perfectly and sits at zero — which looks exactly like the
hang this window exists to rule out, and every view model assertion still passes. Verified by
replacing the binding with a literal `0` and watching `TheBarFollowsTheTask` fail.

### The Java dialog got a smaller fix

It already showed a status line, so it gets an indeterminate bar rather than a nested dialog on top
of a dialog. Real per-file progress would mean threading the install task's numbers through the
service interface; a bar that is merely cycling still says the difference between working and hung,
and a Mojang runtime takes about three minutes.

### Still not proved

- **No progress window has been seen by a human.** It is covered headlessly and the bindings are
  proved by breaking them; whether 460 pixels is the right width, and whether the status line's
  reserved two rows are enough for the longest message, is unknown.
- ~~**Cancel has never stopped a real download.**~~ *Done in wave 45, which found a bug doing it: a
  cancellation was being reported as a download failure in three places.*
- **The Java install still has no real progress**, only motion.
- **Nothing shows progress for a launch.** That path has `BatchingProgressReporter` and the instance
  window's own log, which is a different and better answer -- but it means there are now two ways
  this launcher reports work, and they look nothing alike.

## Wave 45 — cancelling is not failing

Wave 44 ended with an honest note: *"Cancel has never stopped a real download."* Chasing that found a
bug in three places.

### NetJob reports a cancellation and a dead mirror the same way

Both come back as `false`. So the update task turned a cancellation into

> Could not download the new version's files (…). Nothing has been changed.

— which is **true and wrong**. Nothing had been changed, and nothing had gone wrong either: the person
pressed Cancel. Telling them their download failed invites them to go and look for a problem that does
not exist.

Found by writing the test, because from inside `FetchAsync` the two paths are identical; they differ
only in what the person on the other end believes happened. The fix is one line —
`cancellationToken.ThrowIfCancellationRequested()` before reporting the failure — in three places that
all had the same shape:

- `PackUpdateTask.FetchAsync`
- `ModrinthImportTask.DownloadFilesAsync`
- `InstanceStagingTask`, which wraps both and is what the dialogs actually report

### And then it must not raise a dialog either

With the state right, the three callers still popped an error box saying *"Could not install the
modpack — Aborted."* Somebody who pressed Cancel knows what happened. All three now check
`State == TaskState.AbortedByUser` and return quietly.

The updater says a little more than the others, on purpose:

> Cancelled. Nothing was changed.

Silence would leave somebody wondering whether it stopped before or after it started deleting things,
which is the one question a cancelled update raises.

### The destructive phase cannot be interrupted, and that is deliberate

Removing files, moving the new ones in, laying down overrides and rewriting the ledger are all
**synchronous** — there is no `await` between them for a token to be observed at. A cancellation point
in the middle of that sequence would produce the exact outcome the whole download-before-delete
ordering exists to avoid: an instance with its old files gone and its new ones not yet in place.

That was true by construction and is now true on purpose, with a test that cancels after the last
download has been served and asserts the update still completes.

### Proved by cancelling a real download

```
installing 13.2.2 → 45 mods, plus one of my own
starting the update to 13.3.0, then cancelling mid-download ...
  reached: Downloading 26 file(s)  -> cancelling

  ok:         False
  state:      AbortedByUser
  failReason: Aborted.

--- the instance afterwards ---
mods still there:    45 pack mods (was 45)
identical to before: True
my own mod:          True
recorded version:    13.2.2
ledger version:      13.2.2
temp files left:     0
```

The mod list is the same set it was, the player's own file is untouched, both the config and the
ledger still say the old version, and nothing was left in the temp folder.

### Still not proved

- **Cancel has still never been pressed in the window.** The token now demonstrably reaches a real
  `NetJob` and stops it, but the path from the button through `TaskProgressViewModel` to that token
  has only been exercised headlessly.
- **A cancelled INSTALL has not been run for real**, only a cancelled update. The staging task
  destroys its folder on abort and is covered by tests; no real one has been interrupted.
- **Nothing has been cancelled on Linux or macOS**, like everything else here.

## Wave 46 — the launcher's own log, in the launcher

Wave 37 gave this launcher a way to upload a log to a paste service and hand somebody the link. It
did not give it any way to *look at its own log* — so the answer to "send us your launcher log" still
began with explaining where the data folder is.

Now it is one menu entry, and the window has the Upload button on it.

### The whole feature is which folder it reads

The list, the reader, the size caps and the upload confirmation are the **same view model** the
instance's Other logs page uses. One implementation, so the two places a log gets read cannot drift
apart — and this wave is really just pointing it somewhere new and putting a window round it.

The one design decision is **`<root>/logs`, not `<root>`**. The reader searches recursively, and a
data folder holds every instance's Minecraft logs as well:

- pointed at `<root>/logs` → the five rotated `ExtremeLauncher-N.log` files
- pointed at `<root>` → those, plus `latest.log`, `debug.log` and every crash report from every
  instance

A test asserts both, including the wrong one, because it is the mistake the comment warns about and a
reader should be able to see that it was real rather than hypothetical.

Verified against the dev build's actual data folder, which now holds exactly the five:

```
ExtremeLauncher-0.log   ExtremeLauncher-1.log   ExtremeLauncher-2.log
ExtremeLauncher-3.log   ExtremeLauncher-4.log
```

### Smaller decisions

- **Not blocked by a launch**, unlike most of the main window. Reading the log while a game starts is
  exactly when somebody wants to, and the window only reads files.
- **The watcher is disposed when the window closes.** It holds a handle on the logs folder and
  rescans on every write; the launcher writes to that folder constantly, so leaving one running for a
  window nobody is looking at is a rescan per log line, forever.
- **A fresh install with no logs yet shows an empty list**, not an error. The very first start is
  writing its log as it goes, so that moment is real.

### Still not proved

- **Nobody has opened it.** The bindings mirror the instance page's, which are covered headlessly,
  but this window has never been on a screen -- and it is the first one in this port whose whole job
  is to display a wall of text, where the font and the wrapping actually matter.
- **The launcher's log has never been uploaded for real**, for the same reason nothing else has: no
  paste has been created from this port at all (wave 37).
- **The log is not filtered or searchable.** Five files of a few hundred lines each is small enough
  that it does not matter yet; a debug-level log would change that.

## Wave 47 — copying a world

The safe way to experiment with a save, and the thing people otherwise do by hand in a file manager —
which produces two worlds both called "New World" in the game's list and no way to tell them apart.

That is the whole reason the feature is worth having rather than leaving to the file manager:
**Minecraft shows the name out of `level.dat` and never shows the folder.** So copying a world means
copying the folder *and rewriting the name inside the copy*, which is exactly what upstream's
`World::install` does and what a hand copy cannot.

### The folder is named after the original, deduped

`FS::DirNameFromString(m_actualName, to)` — upstream's, and it looks wrong until you notice it cannot
be otherwise. A world's name may contain characters a folder name cannot, so the folder is always
derived and never the name as typed; and the dedupe is what stops the copy landing on top of the
world it came from. So "My World" copied as "Backup" gives a folder derived from *My World*, with
`LevelName` set to *Backup*.

### A copy that could not be renamed is still a copy

If the rename fails, the copy is there and usable — it just has the old name inside it. That is
reported plainly rather than treated as a failure:

> Copied "My World", but the copy could not be renamed. It is in `My World (1)`.

Deleting a good copy in order to report a tidy error would be the worse trade.

### Verified by breaking it

Removing the rename-inside-the-copy step — the naive "just copy the folder" version — fails three
tests, including the one asserting the copy is a real copy of the *files*. A world is its region
files, not its `level.dat`; a copy that only wrote a new `level.dat` would look right in the list and
be an empty world in the game.

Two smaller rules, both mirroring the rest of the page:

- **A world whose `level.dat` will not parse cannot be copied**, unlike delete which works on
  anything. There would be no name to write into the copy, and the result would be a second copy of
  something already broken.
- **A null answer to the name prompt is a cancel; an empty one is refused with a message.** Those are
  different answers and the page has told them apart since the rename action.

### A test bug worth writing down

Four of these failed first time because I selected by `Path.Combine(_saves, "world1")` while the page
stores `FileSystem.CleanPath(...)` — forward slashes. On Windows the two strings never match, so
nothing was selected and `CanCopy` was false.

> Selecting by a path I built is testing my idea of the path. Every other test on this page selects
> by `page.Worlds.Single(…).Path` — the model's own value — which is both shorter and the only
> version that cannot disagree with the code under test.

### Still not proved

- **No copied world has been opened in Minecraft.** The `level.dat` is written by the port's own NBT
  writer, which two independent implementations agree on (wave 35), and the game has still never read
  one.
- ~~**A copy of a large world has not been timed.**~~ *The progress half was closed in wave 48 via
  ITaskRunner; the timing on a real world is still unmeasured.*
- ~~**Copying while the game is running is not prevented.**~~ *Upstream's nag landed in wave 48, on
  all three actions that touch a world.*

## Wave 48 — the two gaps wave 47 left, and a test that asserted nothing

Wave 47 shipped world copying and recorded two holes in the same breath. Both are closed.

### The safety nag upstream has

`worldSafetyNagQuestion` warns before touching a world Minecraft may have open, and all three actions
that touch one — rename, copy, delete — now go through it.

**A nag rather than a lock, deliberately.** The servers page *locks* while the game runs because the
game rewrites `servers.dat` on exit and would silently undo any edit; a world is a different risk. The
game holds it open and writes to it, so changing one is *unsafe* rather than futile — and a world
sitting idle at the title screen is fine. The launcher cannot tell which from out here, so it says so
and lets the person decide:

> Minecraft is running. Changing a world while it is open is potentially unsafe — the game may
> overwrite the change, or the world may be left in a state it cannot load.

With the game stopped nothing is asked, which is worth a test of its own: the ordinary case must not
grow an extra click.

### A view model can reach the progress window now

Wave 44 gave three app-level callers a window to run long work behind. A view model could not use it —
it has no `Window` to be modal to and no business knowing about one — so the world copy awaited
inline with nothing on screen. For a several-hundred-megabyte world that is the same silence the
progress window was built to end.

`ITaskRunner` is the door: the view model asks for a task to be run somewhere visible, the app decides
where. It falls back to running inline when there is no runner, so a headless caller still works and
the page is never disabled for want of a window.

### A half-finished copy is cleaned up

A partial copy is **worse than no copy**: the folder looks like a world, appears in this list *and in
the game's*, and is missing region files — so it loads as broken or empty, which is far more
confusing than the copy simply not happening. So a cancelled or failed copy takes its folder with it,
and says which happened:

> Copy of "My World" cancelled. Nothing was changed.

### The test that asserted nothing

The cleanup test passed **with the cleanup deleted**. Its stub runner returned `false` without running
the task, so no partial folder ever existed — there was nothing to clean up and nothing to assert.

> The same lesson in a new disguise, and the reason every guard in this port gets broken on purpose
> before it is believed. A stub that skips the work under test turns the test into a check that the
> stub returns what it was told to.

The runner now runs the copy for real and *then* reports it unfinished, which is what a cancellation
actually looks like from the caller's side: files written, and told it did not finish. The assertion
also moved to the **folder** rather than the list — a partial copy the page merely does not show is
still one the game would find.

Re-verified by deleting the cleanup again, which now fails the test.

### Still not proved

- **No copied world has been opened in Minecraft**, still.
- **The nag has never been seen while a game was actually running.** `IsLocked` is set by the instance
  window from the launch coordinator, which is covered; no world has been copied during a real launch.
- **A large copy has not been timed.** It now reports progress, but `CopyTask`'s numbers on a
  hundred-thousand-file world have never been watched.

## Wave 49 — customising a component, and reverting it

Pinning a component to an editable local copy — upstream's `PackProfile::customize` / `revertToBase`
and `Component::customize` / `revert`. The capability flags for this (`IsCustom`, `IsCustomizable`,
`IsRevertible`) were ported and tested back in the component wave and had, until now, nothing that
used them.

### Customise cuts the meta link, and that is the whole point

`Component.Customize(patchesDirectory)` writes the component's resolved version file out to
`<instance>/patches/<uid>.json` — verbatim, so the user starts editing from what the pack actually
ships — then points `LocalFile` at it and **clears `MetaVersion`**.

Clearing the link is not tidiness; it is the feature. Before customising, the meta server wins on
every refresh. After, the local file does. And it is not optional, because of a quirk this port
preserved from upstream: `GetVersionFile()` returns the meta version whenever both are set, so
leaving `MetaVersion` in place would let the very next resolution silently overwrite the file the
user is now free to edit. Breaking the clear fails
`CustomisingWritesAPatchFileAndCutsTheMetaLink` — verified.

`Revert` deletes the patch and clears `LocalFile`, then marks the component unloaded so the next
resolution re-fetches rather than trusting the copy just deleted. A **deliberate divergence**:
upstream also reaches for an ambient metadata-index singleton here to reload the meta version on the
spot; the port has no such global and does not need one — the resolution that runs before every
launch repopulates it from the same place every other component's metadata comes from.

### Guarded the way upstream guards it

`PackProfile.Customize` checks `IsCustomizable` (there is a meta version to copy) and `Component`
checks it is not already custom — the two are not redundant: the first is what a UI reads to enable
the button, the second is what makes calling it directly safe. `RevertToBase` takes the metadata
`Index` and refuses through `IsRevertible` when the index has never heard of the uid, because
reverting throws the local copy away and expects the server to have one — without that check a revert
could strand a component that can never be resolved again.

### Proved through the real resolution path, not a hand-written file

The unit tests cover the file write, the link cut, and the guards. The proof that it *works* runs the
actual `ComponentUpdateTask`:

- **`CustomisingProducesAPatchAFreshResolutionLoadsAsCustom`** — resolve, customise, then resolve a
  *fresh* profile (which is what reopening or launching the instance does: both load `mmc-pack.json`
  into new component objects) and the patch `Customize` wrote loads as custom.
- **`AnEditToACustomisedPatchSurvivesResolution`** — edit the patch, reopen, and the edit wins over
  the name the meta server still offers. This is the whole reason to customise.
- **`RevertingLetsTheMetaServerWinAgain`** — revert, reopen, and the meta version takes back over.

> A wrong turn worth recording: my first round-trip test reused one in-memory component across two
> resolutions and failed, because a loaded component short-circuits the resolver (`IsLoaded` returns
> early). That was the test modelling something the system never does — a live component is never
> re-resolved in place; a reload builds fresh objects. Resolving a fresh profile the second time is
> both correct and what actually happens.

### Still not proved

- **The Version page cannot surface these yet, and that is a real precondition, not an oversight.**
  The page loads `mmc-pack.json` through `PackProfile.Load`, which populates neither `MetaVersion` nor
  `LocalFile` — those arrive only from a resolution. So `IsCustomizable` reads false across the board
  on the page as it stands, exactly as upstream's own button is disabled until "the update task
  finishes." Wiring the buttons needs the page to hold resolved components first; doing it now would
  be dead UI. The operations are ready for that page when it has them.
- **Edit-JSON is still not ported** — opening the patch in an external editor. Customise now *creates*
  the file it would open; launching an editor is a separate, platform-specific action (upstream's
  `openJsonEditor`).
- **No customised component has been launched**, only resolved.

## Wave 50 — pre-launch and post-exit commands

Two settings — `PreLaunchCommand` and `PostExitCommand` — had been registered, overridable per
instance, and shown on the settings page since the settings wave, and **never executed**. Only
`WrapperCommand` was wired into the launch. Now all three are.

Ported from `launcher/launch/steps/PreLaunchCommand.cpp` and `PostLaunchCommand.cpp`, which are the
same step under two labels: run a command, forward its output to the log, and fail on a bad exit.

### The non-zero exit is the whole feature

A pre-launch command that exits non-zero **aborts the launch** — the game never starts. That is the
entire reason someone writes one: *"back up my world, and if the backup fails, do not let me play and
overwrite it."* Post-exit fails the same way, which is upstream's behaviour for both. Breaking the
non-zero check (making the step succeed regardless) fails `ANonZeroExitFailsTheStep` — verified.

A command whose executable cannot be found fails the same way the wrapper command does, rather than
being skipped: a hook the user set that silently does nothing is worse than one that stops and says
why.

### Placement is upstream's, and it is load-bearing

Pre-launch runs **first**, before anything touches the instance — the only placement under which
"back up before you let me play" means what it says. Post-exit runs **last**, after the game step; and
because `LauncherPartLaunch` reports a bad exit rather than throwing, post-exit runs *even after a
crash*, so "sync my saves when I'm done" happens whether the session ended well or badly.

Proved through a real `LaunchPipeline` of actual subprocesses, not just by inspecting the step list:

```
happy path:            pipeline ok: True   order: PRE -> GAME -> POST
pre-launch fails (2):  pipeline ok: False  game or post ran: False
```

### The $INST_* variables, and an upstream bug not reproduced

`LaunchVariables` ports `getVariables` — `INST_NAME`, `INST_ID`, `INST_DIR`, `INST_MC_DIR`,
`INST_JAVA`, `INST_JAVA_ARGS`, and `NO_COLOR`. They are substituted into the command *and* placed in
the process environment, so a script can read `$INST_MC_DIR` either way. A test writes `$INST_NAME`
to a file through a real `cmd`/`sh` and reads the instance name back; another reads `INST_ID` out of
the child's own environment.

**One deliberate divergence.** `INST_JAVA` is a prefix of `INST_JAVA_ARGS`, and upstream substitutes
in sorted-key order — so it replaces `$INST_JAVA` first and mangles `$INST_JAVA_ARGS` into
`<the java path>_ARGS` before the longer key is ever tried. Nobody could want that; substituting
longest-key-first is a one-line fix. `TheLongerKeyWinsWhereOneIsAPrefixOfAnother` pins it, and
reintroducing the ascending order fails it.

### A gotcha found in passing (not this wave's to fix)

`Commandline.SplitArgs` treats `\` as an escape **inside quotes**, mirroring Qt's `splitCommand`. So a
*quoted* Windows path in a command — `"C:\tools\x.exe"` — loses its separators, while the same path
unquoted survives. This is pre-existing wave-0 behaviour, faithful to Qt, and shared by the wrapper
command; changing it would diverge from the ported contract and risk the POSIX escape handling. Noted
rather than "fixed": on Windows, an unquoted path or forward slashes work.

### Still not proved

- **No hook has run during a real game launch.** The step runs real subprocesses and the full
  pipeline of them was proven to order and abort correctly; no hook has fired around an actual
  Minecraft start, because that needs a resolved instance and Java.
- **`INST_JAVA_ARGS` is the user's JVM args, not upstream's full `javaArguments()`** (which folds in
  memory and permgen flags too). The closest faithful match without rebuilding the whole java command
  line; a hook that expects the exact expanded set would see less than upstream gives.
- **No settings-page test drives these through the UI.** The values are edited by the existing
  instance-settings editors (already tested); nothing yet asserts a command typed there reaches a
  launch.

## Wave 51 — Quilt end to end, and the dead meta server it uncovered

Set out to prove a loader no earlier wave had: Fabric, Forge and NeoForge were launched for real, Quilt
never was. Proving it turned up a shipping bug.

### The bug: the default metadata server did not exist

The probe pointed the resolver at the build's own default meta URL and got nothing, because
`BuildConfig.cs` had:

```
MetaUrl = "https://meta.extremelauncher.net/v1/"
```

**`meta.extremelauncher.net` has no DNS record.** The fork's CMakeLists actually configures
`https://extremelauncher.net/_api/meta-v1/` — on the same domain the news feed uses (which is why news
worked and nothing else did), and which 301-redirects to Prism's meta server. The port had transcribed
a phantom `meta.` subdomain. Line 116, the news URL, had the domain right; line 98, the meta URL, did
not — an inconsistency inside one file.

The effect out of the box: **a fresh install could fetch no component metadata, so every resolution and
every launch failed.** News would load; nothing else would. No in-process test could have caught it,
because nothing dereferences the URL without a network — which is exactly why a live probe is worth
running.

Fixed to the fork's real URL. The 301 to Prism is followed because the shared HttpClient handler
allows redirects (ProxyFactory, wave 33). Proved by resolving Quilt against the corrected default with
no override:

```
using default MetaUrl: https://extremelauncher.net/_api/meta-v1/
resolved ok: True
```

Guarded by `BuildConfigUrlTests`: the meta URL must be well-formed https, end in a slash, and share a
host with the news feed. Reintroducing the phantom subdomain fails the shared-host test — verified.
One of the four is a DNS check that skips offline: the single fact that actually separates the fix from
the bug, and the only test that would have caught the original on its own.

### Quilt resolves, via Fabric's intermediary

With a working server the resolution is clean:

```
net.minecraft             1.20.1
org.quiltmc.quilt-loader   0.26.3
net.fabricmc.intermediary  1.20.1   (dep)
org.lwjgl3                 3.3.1    (dep)
main class:  org.quiltmc.loader.impl.launch.knot.KnotClient
libraries:   48
```

The finding underneath it: **modern quilt-loader requires `net.fabricmc.intermediary`, not
`org.quiltmc.hashed`.** Quilt moved onto Fabric's intermediary; on the live server
`org.quiltmc.hashed` 404s and quilt-loader 0.26.3's `requires` lists `net.fabricmc.intermediary` with
no version. That is the same empty-version trap the Fabric case had (wave 25), and the same hack-table
entry saves it: intermediary is assigned the Minecraft version. The existing test covered only the
legacy `org.quiltmc.hashed` path; the new `ModernQuiltRequiresFabricsIntermediaryAndStillGetsTheVersion`
covers the path a Quilt instance created today actually takes. Removing intermediary from the table
fails both it and the Fabric test — verified.

### Still not proved

- **No Quilt instance has been launched**, only resolved to a complete profile with the Quilt Knot
  main class and 48 libraries. The same standing gap Fabric/Forge closed and Quilt has not.
- **The redirect adds a hop to every metadata fetch.** The fork serves meta by redirecting to Prism
  rather than hosting its own; harmless, but it means the fork's metadata is only ever as available as
  Prism's, and a launch on a flaky network pays a redirect per document.
- **OptiFine and LiteLoader remain unverified**, the last two loaders the platform layer names.

## Wave 52 — auditing the rest of the baked-in URLs

Wave 51 found the meta URL pointing at a host that did not exist. A hand-transcribed constant that
wrong is rarely alone, so this wave checked every URL in `BuildConfig.cs` against the fork's
CMakeLists in one pass. Two more were wrong; both flowed straight into the Help menu.

### Help was silently switched off

`HelpUrl` was empty, and the links menu hides any entry whose URL is empty — so the **Help item never
appeared**, even though the fork configures `Launcher_HELP_URL` and the endpoint is live (checked:
`https://extremelauncher.net/_api/help/` returns 200).

The fork's value is templated — `.../_api/help/%1/` — but upstream's only caller is
`on_actionOpenWiki`, which is `HELP_URL.arg("")`: it always substitutes an *empty* page, so the
template collapses to the help root. The port has no per-page help, so it stores that resolved root
directly rather than carrying a one-argument template. With it set, the Help entry appears and opens
the live endpoint.

### Translate pointed at an invented repo

`TranslationsUrl` was `https://github.com/ExtremeLauncher/Translations` — which 404s, and whose org
("ExtremeLauncher") is not even the one every other URL uses ("ExtremeLauncherTeam"). That mismatch is
the tell: it was guessed, not transcribed. The fork translates on Weblate
(`https://hosted.weblate.org/projects/extremelauncher/launcher/`), so the value now matches the source
of truth. Nothing beyond the links menu consumes it — the translation machinery is unported — and the
Weblate project is not populated yet, but the constant is no longer fiction.

### What was already right

The rest checked out: the news URL, the bug tracker, the updater repo, and the deliberately-empty
Discord / Matrix / Subreddit (the fork leaves those blank too). The MSA and CurseForge keys stay empty
on purpose — the port's standing policy that credentials come from a build-time secret, never from
source. The imgur client id is left empty for the same reason, which keeps screenshot upload disabled;
that is a policy choice, not a transcription error, and is noted as such.

### Guarded

`BuildConfigUrlTests` grew two checks — Help is set, well-formed, and on the launcher domain; Translate
is not the invented `github.com/ExtremeLauncher/` repo — and `LinksAndMetadataTests` two more: the
default build offers Help pointing at the help endpoint, and Translate points at Weblate. The existing
"a fork that leaves Help empty hides it" test still holds, because it sets the value empty itself.

### Still not proved

- **No human has clicked Help or Translate.** They open through the same link path everything else in
  that menu uses, which has never been exercised against a real browser (wave 32's standing gap).
- **The Weblate project 404s today**, so Translate opens a "project not found" page until the fork
  populates it. The launcher has the right URL; the fork has the rest of the job.
- **Per-page help is not ported** — only the root the toolbar button opens. Upstream's `%1` lets other
  places open a specific help page; nothing in the port needs that yet.

## Wave 53 — uploading a screenshot

The screenshots page could rename and delete; it could not share. Now it uploads to imgur and copies a
link, ported from `ImgurUpload.cpp` and `ImgurAlbumCreation.cpp`.

### One image, one link; several, one album

`ImgurClient.UploadAsync` posts the PNG as multipart (`image`, `type=file`, `title`) with a
`Authorization: Client-ID <id>` header, and reads the link out of imgur's `{ success, data }` envelope.
`CreateAlbumAsync` bundles several already-uploaded images by their delete hashes into an anonymous
`imgur.com/a/<id>` album — upstream's exact body, `deletehashes=…&title=Minecraft%20Screenshots&privacy=hidden`.

**The success flag, not the status code.** Imgur can answer 200 with `success:false` for a rejected or
rate-limited image, and trusting the transport would hand back an empty link and call it done. The
client checks the flag; breaking that check fails `ASuccessFalseEnvelopeIsAFailureNotAnEmptyLink` —
verified.

### Key-gated, like every other credentialed service

Imgur needs a Client-ID and this build ships without one (the standing policy — credentials come from
a build-time secret, not source). So `IsAvailable` is false, `Destination` is empty, and the page
**hides** the Upload button rather than offering an upload that would 401. A fork that fills
`Launcher_IMGUR_CLIENT_ID` gets it working with no other change. Verified with no request sent.

### Ask first, name the host, say what a screenshot shows

`ScreenshotUpload` is the same careful flow as `LogUpload`: a screenshot can show a user name, a server
address, a face — and an anonymous imgur upload is public and not really undoable. So it confirms,
names the host, defaults to no, and flags the affirmative destructive. No dialog service means no
upload — the same rule the logs and packs follow, for the same reason. The link lands on the clipboard,
because a link in a window nobody can copy from is no link at all.

### Single-select today, album-ready

The flow takes a list of 1..N and the app bundles several into an album; the page passes its one
selected screenshot, because the page is single-select. The album path is built and tested (a two-item
upload asserts one call, two files, an album link) so it is ready for a multi-select list when the UI
grows one — rather than being dead code or a rewrite later.

### Still not proved

- **No real screenshot has been uploaded**, because this build has no imgur key — the same shape as
  the paste services (wave 37): the request and response are pinned from upstream through a stub, and
  no byte has reached imgur.
- **The page uploads one screenshot at a time.** Multi-select and the album link it would produce wait
  on list multi-selection in the view model and the window; the machinery underneath is done.
- **Screenshots still have no thumbnail preview** — the other half of the page's gap. The headless
  platform fakes the image decoder, so a preview grid cannot be proven here and is left for a
  human-in-the-loop pass.

## Wave 54 — exporting a CurseForge pack

The instance could be exported as a `.mrpack`; the CurseForge manifest builder had been ported and
tested since the export wave and **nothing called it**. Now the export offers CurseForge alongside
Modrinth. No key or network — it reads the instance and writes a `.zip`.

### The two formats link mods differently, and converge on overrides

A Modrinth manifest names a mod by download URL and hash; a CurseForge manifest names it by project
and file id. So the two branches collect *linked* files differently — `CollectLinkedFiles` reads the
packwiz URL and hashes, `CollectFlameFiles` reads `[update.curseforge]` project-id/file-id — but
everything not linked becomes an override the same way, so both meet at `CollectOverrides` and one
archive writer. The writer grew from one root entry to a list, because a CurseForge pack ships
`manifest.json` **and** `modlist.html`.

### What CurseForge cannot express, it carries

A CurseForge manifest can only name a mod by id, so **only mods installed from CurseForge can be
linked**. A Modrinth mod has no such ids — so rather than drop it, the export copies it into
overrides, where it is carried bodily. Not lossy: the mod is still in the pack, just referenced
differently. It does mean a pack of Modrinth mods exported to CurseForge is almost all overrides,
which is honest about what the format can say. A test asserts a Modrinth mod ends up in
`overrides/mods/` with nothing in the manifest's file list.

### The id check, and a test that first proved nothing

The guard is "link only a CurseForge mod with real ids." My first two tests for it passed with the
check deleted — the Modrinth-mod case is caught by the provider being wrong, the jar-gone case by the
jar being gone, so neither exercised the ids. The case that actually needs the id check is a *corrupt*
packwiz entry: provider curseforge, ids missing, which parse as zero — link it and the manifest
carries `projectID:0`, a file CurseForge cannot resolve. Added that test; it passes, and fails when
the id check is removed. The earlier lesson again: a guard is not covered until a test fails without
it.

### Proved structurally, not just detected

Beyond "the port's detector calls it a CurseForge pack," the exported `manifest.json` is parsed back
through the port's own import-side `FlamePack.Parse` — name, version, and a file indexed by its ids
all read back. If the writer and reader ever drift, that round-trip is where it shows. The manifest is
also asserted BOM-free on its raw bytes, the trap the Modrinth exporter already carried a scar from.

### Wired into the export window

`ExportKind` gained `CurseForgePack`, with an `IsPack()`/`Extension()` helper so the pack-vs-zip
distinction lives in one place rather than being spelled out at every branch. The window offers a
third radio button; the description says plainly that CurseForge mods are linked and everything else
is bundled, because that distinction is invisible from the format's name and changes the result a lot.

### Still not proved

- **No exported CurseForge pack has been opened by CurseForge's app** or another launcher. It
  round-trips through this port's own reader and `PackTypeDetector` recognises it, but the real test
  is the other client — the same standing gap the Modrinth export has.
- **Only Modrinth and CurseForge export.** Technic, FTB and ATLauncher are parsed on the import side
  and have no export.
- **The export window has never been seen by a human**, like every window in this port.

## Wave 74 — cross-platform CI, and the three things it caught

Taking the repo public added a GitHub Actions workflow that builds and tests the port on **Linux and
Windows**. The port had only ever run on Windows, and Linux immediately failed six tests -- each a real
"only-ever-run-on-Windows" defect, not a flake.

### A genuine bug: undo-trash never re-listed the instance

`UndoTrashInstance` moved the files back but called `LoadInstance(id)` and threw the result away --
and `LoadInstance` only BUILDS a record, it does not register it (`LoadList` assigns it into the
instance dictionary; undo did not). So a restored instance was on disk but absent from the list, and
`GetInstanceById` returned null. Windows never caught it because its trash is the Recycle Bin, which
reports no restore path, so the undo round-trip test SKIPS there and had never actually run. Linux's
freedesktop trash does report the path, ran the test, and exposed it. Fixed to assign the restored
record back into the list.

### Test data that was never committed

The packwiz `.pw.toml` fixtures lived only in an untracked folder at the Qt repo's root and were pulled
in through a relative `Content Include`. They existed on the Windows working copy and nowhere in git, so
a fresh Linux checkout had none and three PackwizTests failed. Moved into the port's own tracked
`testdata/`, committed, and the cross-project link -- with its backslash glob that is a literal on Linux
anyway -- removed.

### Tests that only made sense on Windows

`CloningOnAnUnimplementedPlatformSaysSo` and two reflink-guarded clone tests used `[Fact]` with
`Skip.If`; `Skip.If` only skips under `[SkippableFact]`, so on Linux the skip threw and was reported as
a failure. And `AFailedRemovalDoesNotDisturbGrouping` makes a removal fail by holding a file open --
which only blocks a move on Windows, since Unix renames and unlinks open files freely. The first three
became `[SkippableFact]`; the last is scoped to Windows, where its premise holds.

### Now green on both

Windows stays at 3,264 passing / 15 skipped; Linux now passes too, with a few more platform skips. CI
runs on every push touching `dotnet/`, so the port cannot silently regress on either OS again -- which
is the whole point of having taken it public with the workflow attached.

## Wave 73 — find in the launch log

`LogPage` in upstream has a search box; the port's live log could only be copied whole. A modded
launch prints tens of thousands of lines, and the reason someone is on this page is usually to find one
of them. So the log page can now be searched.

### Next and previous, wrapping, over the live list

`SearchText` drives a case-insensitive literal search over the log lines. `FindNext` moves to the next
matching line and wraps to the top after the last; `FindPrevious` walks back and wraps to the bottom
before the first. `CurrentMatchIndex` is the line the view scrolls to and highlights, and `MatchSummary`
reads "3 of 12", or "No matches", or nothing when the box is empty. Matches are recomputed against the
live collection each Find rather than cached, so a search stays correct as lines stream in and as the
5,000-line cap trims from the front -- the summary is re-announced on both.

### Tested

Six tests: nothing is findable without a query; Find next walks the matches and wraps; Find previous
wraps to the bottom; the search is case-insensitive; a query that matches nothing says so; and changing
the query starts the walk over from the top. The wrap-around is mutation-verified -- stop Find next
wrapping and the walk test fails at the line where it should have returned to the first match.

### Still not proved

- **The scroll-to and highlight are app glue, not tested.** The view-model computes and exposes the
  match line index; the log view is a plain `ItemsControl`, so bringing that line into view and tinting
  it is code-behind a headless test cannot exercise. The search box, the Find buttons and the summary
  are wired; the visual jump to the match is the untested part.
- **Per-level colouring** (wave 72's follow-up) is still not surfaced -- it needs the level carried to
  `LaunchLogLine`, a wide change through the shared progress sink, deferred as poor ROI while headless.

## Wave 72 — the launcher reads the game's log levels

The log page could only tell an error from an ordinary line when the launcher's own wrapper tagged it
with a `!![Level]!` marker -- which the GAME never does. So a crash log4j-printed to stdout arrived as
plain `StdOut` and was shown like any other line. `MinecraftInstance::guessLevel` is what upstream uses
to colour the game's own output; it is ported now.

### Levels from the line's own text

`MessageLevels.Guess(line, previous)` reads the level from the content: the modern log4j prefix
`[HH:MM:SS] [thread/LEVEL]`, the older java.util.logging `[SEVERE]`/`[WARNING]`/`[INFO]` forms, and --
the part that matters most -- Java stack traces, which are errors even though `\tat com.foo.Bar` looks
ordinary. The exception patterns are checked last and win over a line's own prefix, and the
`overwriting existing` line upstream singles out stays Fatal. `previous` is returned when nothing names
a level, so a multi-line message keeps one colour.

### Wired where the game's output is levelled

`EmitLines` now guesses the level of any line the wrapper did not mark, carrying the guessed level
across the lines of one read so a stack trace stays Error to its end. It does NOT carry across reads:
stdout and stderr are pumped concurrently, and a shared field would splice two unrelated streams -- a
deliberate, documented divergence from upstream's single-stream parser. With levels now accurate,
`LauncherService` flags a line as an error for `Error` OR `Fatal` (it only checked `Error` before, so a
crash's Fatal line went unhighlighted).

### Tested

Nineteen parser tests -- every log4j level, the old forge forms, `overwriting existing`, five shapes of
stack-trace line, the carry, and an exception outranking an INFO prefix -- plus a real-process test that
echoes an exception to stdout and confirms it arrives as `Error`, proving the wiring. Both the
exception-wins rule and the EmitLines wiring are mutation-verified.

### Still not proved

- **The level is classified, not yet coloured per level.** `LaunchLogLine` still carries error-or-not,
  so the log page shows errors highlighted but does not tint WARN/DEBUG differently. Surfacing the full
  level to the view is a follow-up; the parsing it needs is now here and tested.
- **Cross-read stack-trace continuation** is not carried, per the concurrency note above.

## Wave 71 — Add Empty: a hand-made component

The version page's "Add Empty" was one of the actions blocked since wave 49 -- except it was not really
blocked. Unlike Customize and Revert, which need a RESOLVED component to copy a patch from, adding an
empty component invents one from nothing: a uid, a name, a bare patch. No metadata, no network. So it is
ported now.

### The core is a five-line file write

`PackProfile.InstallEmpty(uid, name, patchesDirectory)` mirrors upstream's `installEmpty`: write a patch
holding just the uid, name and version "1", then append the component as a local override so it shows
and is editable at once. It refuses a uid already in the profile -- two components sharing a uid is
exactly what upstream's NewComponentDialog blacklist prevents -- and that refusal is mutation-verified.

### A two-field dialog, because a component needs two things

`NewComponentViewModel` is the dialog: a name and a uid, with the uid checked against the ones already
present and the clash shown rather than left as a mysterious disabled button. The version page reaches
it through an `INewComponentPrompt` handed the existing uids; the app implements it with a small
`NewComponentWindow`. The command follows the page's own rule for edits -- the patch is written now, the
component-list save deferred to close -- so an Add Empty and a reorder commit together.

### Tested

Two profile tests (the component and its patch appear; a duplicate uid is refused -- mutation-verified),
four dialog-VM tests (both fields required, a clash refused and flagged, the choice trimmed, the button
re-announced on edit), and three page tests (Add Empty is off without a prompt; it adds the component,
tells the dialog which uids are taken, marks unsaved and writes the patch; cancelling changes nothing).

### Still not proved

- **Customize and Revert stay blocked**, and genuinely this time: both need the component RESOLVED to a
  metadata version the page does not fetch at open. Add Empty was the one version-page action that
  needed neither.
- **What goes INTO an empty component** -- libraries, a main class, tweakers -- has no editor; upstream
  edits the patch JSON by hand too. The component exists and launches as an empty patch until then.

## Wave 70 — the copy dialog offers link modes

Wave 63 gave the copy dialog its checkboxes but every copy was a full one. `InstanceCopyTask` has had
three faster strategies since wave 7 -- copy-on-write clone, hard links, symbolic links -- each with its
own tests, and nothing let a user choose them. Now the dialog does.

### Four modes, mapped to the flags the task already reads

`CopyInstanceViewModel` gained an `InstanceCopyMode` -- Copy, Clone, HardLink, SymLink -- defaulting to
the full independent copy, which is the safe choice. `ToChoice` maps it to the prefs
`ChooseStrategy` reads: Clone sets `UseClone`, and a link mode sets `UseHardLinks` or `UseSymLinks`.
The recursion difference is the subtle part and is carried faithfully: hard links MUST recurse, because
a directory cannot be hard-linked, so each file is linked; symbolic links point at the top-level folders
whole, which is faster and is upstream's default for them. That mapping is mutation-verified -- stop the
hard-link mode recursing and the test fails.

### Clone is offered only where it works

`InstanceCopyTask` documents that the DIALOG must gate clone on filesystem support rather than falling
back to a slow copy in silence. So the option appears only when `FileSystem.CanClone` says the instances
volume does copy-on-write; the app computes that once, at the moment the dialog opens, and hides the
radio otherwise. Hard and symbolic links are always offered -- the source and destination are both under
the instances folder, so they are always on one volume.

### Tested

Six view-model tests: the default sets no link flags; each mode sets exactly its own flags with the
right recursion; Clone is offered only when the filesystem supports it; and picking a mode radio
re-announces the others so the group cannot show two filled at once. The strategies themselves -- that a
clone is really a clone, a hard link really shared -- were tested in wave 7 against real filesystem
behaviour.

### Still not proved

- **The link modes are not exercised through the dialog end to end here** -- the view-model maps to the
  right prefs (tested) and the task performs each strategy (tested), but no test drives the dialog into
  a real hard-linked copy. Clone's own test already skips where the filesystem lacks reflinks.
- **Whether a hard-linked instance is what the user wanted** is a judgement the dialog explains in a
  line of text and then trusts them on, as upstream does.

## Wave 69 — a dropped world zip is a world, not a file in saves/

Investigating the drop importer turned up a real bug. `ResourceImportTask` files every dropped resource
the same way: identify its kind, then `File.Copy` it into that kind's folder. That is right for a mod
(a jar in mods/) or a resource pack (a zip in resourcepacks/) -- but wrong for a world. A world save
must be a DIRECTORY, `saves/<name>/level.dat`; copying the zip to `saves/world.zip` produces something
Minecraft cannot see, and the drop silently "succeeded".

### Worlds are extracted, everything else is copied

`ImportOne` now special-cases `WorldSave`: instead of copying the file, it runs it through
`World.Install` (wave 58), which finds the level.dat, strips the wrapper folder, names the world after
its own level.dat rather than the zip, deduplicates against saves/, and extracts the subtree. So a
dropped world lands as a real `saves/<name>/` directory with its region files, and no stray zip is left
behind. Every other kind still copies as before.

### What the detector already decided

The bug was masked by how narrow WorldSave detection is: `WorldSaveUtils` only recognises a world one
level down (`<world>/level.dat`, optionally under `saves/`), which is how one is exported, and `Identify`
ignores directories entirely -- so only correctly-shaped world ZIPS reach this path at all. Those are
exactly the ones that were being misfiled. Folder drops are unsupported for every kind, not just worlds,
so they are out of scope here.

### Tested

A dropped world zip (`MyWorld/level.dat` inside) is imported as `saves/Imported World/level.dat` with
its region file, the wrapper folder stripped, and neither the zip nor the wrapper name left in saves/.
The routing is mutation-verified: let a WorldSave fall through to the generic `File.Copy` and the test
fails, catching both halves -- the zip stops being extracted and starts sitting in saves/ as a file.

### Still not proved

- **Dropping a whole `saves/` directory of several worlds imports only the first.** `World.Install`
  installs the shallowest world it finds; upstream imports each. A single wrapped world -- the common
  drop -- is correct.
- **World FOLDER drops are unsupported**, because `Identify` only inspects files. That is a launcher-wide
  limitation of the drop path, not specific to worlds.

## Wave 68 — the pack browser reads live Modrinth (a real-data probe)

The pack browser -- how a user finds and installs a modpack -- had been tested only against saved
fixtures. This wave points it at the real Modrinth API and reads the results back through the port's own
`ResourceSearchSource`, the same object the browser window drives. Modrinth's search is keyless, so this
needs no credentials, unlike the CurseForge half the browser also offers.

### What the probe proves

Two `SkippableFact`s in the launch tests: search Modrinth for "fabric" modpacks and confirm the port
parses real hits into `IndexedPack` rows -- each with the provider set, an addon id and a name -- then
take the first result and load its version list from the live API, confirming it fills with real
releases. Both passed against the live server on this run: the search facets, the response shape and the
version endpoint all still match what the parser expects.

### Skipped, never a build-breaker

A probe against a server the project does not control cannot fail the build when the server is down. A
network error (`HttpRequestException`, a timeout) throws `SkipException` and the test is skipped; only a
200 whose SHAPE has drifted -- Modrinth renaming a field the parser reads -- fails it, which is exactly
the regression a fixture can never catch because the fixture is frozen. Offline it reports skipped, like
the reflink and DNS probes already do.

### Still not proved

- **Nothing was downloaded.** The probe reads search results and a version list; it does not fetch a
  .mrpack or its mods, so the import-and-resolve path past selection is still covered only by fixtures
  (the ModrinthImportTask tests) and not by live data.
- **CurseForge search is unprobed** because it needs an API key, which stays empty by the credential
  policy. The Flame parser has fixture tests; live Flame is the same standing gap the key blocks.

## Wave 67 — Kill from the main window

The main window could launch an instance but not stop one -- upstream's `actionKillInstance`, for ending
a game (a hung one especially) without first opening its window. The instance window already had Kill;
the main window did not.

### The mirror of Launch

`CanKill` is `CanLaunch` turned around: a selected instance that IS running rather than one that is not,
read from the same `Registry.IsRunning` the launch gate uses. `KillSelected` cancels through THAT
instance's own coordinator (`Registry.For(id).Cancel()`), not the status strip's -- the strip follows
the most recent launch, which is not necessarily the row the user picked to kill. Cancelling unwinds the
launch pipeline the same way the instance window's Kill and the CLI's Ctrl-C do, which is what removes
the extracted natives.

### Tested

Two tests: Kill is off for a selected but idle instance (mutation-verified -- drop the running check and
it fails), and Kill stops a running one -- the fake launch blocks on its cancellation token, Kill
cancels it, the awaited launch unwinds, and the row goes from killable-not-launchable back to
launchable-not-killable. That last test only passes if Kill cancels the right coordinator: a no-op or
wrong-instance Kill would leave the launch blocked forever.

### Still not proved

- **No real game has been killed** -- the coordinator cancels and the pipeline unwinds in the test, but
  a running JVM being signalled is the app-side LoggedProcess path, which no test exercises.
- **Launch offline and demo mode are still not on the main window.** Both turn on the account/session,
  which stays deprioritised; the plain Launch is present, Kill now beside it.

## Wave 66 — Forge and NeoForge resolve end to end (a real-data probe)

A change of pace from adding buttons: proving the resolver against live metadata, the way wave 25 did
for Quilt. Forge and NeoForge have the gnarliest resolution in the platform -- an install profile, a
wrapper main class, libraries the installer patches -- and neither had been confirmed against the real
server.

### What the probe did

Built two throwaway instances -- Forge 47.4.23 on 1.20.1, NeoForge 21.1.250 on 1.21.1 -- and ran the
shipping CLI's `info` against `extremelauncher.net/_api/meta-v1/`. Both resolved cleanly: main class
`io.github.zekerzhayard.forgewrapper.installer.Main` (ForgeWrapper, the correct modern-Forge shim),
Java 17 and 21 respectively, 94 and 107 libraries, no problems. That is the whole Forge family confirmed
end to end for the first time.

### What it turned up, and why it is not a bug

The first Forge run -- with a pack that listed only `net.minecraft` and the loader -- warned "Minecraft
is missing requirement org.lwjgl3 3.3.1" and carried on. That is exactly right, and the port already
had a scar comment for it: `ComponentUpdateTask` in **Launch mode reports** unmet requirements without
acting on them, and **creation resolves** (adding org.lwjgl3) so a real instance never lacks it -- both
matching upstream, which adds dependencies when the instance is made, not when it is launched. Adding
lwjgl3 to the pack (as a real instance has it) made the warning vanish and the resolve complete. No
code changed; the behaviour was correct and deliberate.

### The durable artifact

A probe that leaves nothing behind teaches nobody. `ALaunchWithAMissingRequiredComponentReportsAnError
AndAddsNothing` pins both halves of what the probe exercised, offline, with a stub index: a launch-mode
resolve of a component whose required dependency is absent raises an ERROR naming it, and does NOT
conjure the dependency into the list. Mutation-verified -- downgrade the severity to Warning and it
fails -- so a future change cannot quietly turn the missing-lwjgl3 error into a shrug.

### Still not proved

- **No Forge or NeoForge game has actually started** -- resolution, the classpath and the wrapper main
  class are right, but the ForgeWrapper installer runs at launch against a real JVM, which no test here
  does. The same standing end-to-end gap every loader has.
- **OptiFine and LiteLoader are not launch components** at all -- they are mod-search loader types, so
  there is nothing to resolve; the earlier note calling them "unverified loaders" was a miscategory.

## Wave 65 — Other logs: Delete and Clean

The other-logs page could copy, upload and refresh but not remove anything -- `OtherLogsPage`'s
**Delete** (the selected file) and **Clean** (all of them) were missing. Both are the kind of tidying
someone does after a crash-report pile builds up, on the page they are already looking at.

### Trashed where it can be, deleted where it cannot

Ported from `on_btnDelete_clicked` and `on_btnClean_clicked`: each file is sent to the trash where the
platform has one and deleted where it does not, the same two-step the screenshots page uses. Both ask
first -- a log is regenerable, so they ask once and offer no undo, unlike a world -- and a build with
no way to ask cannot delete at all, so the buttons are off without prompts. Clean names the files it
could not remove (usually the log the running game still holds open) rather than reporting a clean
sweep it did not make.

### Re-read from disk, like every folder page

After a delete the list is rebuilt from the folder rather than patched, and the shown text is cleared
if the file behind it is gone. The page's file watcher would have caught the change anyway; doing it in
the command means the row is gone the instant the button returns, not one watcher tick later.

### Tested

Five tests: Delete and Clean are off without prompts; deleting removes the selected file and leaves the
others; declining keeps it; Clean removes every log across logs/ and crash-reports/; declining Clean
keeps them all. Both confirmation guards are mutation-verified -- strip the "declined" check and the
decline tests fail, which is the property that matters for a destructive action.

### Still not proved

- **The log pages are now complete** for reading, copying, uploading, deleting and cleaning. What
  upstream's live LogPage still has and this does not is find/search and line-wrap toggles -- display
  conveniences, not actions on files.
- **Trashing is exercised only through its own unit tests**; here the assertions are that the file is
  gone from the folder, not which mechanism removed it.

## Wave 64 — Version page: Reload and the instance folders

Three of `VersionPage`'s actions that were missing: **Reload**, **Minecraft folder** and **Libraries
folder**. Small, and the version page is where they matter most -- it is where someone goes when an
instance will not launch, which is exactly when a by-hand fix or a re-read is wanted.

### Reload was already there, just not on a button

`ReloadFromDisk` existed and was used internally after a version change; it re-reads mmc-pack.json and,
if the read fails, leaves the page showing what it was rather than emptying itself. This wave wraps it
in a `Reload` command so the user can trigger it -- for when another launcher, or an edit by hand, has
rewritten the file underneath. A test rewrites the file to fewer components and asserts Reload picks up
the change (mutation-verified: make Reload a no-op and it fails).

### The two folders, through the opener that already existed

Minecraft folder opens the instance's `.minecraft`; Libraries folder opens its local `libraries/` --
upstream's `openPath(gameRoot)` and `openPath(getLocalLibraryPath())`, each created first if absent.
Both go through the `IFolderOpener` wave 59 introduced. The page had only the mmc-pack.json path; it now
takes the game root and library path too, passed by the editor from `InstancePaths`. A test opens each
and asserts the right one is handed to the opener (the libraries-vs-minecraft routing mutation-verified).

### Still not proved

- **Customize, Revert, Add Empty, Install Loader, Add to Minecraft jar, Import Components and Download
  All remain unported.** Customize and Revert have model support (`Component.Customize`/`Revert`, wave
  49) but need a RESOLVED component to write a patch from, and the version page loads the profile
  WITHOUT resolving (no network at open time) -- so they stay blocked on a resolve step, as noted since
  wave 49. Add loader and Change version, the two most-used, were already there.
- **The folder open is app-side and untested end to end**, like every `IFolderOpener` use.

## Wave 63 — the copy-instance dialog, with options

Copying an instance always brought everything -- `InstanceDuplication` built a default `InstanceCopyPrefs`
and only asked for a name, with a code comment admitting "that dialog is not ported". `InstanceCopyPrefs`
itself, with a checkbox's worth of flags for every category and the `IsExcluded` filter behind them, had
been ported and tested since wave 7 and nothing let a user reach it. Now the copy dialog does.

### A dialog VM over the prefs that already existed

`CopyInstanceViewModel` is the dialog: a name and one toggle per category -- saves, mods, resource and
shader packs, servers, screenshots, game options, keep-playtime -- each defaulting to on, because a
Copy that quietly dropped your worlds would be the opposite of what it is for. `ToChoice()` turns the
toggles into an `InstanceCopyPrefs`, trimming the name the way the commit does. The link-mode options
(symlink, hard link, clone) are left at their plain-copy default and noted, not faked: they are a
per-platform performance choice, not a "what to bring" one.

### Threaded without breaking the old path

`InstanceDuplication` gained an optional `IInstanceCopyPrompt`. Given one, the copy asks it for a name
AND prefs; without one, the old plain name-prompt runs and everything is copied -- so every existing
test and any headless caller behaves exactly as before. The prefs reach the real `InstanceCopyTask`, so
unchecking a box actually leaves that category behind. `MainWindowViewModel` forwards the prompt; the
app implements it with a new `CopyInstanceWindow`.

### Tested

Five view-model tests: it starts from the source name with everything checked, refuses a nameless copy
and announces it (bound to the button), carries an unchecked box into the prefs, and trims the name.
Three duplication tests over a real copy: the dialog's options decide what lands on disk -- unchecking
Mods copies the world but not the jar (mutation-verified: make the duplication ignore the choice's prefs
and it fails) -- the source name is prefilled, and cancelling copies nothing. The VM's toggle mapping is
mutation-verified too.

### Still not proved

- **The link-mode strategies are not exposed** -- every copy the dialog makes is a plain file copy.
  `InstanceCopyPrefs` and `InstanceCopyTask` carry clone/hardlink/symlink (tested since wave 7); the
  dialog just does not offer them yet.
- **The dialog has never been seen by a human**, like every window in this port.

## Wave 62 — joining a server from the list

The servers page could add, remove and reorder entries but not the one thing a server list is for:
`WorldListPage`'s neighbour `ServersPage` has a **Join** action that launches the instance straight
into the selected server. Wave 55 wired `--server` all the way to `LaunchRequest`; this connects the
button to it.

### A per-launch server, threaded through the coordinator

`IInstanceLauncher.LaunchAsync` gained an optional `server`, carried through `LaunchCoordinator` to the
app's launcher, where a per-launch server wins over the command-line one and a plain launch passes
none. So Join runs through the SAME coordinator as the window's own Launch button -- which means the
instance window locks its pages and jumps to the log page exactly as it does for a normal launch, with
no new launch path to keep in step. The servers page reaches it through a small `IServerJoiner`
(`CoordinatorServerJoiner` binds one coordinator to one instance id), so the page itself stays testable
with a stub and never sees the launch machinery.

### Gated by the lock it already had

Join is available only when the list is loaded and the game is not running -- the same lock that
disables editing, because servers.dat is rewritten on exit. That reuse is exact: a second launch over a
running instance is precisely what the lock prevents, so no new gate was needed. A row with a blank
address -- a freshly Added one -- is ignored rather than turned into a plain launch, since joining
nothing is not what the button says.

### Tested

Four servers-page tests: Join is off without a joiner and while the game runs, launches the selected
row's address, and does nothing for an address-less row (the blank guard mutation-verified). Two
coordinator tests: the joiner launches the instance into that server (the passthrough mutation-verified
-- drop the argument and the server never arrives), and a plain launch carries none. Seven existing
fake launchers across both test projects were updated to the new signature.

### Still not proved

- **No server has actually been joined** -- the address reaches `LaunchRequest.Server`, which
  `LaunchCommandBuilder` turns into `--quickPlayMultiplayer`/`--server` (tested since wave 55), but a
  real game connecting to a real server is the standing end-to-end gap.
- **World-join from a list** has no equivalent button; worlds are joined from the command line
  (`--world`) only. And the servers page still has no per-row "joining…" feedback beyond the window
  switching to the log.

## Wave 61 — screenshots: copy path and view folder

The screenshots page could rename, delete and upload (wave 53) but was missing three of upstream's
actions: Copy File, Copy Image and View Folder. This wave adds the two that the port's abstractions can
carry honestly.

### View folder, same as the resource pages

The screenshots folder opens in the file manager through the `IFolderOpener` wave 59 wired, created
first if absent. Nothing new -- the same treatment mods, packs and worlds already got, extended to the
page people most often want to open in a file browser (to attach a shot somewhere the launcher does not
reach).

### Copy path, a documented simplification

Upstream's Copy File puts the file itself on the clipboard, so it can be pasted into a file manager or
a chat's attach box; Copy Image puts the decoded picture there. The launcher's clipboard abstraction
carries text, and the headless build fakes the image decoder, so neither of those is offered as-is.
What is offered is Copy path -- the file's location as text, for an upload dialog's filename field or a
terminal. It is a deliberate divergence, written down as one: the useful part of "Copy File" that a
text clipboard can actually do. Copy Image stays out until there is a real decoder and a bitmap
clipboard to put it on.

### Tested

Five view-model tests: Copy path needs a selection, copies the full file PATH (not the display name,
which hides the .png -- mutation-verified), and reports plainly when there is no clipboard; View folder
is off without an opener, and opens the screenshots folder, creating it first if it was deleted.

### Still not proved

- **Copy Image is not implemented** -- it needs an image decoder the headless build fakes and a bitmap
  clipboard, the same wall the screenshot thumbnail preview hit.
- **Copy File copies a path, not a file object** -- pasting into a file manager as a file is not
  possible through a text clipboard; the path covers the terminal and upload-dialog cases.
- **The clipboard and the file-manager open have not been driven by a human**, like every window here.

## Wave 60 — the last worlds-page actions: datapacks and reset icon

Two more of `WorldListPage`'s actions, which finish the worlds page bar MCEdit (an external editor
launch). Both turned out small once the machinery from the last two waves was in place.

### Datapacks opens the world's own folder

Upstream's `on_actionDatapacks_triggered` does not manage datapacks -- it opens the selected world's
`datapacks` folder in the file manager. Datapacks live inside the world, not the instance, so the path
is `<selected world>/datapacks`, created if absent, opened through the same `IFolderOpener` wave 59
wired. It nags first when the game may be running, reusing the worlds page's existing
`SafeToTouchAsync`: the folder is one the user is about to edit by hand. A mutation that opened the
instance's saves folder instead of the world's own is caught.

### Reset icon deletes the icon Minecraft regenerates

`World::resetIcon` ported: a world's icon is `icon.png` in its folder, and resetting it deletes the
file so the game redraws it from spawn next play. `World` gained `HasIcon` and `ResetIcon`; the latter
returns false when there is nothing to remove, which is not a failure -- upstream disables the action,
and the page gates the button on `HasIcon` rather than just validity for the same reason. The guard is
mutation-verified: drop the "no icon" check and `File.Delete` silently no-ops on the missing file and
reports success, which the test refuses.

### Tested

Two `World` tests: a world with an icon has it removed and `HasIcon` flips; a world with none returns
false. Five view-model tests: datapacks is off without a folder opener, opens the world's own
datapacks folder (per-world path mutation-verified), and stays shut when the running-game nag is
declined; reset icon is off for a world with no icon, and removing one clears the file, reports it, and
turns its own button off once the icon is gone. Both new buttons are on the worlds page.

### Still not proved

- **MCEdit is not ported** -- it launches an external world editor the user has configured, a niche
  action and the last of `WorldListPage`'s. The worlds page is otherwise complete: rename, copy, copy
  seed, delete, add, datapacks, reset icon, view folder, refresh.
- **The file-manager open and the deleted icon have not been seen by a human**, like every window here.

## Wave 59 — View folder

Upstream puts a "View folder" action on nearly every instance page (`ExternalResourcesPage::viewFolder`,
`WorldListPage::on_actionView_Folder_triggered`): open this page's folder in the desktop's file
manager. The port had the machinery -- `IFolderOpener`, used by the main window's "open instance
folder" -- but no instance page offered it. Now the resource pages and the worlds page do.

### Each page opens its own folder

The resource page opens the one folder it manages: `mods`, `resourcepacks`, `shaderpacks` or
`texturepacks` depending on its kind. The mods case opens `mods` specifically -- the folder anything
modern goes in -- not the three-folder scan (coremods, nilmods) the list is built from, which matches
`ModFolderModel::dir()`. The worlds page opens `saves`. The path is exposed as `FolderPath` so a test
can pin exactly which directory each kind resolves to, and a mutation that points resource packs at the
mods folder is caught.

### Created before it is opened

The folder is created first if absent, matching upstream's `ensureFolderPathExists`. A fresh instance
has a `mods` folder but no `resourcepacks` one until something is put there, and a file manager opened
on a path that does not exist is an error rather than an empty window. The create is mutation-verified:
remove it and the "opens a folder that was not there" test fails.

### Wired without a new abstraction

`IFolderOpener` already existed; the instance editor gained one parameter and passes the same
`AppFolderOpener` the main window uses to every resource page and the worlds page. Each grew a "View
folder" button, disabled until the page has loaded (its game root, or its saves path, has to be known
before there is a folder to open).

### Tested

Six view-model tests: each of the four kinds resolves to its own folder (a theory), the button is off
with no opener wired, and opening forwards the exact path to the opener and creates the directory. Two
more on the worlds page: the saves folder is what opens, created if absent, and the button is off
without an opener. The routing switch and the create are both mutation-verified.

### Still not proved

- **The actual file-manager launch is app-side and untested** -- `AppFolderOpener` shells out to the
  platform, which a headless test cannot exercise. What is tested is the path each page chooses and
  that it is handed to the opener, which is where the logic is.
- **The other pages upstream gives a View folder** (screenshots, logs, the instance root) do not have
  one yet; this wave covers the resource pages and worlds, where it is most expected.

## Wave 58 — importing a world from a zip

The worlds page could rename, copy, delete and (wave 57) copy a seed, but not **Add** -- upstream's
`on_actionAdd_triggered`, which imports a world from a zip. The extraction primitives were already
ported (`MMCZip.FindFolderOfFileInZip`, `ExtractSubDir`, zip-slip guarded); nothing tied them to a
world.

### Found wherever it is, named for what it is

`World.Install(zipPath, savesFolder)` ports `World::install` + `WorldList::installWorld`. A world may
sit at the root of the zip or one folder down -- a zip made by "compress this folder" wraps it -- so
the shallowest `level.dat` is located and only that subtree is extracted, the prefix stripped. The
destination folder is named after the world's OWN name from level.dat, deduplicated against what is
already in saves via `DirNameFromString`. Naming it after the zip would drag "Backup (3).zip" into the
saves list and let two different worlds both called "World.zip" collide. A `.mcworld` is a zip under
another extension, so the same path handles both.

### Refused, not half-imported

A zip with no level.dat anywhere is not a world and imports nothing -- the empty offset from
`FindFolderOfFileInZip` means "at the root" or "absent", and a `GetEntry` check tells the two apart. A
zip whose level.dat will not parse as NBT is refused too, matching upstream's `isValid` gate. When
extraction fails partway (a zip-slip entry, an I/O error), the half-written destination is deleted
rather than left in the saves list looking like a real world.

### Wired to the button, and a real selection bug found

The worlds page gained an `IWorldFilePicker` (the desktop half filters `*.zip;*.mcworld`) and an Add
command that picks, calls `Install`, re-reads the folder and lands the selection on the import. Writing
the test surfaced a genuine bug: selecting the imported world by the returned path silently failed,
because the directory scan and the path builder spell the same folder with different separators. Fixed
to select by folder name, which is unique after dedup -- so the imported world is actually selected, not
just present.

### Tested

Eight `World.Install` tests over real zips built with the NBT writer: import at the root, import
wrapped in a folder (prefix stripped), named for the world not the zip, fall back to the zip name when
level.dat has none, a non-world zip imports nothing and leaves saves empty, a corrupt level.dat is
refused, a second import of the same world lands beside the first, and an imported world reads back
valid. Both refusal guards are mutation-verified. Four view-model tests: Add is disabled without a
picker, importing selects the new world, cancelling does nothing, and a non-world file is reported.

### Still not proved

- **Folder import (drag-and-drop) is not done.** Upstream's `dropMimeData` imports a dropped world
  folder or zip; this wave covers the Add button (a zip file) only. The remaining `WorldListPage`
  actions -- Datapacks, MCEdit, Reset Icon, View Folder -- are also still unported.
- **No imported world has been opened by the game**, and the file picker has never been driven by a
  human, like every window in this port.

## Wave 57 — copying a world's seed

`WorldListPage` in upstream carries a row of actions; the port's worlds page had Rename, Copy, Delete
and Refresh but not **Copy Seed**. The number was already there -- `World.Load` reads it from
level.dat, new location (`Data/WorldGenSettings/seed`) first and legacy `RandomSeed` second, the same
fallback upstream uses -- and nothing surfaced it. Now the page does.

### The seed the game would print

`WorldViewModel` gained a `Seed` and a `SeedString` that is the bare number, formatted invariant, which
is what a seed is and what the game's own "seed" command shows. The command copies that string through
the same `IClipboard` the log and screenshot pages already use; a page built without one keeps the
button working but says plainly that there was nowhere to copy to, rather than failing silently.

### Zero is a seed, and so is a negative number

Two edges worth pinning. A world that declares no seed reads back as 0 -- indistinguishable from the
world whose seed genuinely is 0, because level.dat does not tell them apart; upstream copies 0 in both
cases and so does this. And seeds are signed 64-bit: a test copies `-8000000000000000000` and asserts
it round-trips whole, which a fixture that only tried positive numbers would never have caught.

### Follows validity, like its neighbours

Copy Seed is disabled for a world whose level.dat will not parse -- the number lives in the file the
launcher just failed to read, so there is nothing to copy -- exactly as Rename and Copy already gate on
validity. Delete stays allowed on a broken save, because tidying one up is the reason to be here.

### Tested

Five view-model tests over real NBT level.dat files (the fixture writes a genuine
`WorldGenSettings/seed` through the real writer, so it tests the reader, not itself): the seed is read,
copying puts the bare number on the clipboard, a negative seed survives, a broken world's seed cannot
be copied, and a missing clipboard is reported. Both load-bearing guards are mutation-verified -- blank
the seed source and two tests fail; drop the validity check and the broken-world test fails. Wired into
the worlds page as a "Copy seed" button.

### Still not proved

- **The rest of upstream's world actions remain unported**: Add (import a world zip), Datapacks, MCEdit
  (external editor), Reset Icon, View Folder. Join-a-world is covered from the launch side by wave 55's
  `--world`.
- **The clipboard has never been exercised by a human** -- the app clipboard is wired, but like every
  window in this port, nobody has watched a real copy land.

## Wave 56 — the legacy texture-pack page

The port had parsers for all five resource kinds -- resource packs, data packs, texture packs, shader
packs, world saves -- but only three had a page. `ModsPageViewModel` already served Mods, ResourcePacks
and ShaderPacks from one class; `TexturePackUtils` sat fully tested and unused by any UI. Upstream has
a dedicated `TexturePackPage` for the pre-1.6 format (a zip or folder with a `pack.txt`, no
`pack.mcmeta`). This wave adds the fourth kind.

### One more ResourceFolderKind, not one more class

Texture packs manage exactly like the other pack folders -- list, enable by rename, remove, refresh,
accept drops -- so they are a fourth `ResourceFolderKind` rather than a new view-model, which is the
same consolidation the mods page already made over upstream's three near-identical page classes. A
`ResourceFolder.LoadTexturePacks` scanner parses each entry's `pack.txt` (a pack that will not read
still gets a row, named after its file, for the reason every folder here does). Title, the thing-name
in status messages, the download label and the rebuild branch each gained their texture-pack arm.

### Shown by folder presence, a deliberate divergence

Upstream shows the page only when the resolved profile carries the `texturepacks` trait
(`TexturePackPage::shouldDisplay`). That trait comes from resolving the component stack, which the
instance window does not do at open time -- a cold instance would need the metadata server. So the
port shows the page when the instance actually **has** a `texturepacks` folder on disk. The
consequence matches upstream's intent where it matters: a modern instance, which has no such folder,
never shows the page; a legacy instance that already holds texture packs does. Documented in the code
as the divergence it is.

### No download button, honestly

`ResourceType` has no `TexturePack` value -- the modern platforms this port searches do not serve the
legacy format -- so the page is created without an installer and the Add button is absent rather than
present and inert. Upstream's own download dialog is a legacy CurseForge category; wiring that is a
separate, larger job and is noted, not faked.

### Tested

Eight view-model tests: it names itself after the format, reads `texturepacks` and not
`resourcepacks` (mutation-verified -- point the branch at the wrong folder and the test fails), lists
both zip and folder packs, disables by rename and deletes on disk, and picks up a pack dropped in from
outside on refresh. Two loader tests: `LoadTexturePacks` scans both shapes and marks them valid, and a
missing folder is empty rather than an error -- the case every modern instance is in. The parser
itself was already covered by real upstream fixtures.

### Still not proved

- **No texture-pack download.** Upstream can fetch them from CurseForge's legacy category; the port
  cannot, so the Add button is absent. The other four managing actions all work.
- **The trait gate is approximated by folder presence.** An instance that declares the trait but has
  never had a texture pack put in it would not show the page until one is added -- acceptable, since
  there is nothing to manage until then, but not upstream's exact rule.
- **The page has never been seen by a human**, like every window in this port.

## Wave 55 — joining a world from the command line, and the CLI's arguments get tests

Upstream's launch surface has `--server` **and** `--world` (Application.cpp). The library already
resolved a world end to end -- `LaunchTarget`, `--quickPlaySingleplayer`, the GUI arg parser -- but
the headless CLI only ever exposed `--server`. Now it exposes `--world` too, and in wiring it a real
fidelity bug surfaced.

### The bug: the port chose the wrong target when both were given

The game takes one `--quickPlay` target, so when both a server and a world are set something has to
win. Upstream picks the **server** -- `if (!m_serverToJoin.isEmpty()) ... else if (world)`, in both
Application.cpp:393 and :1200. The port did the opposite (world first) and, worse, carried a doc
comment *claiming* upstream resolved "world first". No test pinned it, so nothing had caught the
claim being false. The Qt source is the contract: fixed to server-first, comment corrected to quote
the upstream branch.

### The rule now lives in one place

The choice was an inline ternary inside `LauncherService`, invisible to a test without standing up a
whole launch. Pulled it into `LaunchTarget.Choose(server, world)` -- a pure, side-effect-free factory
the CLI and the window both funnel through, so the two front-ends cannot drift on which target wins.
`AServerWinsOverAWorldWhenBothAreGiven` fails if `Choose` is reordered (verified by mutation); a
theory pins that empty strings, not just nulls, mean "no target".

### The CLI got its first test project

`Program.cs` is deliberately thin wiring, but pulling flags out of an argument list -- in any order,
without a stray `--dry-run` becoming the instance id -- is the one piece of genuine logic it owns, and
it had no tests. Extracted the launch-request assembly into an `internal static ParseLaunchRequest`
(no data dir, no network, no game) and stood up `ExtremeLauncher.Cli.Tests`, wired through
`InternalsVisibleTo` the way the other libraries expose their internals. 16 tests: flag order does not
matter, the documented `--offline --dry-run` trap does not swallow the flag as a name, a missing
`--java` is an empty string not null, and `--server`/`--world` are both carried through untouched for
`Choose` to decide. The `TakeOption` end-of-list guard (`index + 1 >= Count`) is mutation-verified --
remove it and the last-token case throws instead of returning null.

### Still not proved

- **No world has actually been opened by a launched game.** The chain is tested link by link --
  `--world` reaches `LaunchRequest.World`, `Choose` prefers the server, `LaunchTarget.World` becomes
  `--quickPlaySingleplayer` under the singleplayer trait -- but a real end-to-end launch into a world
  is the same standing gap every launch path has.
- **`--profile` is still unsupported.** Upstream selects an account by profile name; without account
  UI the offline session is always used, so the GUI arg parser lists it as unsupported and the CLI
  omits it. Unchanged this wave.

## Wave 10 — deleting an instance, and three buttons that were lying

A screenshot of the working window showed a toolbar reading **Launch · Edit · Copy · Delete**. Edit,
Copy and Delete bound `IsEnabled` to `CanEdit` and had **no `Command` at all**: they lit up when an
instance was selected and did nothing whatsoever when clicked.

That is the exact behaviour `LaunchCoordinator`'s own comments call the worst of the three available —
*"A Play button that silently does nothing is the worst of the three possible behaviours ... because
the user cannot tell it from a hang"* — written earlier the same day, three buttons along.

Edit and Copy are now **disabled with a tooltip saying why**. Delete is implemented.

### `FS::trash`, which was one line of Qt and is none of .NET

`Core/Trash.cs`. `QFile::moveToTrash` has no BCL equivalent, and this file had been carrying a note
saying so since wave 1. It matters because the alternative is a Delete button that destroys a modded
instance, with its worlds, permanently and instantly.

- **Windows**: `Microsoft.VisualBasic.FileIO.FileSystem`, which ships in the shared framework.
  Verified for real: a probe file was trashed and then found in the Recycle Bin by
  `Shell.Application`, with its original location intact.
- **Linux**: the freedesktop.org spec — `files/` plus a `.trashinfo` in `info/`, the info file written
  **first** so a failure cannot leave an orphan the desktop's trash viewer shows as unrestorable.
- **macOS**: a move into `~/Trash`.

Linux and macOS are **written but unrun**, like everything else outside Windows in this port.

> **The first version of this was hand-rolled P/Invoke to `SHFileOperationW`, and it crashed the test
> host with an access violation (0xC0000005).** `SHFILEOPSTRUCT` is packed on x86 and naturally aligned
> on x64; `Pack = 1` corrupts the stack of a call whose entire job is to delete things. The framework's
> implementation is the same shell call with the marshalling already right. An odd-looking namespace is
> a small price for not writing interop on the destructive path.

Trashing is **allowed to fail** and every caller handles it: upstream returns false outright under
Flatpak and on Windows Server, and a filesystem with no reachable trash is a normal state for a
launcher on a USB stick — which is this lineage's whole reason for being portable.

#### Upstream bug #18, in `InstanceList::trashInstance` and `deleteInstance`, FIXED here

Both remove the instance from the group index and **save `instgroups.json`** *before* touching the
filesystem, then return false when the removal fails. The instance is still on disk, still listed —
and silently no longer in its group, with that loss already written out.

Trash failure is not exotic; upstream itself returns false for it in two named cases. So the user
presses Delete, is told it did not work, and quietly loses their grouping.

Nothing is recorded here until the directory has actually moved. The test provokes it properly, by
holding a file inside the instance open with `FileShare.None` so the move genuinely fails with the
instance present — **an earlier version used an id that did not exist, which returns at the null check
and never reaches the ordering at issue; it would have passed against the bug.** Confirmed by
restoring upstream's order and watching it fail.

### Asking before doing something irreversible

`ViewModels/InstanceRemoval.cs`. The order is: **trash it if the platform can, and only ask about
permanent deletion once trashing has been tried and failed.** Asking "delete permanently?" up front
trains people to click through a dialog that occasionally means what it says.

- The question **names the instance the user sees**. "Delete this instance?" beside a grid of similar
  tiles is a question about which one.
- It uses **this desktop's word** — "Recycle Bin" on Windows, "Trash" elsewhere. A Windows user sent
  looking for the "trash" will not find it, and recoverability is the entire message.
- Undo is offered **only where it can work**. Windows does not report where an item landed, so no Undo
  button appears there; the Recycle Bin's own Restore is the platform's answer.
- The default prompts answer **no to everything**, so a front-end that forgets to wire dialogs fails
  safe. `DialogPrompts` makes closing, Escape, and a dialog that cannot be shown all mean no; Return
  activates Cancel, and the destructive button is coloured rather than made default so a stray Return
  can never trigger it.

Upstream has no tests for any of this, because every branch ends in a `QMessageBox` and a dialog is
only testable by clicking it. With the prompts behind an interface, the test that matters — answer
"no", then check the worlds are still on disk — is three lines.

Avalonia ships no message box at all, unlike Qt where `QMessageBox::question` is one line, so
`App/DialogPrompts.cs` builds one rather than taking a dependency for a single confirmation.

### Copying an instance

`ViewModels/InstanceDuplication.cs`. The work itself is `InstanceCopyTask`, ported in wave 7 with its
three strategies and its tests; what was missing was the flow around it.

`InstanceCopyTask` now implements `IInstanceTask`, which is how upstream has it — there,
`InstanceCopyTask` simply *is* an `InstanceTask` and inherits the staging path. This port had split the
two, so the path became a settable property that `InstanceStagingTask` assigns before the copy runs.

> `IInstanceTask.Name` is implemented **explicitly**, because `LauncherTask.Name` already means
> something else: the task's display name, here "Copying instance X". Hiding one behind the other would
> have given the staging wrapper a folder called *"Copying instance X"*.

It builds in staging and commits, rather than writing into the instances folder directly — upstream's
arrangement, and the reason is failure: a copy that dies half way would otherwise leave a directory
that looks like an instance and is not.

**Everything is copied by default** — worlds, mods, resource packs, screenshots, servers, options. A
Copy button that quietly left the worlds behind is the opposite of what it is for; copying is precisely
how people protect a world before experimenting on it. The finer per-item choices belong to the copy
dialog, which is not ported.

The name prompt is prefilled with the **original** name, as upstream's dialog is. Two instances may
share a display name; only the directory has to be unique, and `DirNameFromString` sees to that.

> `CommitStagedInstance` now reports the id it settled on, and `InstanceStagingTask` exposes it as
> `CommittedId`. The window selects the copy after making it, and **the id is not the display name** —
> when something already occupies the directory, `DirNameFromString` appends "(1)". My first version
> searched the list for a matching name and took the longest id, which is a guess that picks the wrong
> instance the moment two of them share a name.

`PromptForTextAsync` returns **null for cancelled and empty for a cleared box**, which are different
answers: conflating them turns "I changed my mind" into "make one called nothing". Its OK button *is*
the default, unlike the confirmation dialog's — the affirmative answer here creates something rather
than destroying it, so Return doing it is what a person expects.

### Still not proved

- **Neither dialog has ever been seen.** The view-model logic for both is tested, and the trash is
  verified against the real Recycle Bin, but nobody has watched a confirmation appear or clicked its
  buttons. Everything about the two dialogs themselves — focus, Escape, the destructive button's
  colour, whether they are even legible — is unverified.
- Copying a **280 MB instance** — 200 mod jars and 40 region files, shaped like a real pack — was
  measured at ~800 ms, with the payload byte-identical: 240 files and 280 MB on both sides. The only
  difference is `instance.cfg`, which the copy deliberately rewrites with the new name and icon key.
  (My first probe compared total bytes and reported a mismatch because of exactly that; the crude
  comparison was wrong, not the copy.) An instance directory is **small** in this launcher — libraries
  and assets live in the shared data root — so the gigabyte case is a modded instance with big worlds,
  which is what that fixture imitates.
- Edit is still honest-but-disabled. It needs the instance window: version, mods, settings, notes,
  screenshots, worlds and logs pages. That is the largest unported piece of the UI.
- The startup warning for unsupported command-line options still reports into the launch log, which the
  next launch clears.


## Wave 11 — the instance window begins: the Version page

`ViewModels/VersionPageViewModel.cs`, ported from the decision-making half of
`ui/pages/instance/VersionPage.cpp`.

**This is the page that breaks instances.** Every other page edits files the game reads; this one edits
what the game *is* — remove the wrong row and a working modpack stops launching. Upstream guards it
with `updateButtons()`: forty-odd lines re-deriving eight buttons' enabled states, called from six
places, where missing one call leaves a button acting on a row that is no longer selected. Here each
button is a property of the selection, worked out once.

The rows are rebuilt from the profile after every edit rather than patched in place, because **a
component's problems depend on its neighbours** — removing Fabric makes every Fabric mod's requirement
unsatisfied, so anything short of a re-read leaves stale verdicts on screen next to a changed instance.

### I wrote the rules from memory and two of the three were wrong

Worth recording plainly, because they all looked right:

| rule | what I assumed | what `Component.cpp` says |
| --- | --- | --- |
| Remove | `!important` | `!important` ✓ |
| Change version | "is not custom" | **the version list exists and has entries** |
| Move | list position | **hardcoded `true`**, under its own "HACK, FIXME" |

"Is not custom" is a different question with a different answer, and would have offered the button for
components with nothing to choose from. Both are now read off upstream and pinned by tests.

My file header also claimed *"moving is by ORDER, not list position, and two components can share an
order"*. `PackProfile::move` is a `swapItemsAt` on the list — my own implementation was already
faithful; the comment I wrote above it was not. Same class of error as the `SearchArgs` comment earlier
in this port: an assertion about semantics that the code underneath does not make.

#### Upstream bug #19, in `PackProfile::move`, NOT REPRODUCED

The target index is clamped in a way that wraps in **one direction only**:

```cpp
if (theirIndex >= rowCount()) theirIndex = rowCount() - 1;   // down from the last: no-op
if (theirIndex == -1)         theirIndex = rowCount() - 1;   // up from the first: THE LAST ROW
```

So **Move Up on the top component swaps it with the bottom one**, while Move Down on the bottom does
nothing at all. It is reachable, not theoretical: `isMoveable()` is that hardcoded `true`, so the
button is enabled on the first row. Component order decides which patch wins, so sending Minecraft from
the top of the list to the bottom is not cosmetic.

This port returns early at both ends instead. The existing `MovingOutOfRangeIsANoOp` test already
asserted that, but recorded nothing about upstream differing — which is exactly how someone later
"fixes" it back into the bug. It is now named, explained, and uses **three** components, because with
two a wrap and an ordinary swap look identical.

The page's `CanMoveUp`/`CanMoveDown` guard on position, which is a deliberate divergence: upstream's
always-enabled Move Up is a button that cannot do what it says, and removing those is a running theme
of this port.

### The window around it

`ViewModels/InstanceWindowViewModel.cs` and `App/InstanceWindow.axaml`. Edit is no longer disabled.

**What upstream's page container does that matters**: each page has an `apply()` called when you leave
it, and the container calls every page's `apply()` on close. Miss that and a user's edits vanish when
they shut the window — the most annoying way a settings screen can fail, because nothing appears to go
wrong at the time.

> **Upstream's `BasePage::apply()` returns void.** A page that could not write its file has no way to
> say so, and the window closes anyway — discarding exactly the edits the user asked to keep. Here
> `Save()` returns a bool, a failed save **keeps the window open**, and the failing page is named. That
> is the case with the most tests behind it.

Every dirty page is attempted, not just up to the first failure: one page failing is no reason to
abandon another page's work.

Closing is **asked about** rather than saved silently. On this window a silent save means an instance
that no longer launches, if the edit was a mistake. The code-behind cancels the first close
unconditionally — a dialog's answer arrives long after `OnClosing` has returned, so there is no way to
answer "should I close?" in time — then closes again for real if the view model allows it. That is the
only code-behind in the window.

`NotesPageViewModel` is there as the second page because it is the whole save path in miniature:
twenty lines upstream, edits `instance.cfg`, and can fail to write. It proves the container works with
something whose correctness is obvious, next to something whose correctness is not. Its dirty state is
**compared against what was loaded**, not a flag set per keystroke — typing a character and deleting it
again leaves nothing to save, and a flag would still prompt on close, which is how people learn to
dismiss that prompt without reading it.

`AppInstanceEditor` keeps **one window per instance** — two windows over the same `instance.cfg` would
each hold their own copy of the settings and the last save would silently win — and **reads the profile
without resolving it**, so opening the version page needs no metadata server. Someone looking at an
instance that will not launch is often looking precisely because the network or the metadata is the
problem.

### The UI is tested now

`tests/ExtremeLauncher.App.Tests`, on `Avalonia.Headless.XUnit` — the real windows, rendered with no
display attached. This closes a gap carried for four waves: the build type-checks compiled bindings and
stops there. It cannot tell whether a `DataTemplate` resolves, whether a button reaches the command it
names, or whether a window even constructs.

**It found a real bug on its first run.** The **Launch button had no `IsEnabled` binding at all.** It
relied on the command's `CanExecute`, and a plain `[RelayCommand]` always returns true — so Launch was
live with nothing selected, live for an instance that cannot run, and live during a launch. The command
guards itself, so pressing it did nothing whatsoever.

That is the same defect as the dead Edit/Copy/Delete buttons, on the most important button in the app,
shipped again three waves later. `MainWindowViewModelTests` had checked `CanLaunch` as a property since
wave 10; nothing had ever checked that the XAML bound to it. **A view-model test cannot see this class
of bug, and that is the whole argument for this project.**

What is covered now:

- Every toolbar button has a non-null `Command` — a `[Theory]` over the four labels, so a dead button
  is a failing test rather than a screenshot.
- Enabled states read off the **rendered** buttons: unsupported instances cannot be launched but can be
  deleted; Edit is disabled in a build with no instance window.
- The instance window's page `DataTemplate`s resolve. When one fails to match, Avalonia shows the type
  name as text, which looks like a rendered page until you read it — so the test asserts the components
  appear *and* that "VersionPageViewModel" does not.
- Closing the instance window with unsaved notes goes through the real `OnClosing`: cancel, ask, save,
  close. A failing save leaves the window open.
- **The confirmation dialog, opened and answered.** Its rules had been argued for in comments and never
  executed: Escape means no, closing means no, no-owner means no, and the destructive button is *not*
  the default so a stray Return cannot delete an instance. Verified by breaking two of them — making
  the destructive button default, and defaulting `answered` to true — and watching the right tests
  fail.

> Tests that mutate a view model then read the controls need a layout pass first (`RunJobs()` +
> `UpdateLayout()`): bindings post their updates and item containers are created during layout, so
> reading straight afterwards sees the tree from before the change. My first search-filter test failed
> for exactly this reason and I nearly recorded it as a bug — the intermediate assertion on the view
> model is what separated "the window did not follow" from "the filter is wrong".

### Still not proved

- Change-version and add-loader landed in waves 16/25; **customise and revert landed in wave 49** as
  ported `PackProfile`/`Component` operations, proven through the resolution path. The Version PAGE
  still cannot surface customise/revert (it holds unresolved components, so `IsCustomizable` reads
  false), and **edit-JSON** (open the patch in an external editor) is still not ported.
- The new-instance dialog is covered headlessly now, but **no human has looked at any window in this
  port** since the two screenshots in wave 10. Headless rendering proves structure and bindings; it
  says nothing about whether anything is legible or sensibly laid out.
- A created instance is **never resolved until it is launched**, so a loader that turns out to be
  incompatible fails then rather than at creation. That is upstream's behaviour and the reason the
  lists are filtered, but the filtering is the only guard.
- **The file picker has never been opened.** `AppPackImporter` is wired and the button is tested
  headlessly, but a real `StorageProvider` dialog cannot be driven from a headless test, so the picking
  half is unverified — only what happens after a path comes back.
- **Only Modrinth packs import.** CurseForge, Technic, FTB and ATLauncher are all parsed and tested in
  `ModPlatform` and have no creation task. CurseForge is the biggest gap of those — its manifest names
  files by project and file id, so importing one needs the Flame API rather than a list of URLs.
- ~~Import has no **progress in the window**.~~ *Fixed in wave 44, along with the pack browser and the
  pack updater, by one shared ProgressWindow.*
- ~~The launcher's log is never shown *in* the launcher.~~ *Done in wave 46: the same reader, pointed
  at `<root>/logs`, with the Upload button on it.*
- ~~Screenshots cannot be uploaded anywhere~~ *(upload landed in wave 53: imgur, key-gated, single
  image or album)*, and are still **not previewed** — the list shows names and dates. A thumbnail grid
  is what upstream has, and the headless decoder fakes images so it needs a human-in-the-loop pass.
- The Servers page cannot add, edit or remove entries; that needs a `servers.dat` writer, and writing
  a file the game owns is the one place where getting it wrong loses the whole multiplayer list.
  *(Done in wave 35, guarded by exactly that concern.)*
- ~~The worlds page cannot **copy** a world.~~ *Done in wave 47: a folder copy plus the level.dat
  rewrite, because Minecraft shows the name from inside the file and never the folder.*
- The Mods page cannot **add** a mod. Dragging a jar in, or downloading one from Modrinth or CurseForge,
  needs the resource-download dialog on top of the already-ported `ModPlatform` layer.
- The headless platform renders without a real compositor or a font stack worth the name. It proves
  structure, bindings and behaviour; it does not prove anything **looks** right.
- Still nothing outside Windows.

### Gotchas found while porting

- `DefaultVariable` tracks *explicitness* separately from *is-default*. Assigning the default value
  still marks it explicit — that is the only reason `group:artifact:1.0@jar` round-trips.
- `QChar::toLower()` is locale-independent; `char.ToLower()` is not. Use `ToLowerInvariant` in ported
  comparison code or Turkish locales diverge.
- `InvariantGlobalization` must stay **off** — `NaturalCompare` mirrors Qt's `localeAwareCompare`, and
  instance names are user-supplied and often non-ASCII.
- The Qt harness for the FlexVer vectors drops the last line of the file (`atEnd()` is checked before
  the final line is processed). The C# tests read every line.
