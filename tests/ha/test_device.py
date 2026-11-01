"""Testes do Sp110eDevice com um cliente Bluetooth falso.

Precisam de bleak e bleak-retry-connector (instalados com o Home Assistant);
se não existirem, são saltados.
"""

import asyncio
import importlib
from pathlib import Path
import sys
import types
import unittest

try:
    import bleak  # noqa: F401
    import bleak_retry_connector  # noqa: F401
    HAVE_BLEAK = True
except ImportError:
    HAVE_BLEAK = False

STATUS = bytes.fromhex("01790dff030200c8ff000000")


def load_device_module():
    # Pacote "de teste" que aponta para a pasta da integração sem correr o __init__.py (que precisa do HA)
    pkg = types.ModuleType("sp110e_pkg")
    pkg.__path__ = [str(Path(__file__).resolve().parents[2] / "custom_components" / "sp110e")]
    sys.modules["sp110e_pkg"] = pkg
    return importlib.import_module("sp110e_pkg.device")


class FakeClient:
    def __init__(self):
        self.is_connected = True
        self.writes = []
        self.notify = None
        self.fail_next_write = False
        self.protocol_error_on = set()   # comandos a que o controlador responde "Unlikely Error"
        self.refuse_notify = False

    async def start_notify(self, _uuid, cb):
        if self.refuse_notify:
            raise bleak.exc.BleakError("GATT Protocol Error: Unlikely Error")
        self.notify = cb

    async def write_gatt_char(self, _uuid, data, response=True):
        if self.fail_next_write:
            self.fail_next_write = False
            self.is_connected = False
            raise bleak.exc.BleakError("disconnected")
        self.writes.append(bytes(data))
        if data[3] in self.protocol_error_on:
            # Ligação continua ativa, o controlador só responde com erro
            raise bleak.exc.BleakError((14, "GATT Protocol Error: Unlikely Error"))
        if data[3] in (0x10, 0xD5) and self.notify:
            self.notify(None, bytearray(STATUS))

    async def disconnect(self):
        self.is_connected = False

    async def clear_cache(self):
        pass


@unittest.skipUnless(HAVE_BLEAK, "bleak not installed")
class DeviceTests(unittest.TestCase):
    def setUp(self):
        self.mod = load_device_module()
        self.mod.WRITE_GAP = 0
        self.clients = []

        self.client_setup = lambda c: None

        async def fake_establish(_cls, _dev, _name, **_kw):
            c = FakeClient()
            self.client_setup(c)
            self.clients.append(c)
            return c

        self.mod.establish_connection = fake_establish
        ble = types.SimpleNamespace(address="65:56:04:0A:1E:B5", name="SP110E")
        self.dev = self.mod.Sp110eDevice(ble, "SP110E")

    def run_async(self, coro):
        return asyncio.run(coro)

    def test_update_turn_on_off(self):
        async def go():
            calls = []
            self.dev.add_listener(lambda: calls.append(1))
            s = await self.dev.update()
            self.assertTrue(s.is_on)
            self.assertEqual(s.pixel_count, 200)
            self.assertTrue(self.dev.available)

            await self.dev.turn_on(rgb=(0, 255, 0), brightness=128)
            w = self.clients[0].writes
            self.assertEqual(w[0], bytes.fromhex("01b7e3d5"))     # handshake fixo
            self.assertEqual(w[1], bytes.fromhex("00000010"))     # get info
            self.assertEqual(w[2:], [bytes.fromhex("000000aa"), bytes.fromhex("8000002a"), bytes.fromhex("00ff001e")])
            self.assertEqual(self.dev.status.rgb, (0, 255, 0))
            self.assertEqual(self.dev.status.brightness, 128)

            await self.dev.turn_on(mode=5)
            self.assertEqual(w[-1], bytes.fromhex("0500002c"))
            self.assertEqual(self.dev.status.mode, 5)

            await self.dev.turn_off()
            self.assertEqual(w[-1], bytes.fromhex("000000ab"))
            self.assertFalse(self.dev.status.is_on)
            self.assertGreater(len(calls), 3)
            self.assertEqual(len(self.clients), 1)                # reutiliza a ligação
            await self.dev.disconnect()
            self.assertFalse(self.clients[0].is_connected)
        self.run_async(go())

    def test_reconnects_after_drop(self):
        async def go():
            await self.dev.update()
            self.clients[0].fail_next_write = True
            await self.dev.set_speed(200)
            self.assertEqual(len(self.clients), 2)
            self.assertEqual(self.clients[1].writes[-1], bytes.fromhex("c8000003"))
            self.assertEqual(self.dev.status.speed, 200)
            await self.dev.disconnect()
        self.run_async(go())

    def test_protocol_errors_are_not_fatal(self):
        """O SP110E do utilizador respondeu "Unlikely Error" (0x0E) com a ligação ativa."""
        def setup(c):
            c.protocol_error_on = {0xD5, 0xAA}
        self.client_setup = setup

        async def go():
            s = await self.dev.update()
            self.assertIsNotNone(s)
            await self.dev.turn_on(rgb=(1, 2, 3))
            self.assertEqual(len(self.clients), 1)                # não voltou a ligar
            self.assertEqual(self.clients[0].writes[-1], bytes.fromhex("0102031e"))
            self.assertTrue(self.dev.available)
            await self.dev.disconnect()
        self.run_async(go())

    def test_without_notifications_uses_default_state(self):
        def setup(c):
            c.refuse_notify = True
        self.client_setup = setup
        self.mod.STATUS_TIMEOUT = 0.05

        async def go():
            self.assertIsNone(await self.dev.update())
            self.dev.ensure_status()
            self.assertFalse(self.dev.status.is_on)
            await self.dev.turn_on(brightness=10)
            self.assertTrue(self.dev.status.is_on)
            self.assertEqual(self.clients[0].writes[-1], bytes.fromhex("0a00002a"))
            await self.dev.disconnect()
        self.run_async(go())


if __name__ == "__main__":
    unittest.main()

    def test_marks_unavailable_and_reconnects_when_seen_again(self):
        """PC desligado: o controlador some; ao voltar a anunciar, liga e fica disponível."""
        self.mod.RECONNECT_MIN_INTERVAL = 0

        async def go():
            calls = []
            self.dev.add_listener(lambda: calls.append(self.dev.available))
            await self.dev.update()
            await self.dev.disconnect()
            self.assertTrue(self.dev.available)
            self.dev.mark_unavailable()
            self.assertFalse(self.dev.available)
            self.assertEqual(calls[-1], False)
            self.assertTrue(self.dev.can_reconnect)
            self.assertTrue(await self.dev.reconnect())
            self.assertTrue(self.dev.available)
            self.assertEqual(calls[-1], True)
            self.assertFalse(await self.dev.reconnect())          # já disponível: nada a fazer
            await self.dev.disconnect()
        self.run_async(go())

    def test_not_marked_unavailable_while_connected(self):
        async def go():
            await self.dev.update()                               # ligação ativa: não anuncia
            self.dev.mark_unavailable()
            self.assertTrue(self.dev.available)
            await self.dev.disconnect()
        self.run_async(go())

    def test_reconnect_is_rate_limited_and_survives_failures(self):
        self.mod.RECONNECT_MIN_INTERVAL = 60

        async def boom(*_a, **_k):
            raise bleak.exc.BleakError("not found")

        async def go():
            self.mod.establish_connection = boom
            dev = self.mod.Sp110eDevice(None, "SP110E", "65:56:04:0a:1e:b5")
            self.assertEqual(dev.address, "65:56:04:0A:1E:B5")
            self.assertFalse(await dev.reconnect())               # ainda não visto: falha sem rebentar
            self.assertFalse(dev.can_reconnect)                   # e espera antes de tentar outra vez
            self.assertFalse(dev.available)
        self.run_async(go())
