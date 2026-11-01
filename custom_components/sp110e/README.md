# SP110E LED controller — Home Assistant integration

Controls Sperll **SP110E / SP105E** Bluetooth LED controllers (the ones used by the
**LED Hue** phone app) from Home Assistant, through Home Assistant's own Bluetooth:
the host's adapter or any **ESPHome Bluetooth proxy**.

Made for Pine3 Light Control: once the strip is a light in Home Assistant, the Windows
app controls it from its Home Assistant page. That also works on PCs whose Bluetooth
stack can't talk to these controllers.

## What you get

- A **light** with on/off, brightness, RGB color and 122 effects ("Static", "Auto",
  "Effect 1"–"Effect 120").
- **Effect speed** (0–255), plus a **White channel** on RGBW strips.
- Automatic discovery: Home Assistant shows "SP110E discovered" by itself.

## Install (HACS)

1. HACS → ⋮ → **Custom repositories** → add
   `https://github.com/pinheirofilipem-ux/pine3-lightcontrol`, type **Integration**.
2. Search for **SP110E LED controller** in HACS, download it, and **restart** Home Assistant.
3. Settings → Devices & services: accept the discovered **SP110E**, or
   **Add integration → SP110E LED controller**.

Manual install: copy `custom_components/sp110e` into your Home Assistant
`config/custom_components/` folder and restart.

## Notes

- The controller accepts **one connection at a time**. The integration connects
  when it needs to and disconnects after 60 s without commands, so the LED Hue app
  keeps working in between.
- State is read when it starts, after each command, and every 10 minutes.
- Range matters. If the light becomes unavailable or slow, put an ESP32 running
  ESPHome with `bluetooth_proxy:` (active connections enabled) near the controller.
  The integration uses it automatically.
- The LED chip, color order and number of LEDs are set with the LED Hue app or
  from Pine3 Light Control's Bluetooth page. This integration doesn't change them.

## Protocol

GATT service `FFE0`, characteristic `FFE1` (write + notify). 4-byte commands
`[d0, d1, d2, cmd]`. The full table is in the main README. The controller
*advertises* service `FFB0`, but its GATT table contains `FFE0`/`FFE1`.
