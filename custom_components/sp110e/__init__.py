"""Integração SP110E (controladores LED da app "LED Hue") por Bluetooth."""

from __future__ import annotations

from datetime import timedelta
import logging

from homeassistant.components import bluetooth
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_ADDRESS, CONF_NAME, Platform
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.event import async_track_time_interval

from .const import REFRESH_INTERVAL_SECONDS
from .device import Sp110eDevice

_LOGGER = logging.getLogger(__name__)

PLATFORMS: list[Platform] = [Platform.LIGHT, Platform.NUMBER]

type Sp110eConfigEntry = ConfigEntry[Sp110eDevice]


async def async_setup_entry(hass: HomeAssistant, entry: Sp110eConfigEntry) -> bool:
    address: str = entry.data[CONF_ADDRESS].upper()
    ble_device = bluetooth.async_ble_device_from_address(hass, address, connectable=True)
    device = Sp110eDevice(ble_device, entry.data.get(CONF_NAME) or entry.title, address)

    # A primeira ligação é feita em segundo plano: a configuração termina logo (a luz
    # aparece indisponível até ligar), para um controlador que não responde nunca
    # prender o arranque do Home Assistant.
    if ble_device is None:
        _LOGGER.info("%s: não está à vista (desligado?); a aguardar que volte a anunciar", device.name)
    device.ensure_status()
    device.available = False

    # O SP110E costuma ser alimentado pelo USB do PC: desliga com ele e volta a anunciar
    # quando o PC liga. Em vez de deixar a entrada falhar (e o HA espaçar as tentativas),
    # fica "indisponível" e volta a ligar logo que aparece um anúncio.
    @callback
    def _seen(service_info: bluetooth.BluetoothServiceInfoBleak, _change: bluetooth.BluetoothChange) -> None:
        # Anúncio recebido: atualiza o BLEDevice (pode ter mudado de adaptador/proxy)
        device.set_ble_device(service_info.device)
        if device.can_reconnect:
            entry.async_create_background_task(hass, device.reconnect(), f"sp110e reconnect {address}")

    @callback
    def _gone(_service_info: bluetooth.BluetoothServiceInfoBleak) -> None:
        device.mark_unavailable()

    entry.async_on_unload(
        bluetooth.async_register_callback(
            hass,
            _seen,
            bluetooth.BluetoothCallbackMatcher(address=address, connectable=True),
            bluetooth.BluetoothScanningMode.PASSIVE,
        )
    )
    entry.async_on_unload(bluetooth.async_track_unavailable(hass, _gone, address, connectable=True))

    async def _refresh(_now: object) -> None:
        if not device.available:
            await device.reconnect()
            return
        try:
            await device.update()
        except Exception as err:  # noqa: BLE001
            _LOGGER.debug("%s: leitura periódica falhou: %s", device.name, err)

    entry.async_on_unload(
        async_track_time_interval(hass, _refresh, timedelta(seconds=REFRESH_INTERVAL_SECONDS))
    )

    entry.runtime_data = device
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    if ble_device is not None:
        entry.async_create_background_task(hass, device.reconnect(), f"sp110e connect {address}")
    return True


async def async_unload_entry(hass: HomeAssistant, entry: Sp110eConfigEntry) -> bool:
    unloaded = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    if unloaded:
        await entry.runtime_data.disconnect()
    return unloaded
