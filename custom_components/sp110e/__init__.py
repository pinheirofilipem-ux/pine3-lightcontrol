"""Integração SP110E (controladores LED da app "LED Hue") por Bluetooth."""

from __future__ import annotations

from datetime import timedelta
import logging

from homeassistant.components import bluetooth
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_ADDRESS, CONF_NAME, Platform
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import ConfigEntryNotReady
from homeassistant.helpers.event import async_track_time_interval

from .const import REFRESH_INTERVAL_SECONDS
from .device import Sp110eDevice

_LOGGER = logging.getLogger(__name__)

PLATFORMS: list[Platform] = [Platform.LIGHT, Platform.NUMBER]

type Sp110eConfigEntry = ConfigEntry[Sp110eDevice]


async def async_setup_entry(hass: HomeAssistant, entry: Sp110eConfigEntry) -> bool:
    address: str = entry.data[CONF_ADDRESS]
    ble_device = bluetooth.async_ble_device_from_address(hass, address.upper(), connectable=True)
    if ble_device is None:
        raise ConfigEntryNotReady(
            f"SP110E {address} not found: is it powered and in range of a Bluetooth adapter or proxy?"
        )

    device = Sp110eDevice(ble_device, entry.data.get(CONF_NAME) or entry.title)

    @callback
    def _seen(service_info: bluetooth.BluetoothServiceInfoBleak, _change: bluetooth.BluetoothChange) -> None:
        # Anúncio recebido: atualiza o BLEDevice (pode ter mudado de adaptador/proxy)
        device.set_ble_device(service_info.device)

    entry.async_on_unload(
        bluetooth.async_register_callback(
            hass,
            _seen,
            bluetooth.BluetoothCallbackMatcher(address=address.upper(), connectable=True),
            bluetooth.BluetoothScanningMode.PASSIVE,
        )
    )

    # Primeira leitura do estado; se a ligação falhar, o HA volta a tentar mais tarde
    try:
        await device.update()
    except Exception as err:  # noqa: BLE001
        await device.disconnect()
        raise ConfigEntryNotReady(f"Could not connect to {address}: {err}") from err
    if device.status is None:
        _LOGGER.warning("%s: ligado, mas o controlador não enviou o estado; a usar valores por omissão", device.name)
        device.ensure_status()

    async def _refresh(_now: object) -> None:
        try:
            await device.update()
        except Exception as err:  # noqa: BLE001
            _LOGGER.debug("%s: leitura periódica falhou: %s", device.name, err)

    entry.async_on_unload(
        async_track_time_interval(hass, _refresh, timedelta(seconds=REFRESH_INTERVAL_SECONDS))
    )

    entry.runtime_data = device
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def async_unload_entry(hass: HomeAssistant, entry: Sp110eConfigEntry) -> bool:
    unloaded = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    if unloaded:
        await entry.runtime_data.disconnect()
    return unloaded
