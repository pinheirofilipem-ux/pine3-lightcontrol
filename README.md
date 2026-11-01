# Pine3 Light Control

Windows tray app (WPF, .NET 10) with a control panel for the lights you have in
Home Assistant, and **scenes** that combine them. It's the sibling of
[Pine3 Monitor](https://github.com/pinheirofilipem-ux/pine3-monitor) and uses the same look.

made by magnificent3

## What it does

- **Home Assistant:** connects with a long-lived access token over the WebSocket API,
  with live state updates. You choose which entities to show: lights (brightness, color,
  color temperature, effects, depending on what each light supports), switches,
  input booleans, fans, scenes, scripts and buttons.
- **PC RGB (OpenRGB):** graphics card, memory and any other device OpenRGB sees, through
  its SDK server (TCP 6742): on/off, brightness and color. Devices with the same name
  (e.g. four memory modules) are controlled as one light.
- **Scenes:** set the lights the way you want, tick the ones that belong to the scene and
  save it. One click (or the tray) puts every light back the same way: on/off,
  brightness, color or color temperature, effect, and Home Assistant scenes/scripts to run.
- **Tray:** left-click opens the panel. Right-click activates scenes, toggles each light
  and turns everything on or off. Closing the window keeps the app in the tray.
- **Startup:** optional "Start with Windows" (per user, no admin needed).

## SP110E / LED Hue Bluetooth controllers

These controllers are added through Home Assistant with the integration in
[`custom_components/sp110e`](custom_components/sp110e/README.md), which you install from HACS.
Some PCs can't read these controllers' GATT table: the connection is made but service
discovery never answers, on any adapter and in Chrome too, while a phone reads it in a
fraction of a second. Home Assistant (Linux) controls them fine, and from there they're
ordinary lights for this app.

## Build

1. Run `build.bat`. It installs the .NET 10 SDK through winget if needed, runs the
   tests, publishes to `dist\app`, and, if Inno Setup 6 is installed, creates
   `dist\Pine3LightControl-Setup-<version>.exe`.
2. Run the installer. It installs for the current user without asking for admin.

The code also compiles on Linux or macOS (`EnableWindowsTargeting`), and the tests run there:
`dotnet test tests/Pine3LightControl.Tests`.

## Release

1. Bump the version in `src/Pine3LightControl/Pine3LightControl.csproj` (`<Version>`)
   and `installer.iss` (`AppVersion`).
2. GitHub Actions → **Build** → **Run workflow** with **release** ticked (or push a tag
   `v<version>`). It builds and tests on Windows and publishes the installer as a
   pre-release while the version is 0.x.

## First use

In Home Assistant, open your profile → Security → Long-lived access tokens → Create token.
In the app, go to **Home Assistant**, fill in the address
(e.g. `http://homeassistant.local:8123`) and the token, then press **Save and connect**.
Tick the entities you want under "What to control". The token is stored encrypted for
your Windows user (DPAPI).

For the PC's own RGB, install [OpenRGB](https://openrgb.org) and run it as administrator
(the memory needs it), start its SDK server (SDK Server → Start Server; in Settings turn on
Start at login, Start minimized and Start server), and close iCUE and the AMD RGB tool.
In the app, open **PC RGB**, tick **Use OpenRGB**, press **Save and connect** and tick the
devices to show.

Then create scenes on the **Scenes** page.

## Where things live

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Pine3 Light Control` (per-user install) |
| Settings and scenes | `%APPDATA%\Pine3LightControl\config.json` |
| Log | `%APPDATA%\Pine3LightControl\pine3lightcontrol.log` |

## Project layout

```
src/Pine3LightControl.Core   no Windows APIs: Home Assistant and OpenRGB clients,
                             light model, scenes, config (testable anywhere)
src/Pine3LightControl        WPF app: tray, control panel
tests/                       xUnit: send queue, scenes, fake HA and OpenRGB servers
custom_components/sp110e/    Home Assistant integration for SP110E (HACS)
tests/ha, tests/ha_integration   Python tests of the integration
installer.iss                Inno Setup script
build.bat                    tests + build + installer
```
