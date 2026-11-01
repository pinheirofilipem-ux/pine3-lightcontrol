# Pine3 Light Control — instruções para o Claude Code

App de tray para Windows (WPF, .NET 10) que controla luzes Bluetooth (controladores
SP110E/SP105E da app LED Hue) e entidades do Home Assistant. Irmã do pine3-monitor
(mesmo estilo: tema Fluent, cartões, tray, Inno Setup, CI em Windows).

## Regras
- Responder em PT-PT. Comentários do código em PT; texto da UI em inglês (como o pine3-monitor).
- `Pine3LightControl.Core` (net10.0) **não** pode usar APIs do Windows: é o que os testes cobrem.
  O transporte BLE (WinRT) fica na app, atrás de `IBleLink`.
- Toda a luz implementa `ILight` (via `LightBase`); os comandos passam por uma `SendQueue`
  (o último valor de cada tipo ganha) para os sliders não inundarem o BLE nem o HA.
- Novos comandos/estado: acrescentar testes em `tests/` e correr `dotnet test`.
- Versão: subir em `src/Pine3LightControl/Pine3LightControl.csproj` e `installer.iss` (o CI confere).
- Integração do Home Assistant em `custom_components/sp110e` (Python, HA ≥ 2025.2):
  protocolo em `protocol.py` (sem dependências), ligação em `device.py` (bleak-retry-connector,
  liga a pedido e desliga ao fim de 60 s). Versão no `manifest.json`. Testes em `tests/ha`.
- Nota: neste PC o Windows não consegue ler os serviços GATT do SP110E (ligação feita, descoberta
  sem resposta, com dois adaptadores e no Chrome); o iPhone (nRF Connect) lê `FFE0/FFE1` em 0,2 s.

## Comandos
```bash
dotnet test tests/Pine3LightControl.Tests        # corre em Linux
dotnet build src/Pine3LightControl                # compila o WPF em Linux (EnableWindowsTargeting)
build.bat                                         # Windows: testes + publish + instalador
python -m unittest discover -s tests/ha           # integração HA (os testes de device.py precisam de bleak)
```
