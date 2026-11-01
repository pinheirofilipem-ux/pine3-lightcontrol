"""Constantes da integração SP110E."""

DOMAIN = "sp110e"

# Nomes que estes controladores anunciam por Bluetooth (app "LED Hue").
DEVICE_NAMES = ("SP110E", "SP105E")

# Desliga-se ao fim deste tempo sem comandos: o controlador só aceita uma
# ligação de cada vez e assim a app LED Hue do telemóvel continua a funcionar.
IDLE_DISCONNECT_SECONDS = 60

# De quanto em quanto tempo se lê o estado (liga, lê, volta a desligar).
REFRESH_INTERVAL_SECONDS = 600
