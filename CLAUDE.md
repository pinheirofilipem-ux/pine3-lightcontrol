# Pine3 Light Control — instruções para o Claude Code

App de tray para Windows (WPF, .NET 10) que controla entidades do Home Assistant e as
combina em cenas. Irmã do pine3-monitor (mesmo estilo: tema Fluent, cartões, tray,
Inno Setup, CI em Windows). Os controladores Bluetooth SP110E (app LED Hue) entram pelo
HA, com a integração em `custom_components/sp110e`.

## Regras
- Responder em PT-PT. Comentários do código em PT; texto da UI em inglês (como o pine3-monitor).
- `Pine3LightControl.Core` (net10.0) **não** pode usar APIs do Windows: é o que os testes cobrem.
- OpenRGB: `Core/OpenRgb` fala o protocolo SDK v1 (TCP 6742; formato confirmado no código do
  OpenRGB release_1.1). Dispositivos com o mesmo nome formam uma luz (`OpenRgbLight`); brilho =
  cor escalada (modo Direct). Placa de referência 6950 XT = Cooler Master `2516:014D`, RAM
  Vengeance RGB Pro DDR4 por SMBus (precisa do OpenRGB como administrador + PawnIO).
- Cenas: `Core/Scenes` (`SceneEngine.Capture/Apply`), guardadas em `AppConfig.Scenes`.
- Toda a luz implementa `ILight` (via `LightBase`); os comandos passam por uma `SendQueue`
  (o último valor de cada tipo ganha) para os sliders não inundarem o HA.
- Novos comandos/estado: acrescentar testes em `tests/` e correr `dotnet test`.
- Versão: subir em `src/Pine3LightControl/Pine3LightControl.csproj` e `installer.iss` (o CI confere).
- Integração do Home Assistant em `custom_components/sp110e` (Python, HA ≥ 2025.2):
  protocolo em `protocol.py` (sem dependências), ligação em `device.py` (bleak-retry-connector,
  liga a pedido e desliga ao fim de 60 s). Versão no `manifest.json`. Testes em `tests/ha`.
- O Bluetooth direto foi retirado da app (0.2.0): neste PC o Windows não lê os serviços GATT do
  SP110E (dois adaptadores, app e Chrome); o HA (Linux) e o iPhone leem `FFE0/FFE1` sem problemas.

## Comandos
```bash
dotnet test tests/Pine3LightControl.Tests        # corre em Linux
dotnet build src/Pine3LightControl                # compila o WPF em Linux (EnableWindowsTargeting)
build.bat                                         # Windows: testes + publish + instalador
python -m unittest discover -s tests/ha           # integração HA (os testes de device.py precisam de bleak)
```
