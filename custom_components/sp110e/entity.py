"""Base das entidades: device info comum e atualização por notificação."""

from __future__ import annotations

from homeassistant.helpers.device_registry import CONNECTION_BLUETOOTH, DeviceInfo
from homeassistant.helpers.entity import Entity

from . import protocol
from .const import DOMAIN
from .device import Sp110eDevice


class Sp110eEntity(Entity):
    _attr_has_entity_name = True
    _attr_should_poll = False

    def __init__(self, device: Sp110eDevice, title: str) -> None:
        self._device = device
        status = device.status
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, device.address)},
            connections={(CONNECTION_BLUETOOTH, device.address)},
            name=title,
            manufacturer="Sperll",
            model="SP110E",
            hw_version=(
                f"{protocol.IC_MODELS[status.ic_model] if status.ic_model < len(protocol.IC_MODELS) else status.ic_model}, "
                f"{protocol.RGB_ORDERS[status.rgb_order] if status.rgb_order < len(protocol.RGB_ORDERS) else status.rgb_order}, "
                f"{status.pixel_count} LEDs"
                if status
                else None
            ),
        )

    @property
    def available(self) -> bool:
        return self._device.available and self._device.status is not None

    async def async_added_to_hass(self) -> None:
        self.async_on_remove(self._device.add_listener(self.async_write_ha_state))
