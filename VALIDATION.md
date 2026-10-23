# Validation of 2.5.1

Tested on 2026-10-23 on Windows 11 build 26200, x64, using Visual Studio MSBuild 18.9 and .NET SDK 10.0.400. Both the .NET 9 Windows Desktop and .NET Framework 4.8.1 targets were built and exercised on the real desktop.

The solution builds without warnings or errors. The real Explorer harness passed all 12 scenarios on each runtime:

| Scenario | .NET 9 | .NET Framework 4.8.1 |
| --- | --- | --- |
| `/select` creates a tab and selects the file | Pass | Pass |
| `/select` reuses a tab and changes the selection between `sample.txt` and `sample.md` | Pass | Pass |
| `SHOpenFolderAndSelectItems` multi-selection | Pass | Pass |
| Shell API subfolder selection | Pass | Pass |
| Shell API selection in a newly created tab | Pass | Pass |
| Ordinary empty folder merges into the target window | Pass | Pass |
| Incoming selection activates the correct inactive tab | Pass | Pass |
| Repeating the identical selection activates an inactive tab | Pass | Pass |
| First reveal into a fresh folder | Pass | Pass |
| Immediate second reveal reuses that tab | Pass | Pass |
| Disabling reuse creates an additional tab with the requested selection | Pass | Pass |
| Shutting down during conversion reveals and preserves the native source | Pass | Pass |

The test fixture contains Japanese text, spaces, and `#` in its path. Assertions read full selected paths through Shell COM, check the actual active `ShellTabWindowClass` handle, and ensure converted fixture tabs share a single window. The test runner backs up and restores the existing settings file and closes only its own fixture windows.

Normal request completion is event driven. Native Explorer startup and navigation still take time: observed end-to-end new tab requests took roughly 2.5–5 seconds on this desktop; direct requests to already loaded tabs typically took 35–450 ms. These are observations, not performance guarantees. There is no fixed selection capture delay or restoration polling in the implementation.

The folder view emitted `DocumentComplete`, followed by the initial selection change, followed by `EnumDone`. A destination monitor also checks the expected navigation, so a previous Home enumeration cannot satisfy a new folder request. Event sinks are detached with `using`/`finally` and asynchronous task continuations keep restoration out of enumeration callbacks.

Virtual/network folder behavior, other Windows builds, third-party Shell namespace providers, and Explorer restarts were not exhaustively tested. A missing enumeration event reaches a ten-second failure deadline and leaves the native source window available. An ordinary successful open does not wait for that deadline.

Build and rerun instructions are in [README.md](README.md).
