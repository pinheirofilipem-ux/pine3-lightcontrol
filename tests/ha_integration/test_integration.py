"""Fluxo de configuração, entidades e serviços com um SP110E falso."""

from unittest.mock import patch

from bleak.backends.device import BLEDevice
from bleak.backends.scanner import AdvertisementData
from homeassistant.components.bluetooth import BluetoothServiceInfoBleak
from homeassistant.config_entries import SOURCE_BLUETOOTH, SOURCE_USER
from homeassistant.const import CONF_ADDRESS, STATE_OFF, STATE_ON
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.sp110e.const import DOMAIN

ADDRESS = "65:56:04:0A:1E:B5"
STATUS = bytes.fromhex("01790dff030200c8ff000000")   # lido do controlador real

DEVICE = BLEDevice(ADDRESS, "SP110E", {})
INFO = BluetoothServiceInfoBleak(
    name="SP110E",
    address=ADDRESS,
    rssi=-60,
    manufacturer_data={22117: bytes.fromhex("040a1eb5")},
    service_data={},
    service_uuids=["0000ffb0-0000-1000-8000-00805f9b34fb"],
    source="local",
    device=DEVICE,
    advertisement=AdvertisementData(
        local_name="SP110E",
        manufacturer_data={22117: bytes.fromhex("040a1eb5")},
        service_data={},
        service_uuids=["0000ffb0-0000-1000-8000-00805f9b34fb"],
        tx_power=None,
        rssi=-60,
        platform_data=(),
    ),
    connectable=True,
    time=0,
    tx_power=None,
)


def _nameless() -> BluetoothServiceInfoBleak:
    adv = AdvertisementData(
        local_name=None,
        manufacturer_data={22117: bytes.fromhex("040a1eb5")},
        service_data={},
        service_uuids=["0000ffb0-0000-1000-8000-00805f9b34fb"],
        tx_power=None,
        rssi=-82,
        platform_data=(),
    )
    return BluetoothServiceInfoBleak(
        name=ADDRESS, address=ADDRESS, rssi=-82,
        manufacturer_data={22117: bytes.fromhex("040a1eb5")}, service_data={},
        service_uuids=["0000ffb0-0000-1000-8000-00805f9b34fb"], source="local",
        device=BLEDevice(ADDRESS, None, {}), advertisement=adv, connectable=True, time=0, tx_power=None,
    )


class FakeClient:
    instances: list["FakeClient"] = []

    def __init__(self):
        self.is_connected = True
        self.writes: list[bytes] = []
        self.notify = None
        FakeClient.instances.append(self)

    async def start_notify(self, _uuid, cb):
        self.notify = cb

    async def write_gatt_char(self, _uuid, data, response=True):
        self.writes.append(bytes(data))
        if data[3] in (0x10, 0xD5):
            self.notify(None, bytearray(STATUS))

    async def disconnect(self):
        self.is_connected = False

    async def clear_cache(self):
        pass


async def _fake_establish(*_args, **_kwargs):
    return FakeClient()


async def test_bluetooth_discovery_flow(hass: HomeAssistant) -> None:
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_BLUETOOTH}, data=INFO)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "bluetooth_confirm"
    with patch("custom_components.sp110e.async_setup_entry", return_value=True):
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == "SP110E 1E:B5"
    assert result["data"][CONF_ADDRESS] == ADDRESS

    # Segunda descoberta do mesmo controlador: já configurado
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_BLUETOOTH}, data=INFO)
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"


async def test_user_flow(hass: HomeAssistant) -> None:
    with patch("custom_components.sp110e.config_flow.async_discovered_service_info", return_value=[INFO]):
        result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_USER})
        assert result["type"] is FlowResultType.FORM
        assert result["step_id"] == "user"
        with patch("custom_components.sp110e.async_setup_entry", return_value=True):
            result = await hass.config_entries.flow.async_configure(result["flow_id"], {CONF_ADDRESS: ADDRESS})
    assert result["type"] is FlowResultType.CREATE_ENTRY


async def test_discovery_without_name(hass: HomeAssistant) -> None:
    """Scan passivo: sem nome, só o endereço e o serviço FFB0 (o que o HA do utilizador vê)."""
    nameless = _nameless()
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_BLUETOOTH}, data=nameless)
    assert result["type"] is FlowResultType.FORM
    with patch("custom_components.sp110e.async_setup_entry", return_value=True):
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["title"] == "SP110E 1E:B5"
    assert result["data"]["name"] == "SP110E"


async def test_manual_address(hass: HomeAssistant) -> None:
    with patch("custom_components.sp110e.config_flow.async_discovered_service_info", return_value=[]):
        result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_USER})
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "manual"

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {CONF_ADDRESS: "nope"})
    assert result["errors"] == {CONF_ADDRESS: "invalid_address"}

    with patch("custom_components.sp110e.config_flow.async_ble_device_from_address", return_value=None):
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {CONF_ADDRESS: ADDRESS})
    assert result["errors"] == {CONF_ADDRESS: "not_in_range"}

    with (
        patch("custom_components.sp110e.config_flow.async_ble_device_from_address", return_value=DEVICE),
        patch("custom_components.sp110e.async_setup_entry", return_value=True),
    ):
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {CONF_ADDRESS: "65-56-04-0a-1e-b5"})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["data"][CONF_ADDRESS] == ADDRESS


async def test_setup_entities_and_services(hass: HomeAssistant) -> None:
    FakeClient.instances.clear()
    entry = MockConfigEntry(domain=DOMAIN, unique_id=ADDRESS, title="SP110E 1E:B5",
                            data={CONF_ADDRESS: ADDRESS, "name": "SP110E"})
    entry.add_to_hass(hass)
    with (
        patch("custom_components.sp110e.bluetooth.async_ble_device_from_address", return_value=DEVICE),
        patch("custom_components.sp110e.device.establish_connection", _fake_establish),
        patch("custom_components.sp110e.device.WRITE_GAP", 0),
    ):
        assert await hass.config_entries.async_setup(entry.entry_id)
        await hass.async_block_till_done()

        state = hass.states.get("light.sp110e_1e_b5")
        assert state is not None, [s.entity_id for s in hass.states.async_all()]
        assert state.state == STATE_ON
        assert tuple(state.attributes["rgb_color"]) == (255, 0, 0)
        assert state.attributes["brightness"] == 255
        assert state.attributes["effect"] == "Static"
        assert len(state.attributes["effect_list"]) == 122

        speed = hass.states.get("number.sp110e_1e_b5_effect_speed")
        assert speed is not None and float(speed.state) == 13

        client = FakeClient.instances[-1]
        await hass.services.async_call("light", "turn_on",
                                       {"entity_id": "light.sp110e_1e_b5", "rgb_color": [0, 0, 255], "brightness": 128},
                                       blocking=True)
        assert client.writes[-3:] == [bytes.fromhex("000000aa"), bytes.fromhex("8000002a"), bytes.fromhex("0000ff1e")]
        state = hass.states.get("light.sp110e_1e_b5")
        assert tuple(state.attributes["rgb_color"]) == (0, 0, 255)

        await hass.services.async_call("light", "turn_on",
                                       {"entity_id": "light.sp110e_1e_b5", "effect": "Effect 7"}, blocking=True)
        assert client.writes[-1] == bytes.fromhex("0700002c")
        assert hass.states.get("light.sp110e_1e_b5").attributes["effect"] == "Effect 7"

        await hass.services.async_call("light", "turn_off", {"entity_id": "light.sp110e_1e_b5"}, blocking=True)
        assert client.writes[-1] == bytes.fromhex("000000ab")
        assert hass.states.get("light.sp110e_1e_b5").state == STATE_OFF

        await hass.services.async_call("number", "set_value",
                                       {"entity_id": "number.sp110e_1e_b5_effect_speed", "value": 200}, blocking=True)
        assert client.writes[-1] == bytes.fromhex("c8000003")

        assert await hass.config_entries.async_unload(entry.entry_id)
        await hass.async_block_till_done()
        assert not client.is_connected


async def test_manifest_matcher_catches_nameless_advert(hass: HomeAssistant) -> None:
    """Os matchers do manifest.json apanham o anúncio real (sem nome, serviço FFB0)."""
    from homeassistant.components.bluetooth.match import ble_device_matches
    from homeassistant.loader import async_get_bluetooth

    matchers = [m for m in await async_get_bluetooth(hass) if m["domain"] == DOMAIN]
    assert matchers, "custom integration matchers not loaded"
    assert any(ble_device_matches(m, _nameless()) for m in matchers)
    assert any(ble_device_matches(m, INFO) for m in matchers)
