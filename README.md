# ExplorerTabUtility

Keep Windows 11 File Explorer windows together as tabs while preserving the selected file, keyboard focus, and scroll position from **Show in folder / Reveal in Explorer**.

This is an independently maintained version of [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility), with improved selection handling and responsiveness.

## Install

1. Download the ZIP for your device from the [latest release](https://github.com/sheetau/ExplorerTabUtility/releases/latest): **x64** for most PCs, or **arm64** for ARM-based PCs.
2. Extract the entire ZIP into a writable folder and run `ExplorerTabUtility.exe`. Keep the accompanying files together.

Windows 11 with native File Explorer tabs is required. The necessary runtimes are included. Exit any other ExplorerTabUtility instance before starting this version.

## Use

- **Window hook** converts new Explorer windows into tabs. Hold Ctrl+Shift while opening a location to keep it in a separate window.
- **Reuse tabs** switches to an existing tab when the same folder is already open.
- **Shortcuts** lets you configure keyboard and mouse actions. Expand a row to edit it, or use **Add shortcut** below the list. Changes are saved automatically.
- **Preferences** controls startup, update notifications, closed-tab history, and other options.

Open settings from the tray icon or by running the executable again. Closing settings keeps the app running; choose **Exit** from the tray menu to quit.

The taskbar jump list's **File Explorer** command and identified pinned folders can open tabs directly. File items and unrecognized entries retain normal Windows behavior.

## Updates and settings

Enable **Check for updates** to check GitHub Releases at startup and while the app is running. Open settings from an update notification and choose **Update and restart** to install the new version while keeping your settings. You can also choose **Check for updates** from the tray menu.

To update manually, exit the app and extract the new ZIP into the same folder. Settings are stored in `%APPDATA%/ExplorerTabUtility/settings.json`.

Downloads and updates come from [this repository](https://github.com/sheetau/ExplorerTabUtility/releases). Upstream's WinGet and Chocolatey packages install the w4po version.

The original application is by w4po and upstream contributors. Released under the [MIT license](LICENSE).
