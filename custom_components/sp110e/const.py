"""Constantes da integração SP110E."""

DOMAIN = "sp110e"

# Nomes que estes controladores anunciam por Bluetooth (app "LED Hue").
DEVICE_NAMES = ("SP110E", "SP105E")

# Serviço que o controlador anuncia (o GATT por dentro é FFE0/FFE1). Com scan
# passivo o HA não recebe o nome (vem na resposta ao scan), por isso também
# se reconhece por este UUID.
ADVERTISED_SERVICE_UUID = "0000ffb0-0000-1000-8000-00805f9b34fb"
GATT_SERVICE_UUID = "0000ffe0-0000-1000-8000-00805f9b34fb"
DEFAULT_NAME = "SP110E"

# Desliga-se ao fim deste tempo sem comandos: o controlador só aceita uma
# ligação de cada vez e assim a app LED Hue do telemóvel continua a funcionar.
IDLE_DISCONNECT_SECONDS = 60

# De quanto em quanto tempo se lê o estado (liga, lê, volta a desligar).
REFRESH_INTERVAL_SECONDS = 600
