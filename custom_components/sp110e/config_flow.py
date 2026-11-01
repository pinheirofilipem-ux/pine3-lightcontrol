"""Configuração: descoberta automática por Bluetooth ou escolha manual."""

from __future__ import annotations

import re
from typing import Any

import voluptuous as vol

from homeassistant.components.bluetooth import (
    BluetoothServiceInfoBleak,
    async_ble_device_from_address,
    async_discovered_service_info,
)
from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.const import CONF_ADDRESS, CONF_NAME

from .const import ADVERTISED_SERVICE_UUID, DEFAULT_NAME, DEVICE_NAMES, DOMAIN, GATT_SERVICE_UUID

_MAC = re.compile(r"^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}$")


def _is_sp110e(info: BluetoothServiceInfoBleak) -> bool:
    uuids = {u.lower() for u in info.service_uuids}
    return (
        (info.name or "").upper().startswith(DEVICE_NAMES)
        or ADVERTISED_SERVICE_UUID in uuids
        or GATT_SERVICE_UUID in uuids
    )


def _name(info: BluetoothServiceInfoBleak) -> str:
    """Nome anunciado; sem resposta ao scan o HA só conhece o endereço."""
    name = (info.name or "").strip()
    if not name or _MAC.match(name) or name.replace("-", ":").upper() == info.address.upper():
        return DEFAULT_NAME
    return name


def _title(info: BluetoothServiceInfoBleak) -> str:
    return f"{_name(info)} {info.address[-5:]}"


class Sp110eConfigFlow(ConfigFlow, domain=DOMAIN):
    """Fluxo de configuração."""

    VERSION = 1

    def __init__(self) -> None:
        self._discovery: BluetoothServiceInfoBleak | None = None
        self._found: dict[str, BluetoothServiceInfoBleak] = {}

    async def async_step_bluetooth(self, discovery_info: BluetoothServiceInfoBleak) -> ConfigFlowResult:
        """O HA encontrou um SP110E."""
        await self.async_set_unique_id(discovery_info.address.upper())
        self._abort_if_unique_id_configured()
        self._discovery = discovery_info
        self.context["title_placeholders"] = {"name": _title(discovery_info)}
        return await self.async_step_bluetooth_confirm()

    async def async_step_bluetooth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        assert self._discovery is not None
        if user_input is not None:
            return self._create(self._discovery)
        self._set_confirm_only()
        return self.async_show_form(
            step_id="bluetooth_confirm",
            description_placeholders={"name": _title(self._discovery)},
        )

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Adicionado à mão: lista os SP110E que o HA está a ver."""
        if user_input is not None:
            address = user_input[CONF_ADDRESS]
            await self.async_set_unique_id(address.upper(), raise_on_progress=False)
            self._abort_if_unique_id_configured()
            return self._create(self._found[address])

        current = self._async_current_ids(include_ignore=False)
        for info in async_discovered_service_info(self.hass, connectable=True):
            if _is_sp110e(info) and info.address.upper() not in current:
                self._found[info.address] = info
        if not self._found:
            return await self.async_step_manual()

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema(
                {vol.Required(CONF_ADDRESS): vol.In({a: f"{_title(i)} ({a})" for a, i in self._found.items()})}
            ),
        )

    async def async_step_manual(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Nada encontrado automaticamente: escrever o endereço (ex.: 65:56:04:0A:1E:B5)."""
        errors: dict[str, str] = {}
        if user_input is not None:
            address = user_input[CONF_ADDRESS].strip().upper().replace("-", ":")
            if not _MAC.match(address):
                errors[CONF_ADDRESS] = "invalid_address"
            elif async_ble_device_from_address(self.hass, address, connectable=True) is None:
                errors[CONF_ADDRESS] = "not_in_range"
            else:
                await self.async_set_unique_id(address, raise_on_progress=False)
                self._abort_if_unique_id_configured()
                return self.async_create_entry(
                    title=f"{DEFAULT_NAME} {address[-5:]}",
                    data={CONF_ADDRESS: address, CONF_NAME: DEFAULT_NAME},
                )
        return self.async_show_form(
            step_id="manual",
            data_schema=vol.Schema({vol.Required(CONF_ADDRESS): str}),
            errors=errors,
        )

    def _create(self, info: BluetoothServiceInfoBleak) -> ConfigFlowResult:
        return self.async_create_entry(
            title=_title(info),
            data={CONF_ADDRESS: info.address.upper(), CONF_NAME: _name(info)},
        )
