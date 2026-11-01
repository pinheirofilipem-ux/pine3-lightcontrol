"""Protocolo BLE dos controladores Sperll SP110E / SP105E (app "LED Hue").

Serviço 0xFFE0, característica 0xFFE1 (Write + Notify). Cada comando tem
4 bytes: [d0, d1, d2, comando]. O estado chega como notificação de 12 bytes
(resposta a GET_INFO) ou 13 bytes (resposta a CHECK_DEVICE, com um checksum
à frente).

Sem dependências do Home Assistant, para se poder testar isoladamente.
"""

from __future__ import annotations

from dataclasses import dataclass
import random

SERVICE_UUID = "0000ffe0-0000-1000-8000-00805f9b34fb"
CHAR_UUID = "0000ffe1-0000-1000-8000-00805f9b34fb"

CMD_ON = 0xAA
CMD_OFF = 0xAB
CMD_COLOR = 0x1E
CMD_BRIGHTNESS = 0x2A
CMD_WHITE = 0x69
CMD_MODE = 0x2C
CMD_MODE_AUTO = 0x06
CMD_SPEED = 0x03
CMD_IC_MODEL = 0x1C
CMD_RGB_ORDER = 0x3C
CMD_PIXEL_COUNT = 0x2D
CMD_CHECK_DEVICE = 0xD5
CMD_GET_INFO = 0x10

MODE_AUTO = 0
MODE_STATIC = 121
MAX_PIXELS = 1024

IC_MODELS = (
    "SM16703", "TM1804", "UCS1903", "WS2811", "WS2801", "SK6812", "LPD6803", "LPD8806",
    "APA102", "APA105", "DMX512", "TM1914", "TM1913", "P9813", "INK1003", "P943S",
    "P9411", "P9413", "TX1812", "TX1813", "GS8206", "GS8208", "SK9822", "TM1814",
    "SK6812_RGBW", "P9414", "PG412",
)
RGB_ORDERS = ("RGB", "RBG", "GRB", "GBR", "BRG", "BGR")

# Efeitos como aparecem no Home Assistant: "Static", "Auto" e "Effect 1".."Effect 120".
EFFECT_STATIC = "Static"
EFFECT_AUTO = "Auto"
EFFECTS = [EFFECT_STATIC, EFFECT_AUTO] + [f"Effect {i}" for i in range(1, MODE_STATIC)]


def effect_to_mode(effect: str) -> int | None:
    """Nome do efeito -> número do modo (0 auto, 1..120, 121 cor fixa)."""
    if effect == EFFECT_STATIC:
        return MODE_STATIC
    if effect == EFFECT_AUTO:
        return MODE_AUTO
    if effect.startswith("Effect "):
        try:
            n = int(effect[7:])
        except ValueError:
            return None
        if 1 <= n < MODE_STATIC:
            return n
    return None


def mode_to_effect(mode: int) -> str | None:
    if mode == MODE_STATIC:
        return EFFECT_STATIC
    if mode == MODE_AUTO:
        return EFFECT_AUTO
    if 1 <= mode < MODE_STATIC:
        return f"Effect {mode}"
    return None


def is_rgbw(ic_model: int) -> bool:
    return 22 < ic_model < len(IC_MODELS)


def _b(v: int) -> int:
    return max(0, min(255, int(v)))


def _cmd(cmd: int, d0: int = 0, d1: int = 0, d2: int = 0) -> bytes:
    return bytes((d0, d1, d2, cmd))


def power(on: bool) -> bytes:
    return _cmd(CMD_ON if on else CMD_OFF)


def color(r: int, g: int, b: int) -> bytes:
    return _cmd(CMD_COLOR, _b(r), _b(g), _b(b))


def brightness(v: int) -> bytes:
    return _cmd(CMD_BRIGHTNESS, _b(v))


def white(v: int) -> bytes:
    return _cmd(CMD_WHITE, _b(v))


def speed(v: int) -> bytes:
    return _cmd(CMD_SPEED, _b(v))


def mode(m: int) -> bytes:
    if not 0 <= m < MODE_STATIC:
        raise ValueError(f"mode {m} (use color() for static)")
    return _cmd(CMD_MODE_AUTO) if m == MODE_AUTO else _cmd(CMD_MODE, m)


def ic_model(index: int) -> bytes:
    if not 0 <= index < len(IC_MODELS):
        raise ValueError(index)
    return _cmd(CMD_IC_MODEL, index)


def rgb_order(index: int) -> bytes:
    if not 0 <= index < len(RGB_ORDERS):
        raise ValueError(index)
    return _cmd(CMD_RGB_ORDER, index)


def pixel_count(n: int) -> bytes:
    if not 1 <= n <= MAX_PIXELS:
        raise ValueError(n)
    return _cmd(CMD_PIXEL_COUNT, n >> 8, n & 0xFF)


def get_info() -> bytes:
    return _cmd(CMD_GET_INFO)


def check_device(rng: random.Random | None = None) -> bytes:
    """Handshake: alguns firmwares cortam a ligação se não o receberem logo."""
    rng = rng or random
    return _cmd(CMD_CHECK_DEVICE, rng.randrange(256), rng.randrange(256), rng.randrange(256))


@dataclass(frozen=True)
class Status:
    is_on: bool
    mode: int
    speed: int
    brightness: int
    ic_model: int
    rgb_order: int
    pixel_count: int
    rgb: tuple[int, int, int]
    white: int


def parse_status(data: bytes | bytearray) -> Status | None:
    """Notificação de estado (12 bytes, ou 13 com checksum à frente)."""
    if len(data) == 13:
        data = data[1:]
    elif len(data) != 12:
        return None
    return Status(
        is_on=data[0] != 0,
        mode=data[1],
        speed=data[2],
        brightness=data[3],
        ic_model=data[4],
        rgb_order=data[5],
        pixel_count=(data[6] << 8) | data[7],
        rgb=(data[8], data[9], data[10]),
        white=data[11],
    )
