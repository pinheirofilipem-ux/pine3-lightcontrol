"""Velocidade dos efeitos (e canal branco, só em fitas RGBW)."""

from __future__ import annotations

from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import Sp110eConfigEntry, protocol
from .entity import Sp110eEntity


async def async_setup_entry(
    hass: HomeAssistant, entry: Sp110eConfigEntry, async_add_entities: AddConfigEntryEntitiesCallback
) -> None:
    device = entry.runtime_data
    entities: list[NumberEntity] = [Sp110eSpeed(device, entry.title)]
    if device.status is not None and protocol.is_rgbw(device.status.ic_model):
        entities.append(Sp110eWhite(device, entry.title))
    async_add_entities(entities)


class Sp110eSpeed(Sp110eEntity, NumberEntity):
    _attr_translation_key = "speed"
    _attr_native_min_value = 0
    _attr_native_max_value = 255
    _attr_native_step = 1
    _attr_mode = NumberMode.SLIDER
    _attr_entity_category = EntityCategory.CONFIG

    def __init__(self, device, title: str) -> None:
        super().__init__(device, title)
        self._attr_unique_id = f"{device.address}_speed"

    @property
    def native_value(self) -> float | None:
        s = self._device.status
        return s.speed if s else None

    async def async_set_native_value(self, value: float) -> None:
        await self._device.set_speed(int(value))


class Sp110eWhite(Sp110eEntity, NumberEntity):
    _attr_translation_key = "white"
    _attr_native_min_value = 0
    _attr_native_max_value = 255
    _attr_native_step = 1
    _attr_mode = NumberMode.SLIDER

    def __init__(self, device, title: str) -> None:
        super().__init__(device, title)
        self._attr_unique_id = f"{device.address}_white"

    @property
    def native_value(self) -> float | None:
        s = self._device.status
        return s.white if s else None

    async def async_set_native_value(self, value: float) -> None:
        await self._device.set_white(int(value))
