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

    async def start_notify(self, _uuid, cb):
        self.notify = cb

    async def write_gatt_char(self, _uuid, data, response=True):
        if self.fail_next_write:
            self.fail_next_write = False
            self.is_connected = False
            raise bleak.exc.BleakError("disconnected")
        self.writes.append(bytes(data))
        if data[3] in (0x10, 0xD5):
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

        async def fake_establish(_cls, _dev, _name, **_kw):
            c = FakeClient()
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
            self.assertEqual(w[0][3], 0xD5)                       # handshake
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


if __name__ == "__main__":
    unittest.main()
