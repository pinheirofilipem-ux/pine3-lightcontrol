"""Testes do protocolo SP110E da integração do Home Assistant (sem precisar do HA).

python -m unittest discover -s tests/ha
"""

import importlib.util
from pathlib import Path
import random
import sys
import unittest

_path = Path(__file__).resolve().parents[2] / "custom_components" / "sp110e" / "protocol.py"
_spec = importlib.util.spec_from_file_location("sp110e_protocol", _path)
p = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = p   # o @dataclass precisa do módulo registado
_spec.loader.exec_module(p)


class ProtocolTests(unittest.TestCase):
    def test_commands(self):
        self.assertEqual(p.power(True), bytes.fromhex("000000aa"))
        self.assertEqual(p.power(False), bytes.fromhex("000000ab"))
        self.assertEqual(p.color(0x12, 0x34, 0x56), bytes.fromhex("1234561e"))
        self.assertEqual(p.brightness(300), bytes.fromhex("ff00002a"))
        self.assertEqual(p.brightness(-1), bytes.fromhex("0000002a"))
        self.assertEqual(p.speed(0xC6), bytes.fromhex("c6000003"))
        self.assertEqual(p.white(0xFF), bytes.fromhex("ff000069"))
        self.assertEqual(p.mode(0x3F), bytes.fromhex("3f00002c"))
        self.assertEqual(p.mode(p.MODE_AUTO), bytes.fromhex("00000006"))
        self.assertEqual(p.ic_model(3), bytes.fromhex("0300001c"))
        self.assertEqual(p.rgb_order(4), bytes.fromhex("0400003c"))
        self.assertEqual(p.pixel_count(362), bytes.fromhex("016a002d"))
        self.assertEqual(p.get_info(), bytes.fromhex("00000010"))
        self.assertEqual(p.check_device(random.Random(1))[3], 0xD5)
        with self.assertRaises(ValueError):
            p.mode(p.MODE_STATIC)
        with self.assertRaises(ValueError):
            p.pixel_count(0)

    def test_status_from_real_device(self):
        # Lido com o nRF Connect do SP110E do utilizador: 0179 0DFF 0302 00C8 FF00 0000
        s = p.parse_status(bytes.fromhex("01790dff030200c8ff000000"))
        self.assertTrue(s.is_on)
        self.assertEqual(s.mode, p.MODE_STATIC)
        self.assertEqual(s.speed, 13)
        self.assertEqual(s.brightness, 255)
        self.assertEqual(p.IC_MODELS[s.ic_model], "WS2811")
        self.assertEqual(p.RGB_ORDERS[s.rgb_order], "GRB")
        self.assertEqual(s.pixel_count, 200)
        self.assertEqual(s.rgb, (255, 0, 0))
        self.assertEqual(s.white, 0)
        # Resposta ao CHECK_DEVICE: 13 bytes, checksum à frente
        self.assertEqual(p.parse_status(bytes.fromhex("99") + bytes.fromhex("01790dff030200c8ff000000")), s)
        self.assertIsNone(p.parse_status(b"\x01\x02"))

    def test_effects(self):
        self.assertEqual(len(p.EFFECTS), 122)
        for m in range(0, p.MODE_STATIC + 1):
            self.assertEqual(p.effect_to_mode(p.mode_to_effect(m)), m)
        self.assertIsNone(p.effect_to_mode("Effect 999"))
        self.assertIsNone(p.effect_to_mode("nope"))


if __name__ == "__main__":
    unittest.main()
