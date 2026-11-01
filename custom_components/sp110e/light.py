"""Entidade de luz: ligar/desligar, brilho, cor RGB e os 120 efeitos."""

from __future__ import annotations

from typing import Any

from homeassistant.components.light import (
    ATTR_BRIGHTNESS,
    ATTR_EFFECT,
    ATTR_RGB_COLOR,
    ColorMode,
    LightEntity,
    LightEntityFeature,
)
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import Sp110eConfigEntry, protocol
from .entity import Sp110eEntity


async def async_setup_entry(
    hass: HomeAssistant, entry: Sp110eConfigEntry, async_add_entities: AddConfigEntryEntitiesCallback
) -> None:
    async_add_entities([Sp110eLight(entry.runtime_data, entry.title)])


class Sp110eLight(Sp110eEntity, LightEntity):
    _attr_name = None   # a luz usa o nome do dispositivo
    _attr_color_mode = ColorMode.RGB
    _attr_supported_color_modes = {ColorMode.RGB}
    _attr_supported_features = LightEntityFeature.EFFECT
    _attr_effect_list = protocol.EFFECTS

    def __init__(self, device, title: str) -> None:
        super().__init__(device, title)
        self._attr_unique_id = device.address

    @property
    def is_on(self) -> bool | None:
        s = self._device.status
        return s.is_on if s else None

    @property
    def brightness(self) -> int | None:
        s = self._device.status
        return max(1, s.brightness) if s else None

    @property
    def rgb_color(self) -> tuple[int, int, int] | None:
        s = self._device.status
        return s.rgb if s else None

    @property
    def effect(self) -> str | None:
        s = self._device.status
        return protocol.mode_to_effect(s.mode) if s else None

    async def async_turn_on(self, **kwargs: Any) -> None:
        mode = None
        if (effect := kwargs.get(ATTR_EFFECT)) is not None:
            mode = protocol.effect_to_mode(effect)
        await self._device.turn_on(
            rgb=kwargs.get(ATTR_RGB_COLOR),
            brightness=kwargs.get(ATTR_BRIGHTNESS),
            mode=mode,
        )

    async def async_turn_off(self, **kwargs: Any) -> None:
        await self._device.turn_off()
