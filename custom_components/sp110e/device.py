"""Ligação ao controlador SP110E pelo Bluetooth do Home Assistant.

Liga quando é preciso (comando ou leitura de estado), mantém a ligação enquanto
houver atividade e desliga-se ao fim de IDLE_DISCONNECT_SECONDS, para o
controlador (que só aceita uma ligação) continuar disponível para o telemóvel.
Funciona com o adaptador local e com proxies Bluetooth do ESPHome.
"""

from __future__ import annotations

import asyncio
import dataclasses
from collections.abc import Callable
import logging

from bleak.backends.device import BLEDevice
from bleak.exc import BleakError
from bleak_retry_connector import (
    BLEAK_RETRY_EXCEPTIONS,
    BleakClientWithServiceCache,
    establish_connection,
)

from . import protocol
from .const import IDLE_DISCONNECT_SECONDS

_LOGGER = logging.getLogger(__name__)

# Comandos seguidos sem esperar resposta podem perder-se; pequena pausa entre eles.
WRITE_GAP = 0.03
STATUS_TIMEOUT = 5.0


class Sp110eDevice:
    """Estado e comandos de um controlador."""

    def __init__(self, ble_device: BLEDevice, name: str) -> None:
        self._ble_device = ble_device
        self.name = name
        self._client: BleakClientWithServiceCache | None = None
        self._lock = asyncio.Lock()
        self._idle_handle: asyncio.TimerHandle | None = None
        self._status_event = asyncio.Event()
        self._listeners: list[Callable[[], None]] = []
        self.status: protocol.Status | None = None
        self.available = False

    # ── Estado ────────────────────────────────────────────────────────────

    @property
    def address(self) -> str:
        return self._ble_device.address

    def set_ble_device(self, ble_device: BLEDevice) -> None:
        """Novo BLEDevice (outro adaptador/proxy pode estar mais perto)."""
        self._ble_device = ble_device

    def add_listener(self, cb: Callable[[], None]) -> Callable[[], None]:
        self._listeners.append(cb)

        def remove() -> None:
            if cb in self._listeners:
                self._listeners.remove(cb)

        return remove

    def _notify_listeners(self) -> None:
        for cb in list(self._listeners):
            try:
                cb()
            except Exception:  # noqa: BLE001
                _LOGGER.exception("%s: listener", self.name)

    def _on_notify(self, _sender: object, data: bytearray) -> None:
        status = protocol.parse_status(bytes(data))
        if status is None:
            _LOGGER.debug("%s: notificação ignorada: %s", self.name, data.hex())
            return
        self.status = status
        self.available = True
        self._status_event.set()
        self._notify_listeners()

    def _update_local(self, **changes: object) -> None:
        """Atualização otimista (o controlador não confirma comandos)."""
        if self.status is None:
            return
        self.status = dataclasses.replace(self.status, **changes)  # type: ignore[arg-type]
        self._notify_listeners()

    # ── Ligação ───────────────────────────────────────────────────────────

    def _on_disconnected(self, _client: object) -> None:
        _LOGGER.debug("%s: desligado", self.name)
        self._client = None

    async def _ensure_connected(self) -> BleakClientWithServiceCache:
        if self._client is not None and self._client.is_connected:
            return self._client
        _LOGGER.debug("%s: a ligar a %s", self.name, self._ble_device.address)
        client = await establish_connection(
            BleakClientWithServiceCache,
            self._ble_device,
            self.name,
            disconnected_callback=self._on_disconnected,
            ble_device_callback=lambda: self._ble_device,
        )
        try:
            await client.start_notify(protocol.CHAR_UUID, self._on_notify)
        except BleakError:
            # Cache de serviços desatualizada: limpa e tenta outra vez
            await client.clear_cache()
            await client.disconnect()
            raise
        self._client = client
        # Handshake logo após ligar (alguns firmwares cortam a ligação sem isto)
        await self._write(client, protocol.check_device())
        return client

    async def _write(self, client: BleakClientWithServiceCache, data: bytes) -> None:
        await client.write_gatt_char(protocol.CHAR_UUID, data, response=True)
        await asyncio.sleep(WRITE_GAP)

    def _schedule_idle_disconnect(self) -> None:
        if self._idle_handle is not None:
            self._idle_handle.cancel()
        loop = asyncio.get_running_loop()
        self._idle_handle = loop.call_later(
            IDLE_DISCONNECT_SECONDS, lambda: asyncio.ensure_future(self.disconnect())
        )

    async def disconnect(self) -> None:
        if self._idle_handle is not None:
            self._idle_handle.cancel()
            self._idle_handle = None
        async with self._lock:
            client, self._client = self._client, None
            if client is not None and client.is_connected:
                try:
                    await client.disconnect()
                except BleakError:
                    pass

    async def _send(self, *commands: bytes) -> None:
        """Envia comandos (liga se preciso). Tenta uma segunda vez se a ligação caiu."""
        async with self._lock:
            for attempt in (1, 2):
                try:
                    client = await self._ensure_connected()
                    for cmd in commands:
                        await self._write(client, cmd)
                    self.available = True
                    break
                except BLEAK_RETRY_EXCEPTIONS as err:
                    self._client = None
                    if attempt == 2:
                        self.available = False
                        self._notify_listeners()
                        raise
                    _LOGGER.debug("%s: %s — nova tentativa", self.name, err)
        self._schedule_idle_disconnect()

    # ── Operações ─────────────────────────────────────────────────────────

    async def update(self) -> protocol.Status | None:
        """Lê o estado do controlador."""
        self._status_event.clear()
        await self._send(protocol.get_info())
        try:
            await asyncio.wait_for(self._status_event.wait(), STATUS_TIMEOUT)
        except TimeoutError:
            _LOGGER.debug("%s: sem resposta ao pedido de estado", self.name)
        return self.status

    async def turn_on(
        self,
        rgb: tuple[int, int, int] | None = None,
        brightness: int | None = None,
        mode: int | None = None,
    ) -> None:
        cmds = [protocol.power(True)]
        changes: dict[str, object] = {"is_on": True}
        if brightness is not None:
            cmds.append(protocol.brightness(brightness))
            changes["brightness"] = brightness
        if rgb is not None:
            cmds.append(protocol.color(*rgb))
            changes["rgb"] = tuple(rgb)
            changes["mode"] = protocol.MODE_STATIC
        elif mode is not None:
            if mode == protocol.MODE_STATIC:
                cur = self.status.rgb if self.status else (255, 255, 255)
                cmds.append(protocol.color(*cur))
            else:
                cmds.append(protocol.mode(mode))
            changes["mode"] = mode
        await self._send(*cmds)
        self._update_local(**changes)

    async def turn_off(self) -> None:
        await self._send(protocol.power(False))
        self._update_local(is_on=False)

    async def set_speed(self, value: int) -> None:
        await self._send(protocol.speed(value))
        self._update_local(speed=value)

    async def set_white(self, value: int) -> None:
        await self._send(protocol.white(value))
        self._update_local(white=value)
