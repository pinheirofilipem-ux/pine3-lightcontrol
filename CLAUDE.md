# Pine3 Light Control — instruções para o Claude Code

App de tray para Windows (WPF, .NET 10) que controla entidades do Home Assistant e as
combina em cenas. Irmã do pine3-monitor (mesmo estilo: tema Fluent, cartões, tray,
Inno Setup, CI em Windows). Os controladores Bluetooth SP110E (app LED Hue) entram pelo
HA, com a integração em `custom_components/sp110e`.

## Regras
- Responder em PT-PT. Comentários do código em PT; texto da UI em inglês (como o pine3-monitor).
- `Pine3LightControl.Core` (net10.0) **não** pode usar APIs do Windows: é o que os testes cobrem.
- Hardware real (gráfica/RAM) só se testa no PC do utilizador: nunca alargar endereços SMBus,
  PIDs aceites ou escritas persistentes sem confirmação dele.
- OpenRGB: `Core/OpenRgb` fala o protocolo SDK v1 (TCP 6742; formato confirmado no código do
  OpenRGB release_1.1). Dispositivos com o mesmo nome formam uma luz (`OpenRgbLight`); brilho =
  cor escalada (modo Direct). Placa de referência 6950 XT = Cooler Master `2516:014D`, RAM
  Vengeance RGB Pro DDR4 por SMBus (precisa do OpenRGB como administrador + PawnIO).
- RGB do PC sem software extra: `Core/PcRgb` (protocolos, testados com HID/SMBus falsos) e
  `src/Pine3LightControl/PcRgb` (HidSharp, PawnIO). Gráfica: Cooler Master CMR6000 (2516:014D,
  interface 1, relatórios de 65 bytes). Memória: Corsair DRAM 0x58–0x5F, só PIDs Vengeance RGB
  Pro DDR4 com CRC certo, modo direto (volátil); firmware < 4 grava efeito estático (debounce).
  Mutex global `Global\Access_SMBUS.HTP.Method` partilhado com o Pine3 Monitor/LHM.
  Rato: Logitech HID++ 2.0 pelo recetor 046D:C539 (coleção de 20 bytes), feature 0x8070
  (SET_EFFECT 0x30 sem persistência) ou 0x8071 (o G903 HERO: CONTROL 0x50 01 03 05 e SET 0x10
  com data[12]=1, como o OpenRGB; o CONTROL repete-se após 20 s parado ou falha, porque o rato
  adormece e o firmware retoma o RGB). Teclado: Ducky OK-M 75 (28E9:31C6) por VIA 12 (usage page
  0xFF60, relatórios de 64 bytes + report ID; só com fio: o recetor 2.4 GHz 056D:C077 não passa o VIA), canal 3 = teclas, 2 = lateral; nunca envia 0x09 (gravar na EEPROM).
- A app corre como administrador (requireAdministrator) e arranca por tarefa agendada; o
  instalador é para todo o sistema. PawnIOLib.dll + SmbusI801.bin vêm de `tools/get-pawnio.ps1`.
- Cenas: `Core/Scenes` (`SceneEngine.Capture/Apply`), guardadas em `AppConfig.Scenes`. O rato e o
  teclado (`LightHub.IsPeripheral`) não entram nas cenas nem no "All on/off": configuram-se na
  página PC RGB. O último estado de cada luz do PC fica em `PcRgb.LastState` e é reposto quando
  a luz é detetada; o teclado é detetado sozinho quando se liga o cabo (`DeviceList.Changed`).
- Gravação nos dispositivos: só a pedido (botão + confirmação). Memórias: efeito estático
  (config. de efeito + cores, `SaveToDeviceAsync`). Gráfica (CMR6000): o OpenRGB não tem
  comando de gravação; não inventar um.
- Toda a luz implementa `ILight` (via `LightBase`); os comandos passam por uma `SendQueue`
  (o último valor de cada tipo ganha) para os sliders não inundarem o HA.
- Novos comandos/estado: acrescentar testes em `tests/` e correr `dotnet test`.
- Versão: subir em `src/Pine3LightControl/Pine3LightControl.csproj` e `installer.iss` (o CI confere).
- Integração do Home Assistant em `custom_components/sp110e` (Python, HA ≥ 2025.2):
  protocolo em `protocol.py` (sem dependências), ligação em `device.py` (bleak-retry-connector,
  liga a pedido e desliga ao fim de 60 s). Versão no `manifest.json`. Testes em `tests/ha`.
  Sem energia (PC desligado) fica indisponível (`async_track_unavailable`) e volta a ligar
  sozinho ao primeiro anúncio (`Sp110eDevice.reconnect`, no máximo a cada 15 s); a entrada
  carrega mesmo com o controlador desligado (não usa `ConfigEntryNotReady`).
- O Bluetooth direto foi retirado da app (0.2.0): neste PC o Windows não lê os serviços GATT do
  SP110E (dois adaptadores, app e Chrome); o HA (Linux) e o iPhone leem `FFE0/FFE1` sem problemas.

## Comandos
```bash
dotnet test tests/Pine3LightControl.Tests        # corre em Linux
dotnet build src/Pine3LightControl                # compila o WPF em Linux (EnableWindowsTargeting)
build.bat                                         # Windows: testes + publish + instalador
python -m unittest discover -s tests/ha           # integração HA (os testes de device.py precisam de bleak)
```
