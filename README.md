# Pine3 Light Control

Windows tray app (WPF, .NET 10) that controls the PC's Bluetooth LED strips and
the Home Assistant lights you choose, from one control panel. It's the sibling of
[Pine3 Monitor](https://github.com/pinheirofilipem-ux/pine3-monitor) and uses the same look.

made by magnificent3

## What it does

- **Bluetooth LED controllers:** the controllers used by the **LED Hue** app
  (Sperll SP110E / SP105E family). It talks to them directly over Bluetooth LE, so the
  phone isn't needed: on/off, brightness, color, 120 effects + speed, white channel
  (RGBW strips), and the strip setup (LED chip, color order, number of LEDs, name).
  It reconnects automatically if a controller drops.
- **Home Assistant:** connects with a long-lived access token over the WebSocket API,
  with live state updates. You pick the entities to show: lights (brightness, color,
  color temperature, effects, depending on what each light supports), switches,
  input booleans, fans, scenes, scripts and buttons.
- **Tray:** left-click opens the panel. Right-click toggles each light, turns all on
  or off, and runs scenes and scripts. Closing the window keeps the app in the tray.
- **Startup:** optional "Start with Windows" (per user, no admin needed).

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
2. Commit, then tag and push: `git tag v0.1.0 && git push origin v0.1.0`.
3. GitHub Actions (`.github/workflows/build.yml`) builds and tests on Windows and creates
   the release with the installer. Every other push is built too.

## First use

**Bluetooth:** close the LED Hue app on the phone (the controller accepts one
connection at a time). Open **Bluetooth → Scan**, then **Add** the controller.
It shows up on the Lights page. If the colors come out wrong, set the LED chip,
color order and number of LEDs, then press **Send to controller**.

**Home Assistant:** in Home Assistant, open your profile → Security → Long-lived
access tokens → Create token. In the app, go to **Home Assistant**, fill in the address
(e.g. `http://homeassistant.local:8123`) and the token, then press
**Save and connect**. Tick the entities you want under "What to control".
The token is stored encrypted for your Windows user (DPAPI).

## Where things live

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Pine3 Light Control` (per-user install) |
| Settings | `%APPDATA%\Pine3LightControl\config.json` |
| Log | `%APPDATA%\Pine3LightControl\pine3lightcontrol.log` |

## Project layout

```
src/Pine3LightControl.Core   no Windows APIs: SP110E protocol, Home Assistant client,
                             light model, config (testable anywhere)
src/Pine3LightControl        WPF app: tray, control panel, Bluetooth LE via WinRT
tests/                       xUnit: protocol, send queue, fake BLE link, fake HA server
installer.iss                Inno Setup script
build.bat                    tests + build + installer
```

## SP110E protocol (summary)

GATT service `FFE0`, characteristic `FFE1` (write + notify). Each command is 4 bytes
`[d0, d1, d2, cmd]`:

| Command | Bytes |
|---|---|
| On / Off | `00 00 00 AA` / `00 00 00 AB` |
| Color | `RR GG BB 1E` |
| Brightness | `VV 00 00 2A` |
| White (RGBW) | `VV 00 00 69` |
| Effect 1–120 / auto | `NN 00 00 2C` / `00 00 00 06` |
| Speed | `VV 00 00 03` |
| LED chip / color order | `II 00 00 1C` / `OO 00 00 3C` |
| Number of LEDs | `HI LO 00 2D` |
| Read state | `00 00 00 10` → 12-byte notification |
| Check device (handshake) | `xx yy zz D5` → 13-byte notification |

Sources: the mbullington "SP110E protocol" gist and the `roslovets/SP110E` Python driver.
The **Show GATT services** button on the Bluetooth page lists a device's services, to
diagnose controllers that use a different protocol.
