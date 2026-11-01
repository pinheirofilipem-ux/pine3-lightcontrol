"""Testes da integração dentro de um Home Assistant de teste.

pip install pytest-homeassistant-custom-component bleak bleak-retry-connector habluetooth ...
pytest tests/ha_integration   (corre a partir da raiz do repositório)
"""

import sys
from pathlib import Path

import pytest

# custom_components/ na raiz do repositório
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))


@pytest.fixture(autouse=True)
def auto_enable_custom_integrations(enable_custom_integrations):
    yield


@pytest.fixture(autouse=True)
def auto_bluetooth(enable_bluetooth):
    yield
