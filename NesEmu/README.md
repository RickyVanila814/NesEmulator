# NesEmu — emulador de NES em C# (projeto pedagógico)

## Como criar a solution e rodar

```bash
dotnet new sln -n NesEmu
dotnet sln add src/NesEmu.Core src/NesEmu.Cli src/NesEmu.Game tests/NesEmu.Tests
dotnet build
dotnet test                                   # testes unitários da CPU (+ nestest, se os arquivos existirem)
dotnet run --project src/NesEmu.Cli -- minha.nes 60 frame.bmp   # roda 60 quadros e salva o último em frame.bmp
```

Para jogar (janela em tempo real):

```bash
dotnet run --project src/NesEmu.Game -- "C:\caminho\Mega Man 2 (USA).nes"
```

Teclas: setas ou WASD = direcional, **X** = A, **Z** = B, **Enter** = Start, **Shift direito** ou **Backspace** = Select, **Esc** = sair.

Requer .NET 8 SDK ou superior.

## Estrutura

| Pasta / arquivo | O que faz |
|---|---|
| `Core/Cartridge.cs` | Carrega o formato iNES (cabeçalho, PRG, CHR, mapper, espelhamento) |
| `Core/Mappers/` | `IMapper` + `Mapper0` (NROM) + `Mapper1` (MMC1). UxROM/CNROM entram aqui |
| `Core/IBus.cs`, `Core/Bus.cs` | Mapa de memória da CPU (RAM espelhada, stubs de PPU/APU/IO, cartucho) |
| `Core/Ppu.cs` | PPU 2C02 ponto a ponto: registradores, scroll, fundo, sprites, sprite 0 hit, NMI |
| `Core/Apu.cs` | APU 2A03: 2 pulsos, triângulo, ruído, DMC, frame counter, mixer e saída PCM 44,1 kHz |
| `Core/Nes.cs` | Junta CPU + barramento + PPU + APU (3 pontos de PPU por ciclo de CPU) e roda quadros |
| `Core/Cpu6502.cs` | CPU 6502 com as 151 instruções oficiais e contagem de ciclos |
| `Game/` | Janela MonoGame (DesktopGL): roda 1 quadro por Update, lê teclado/gamepad, desenha ampliado |
| `Core/NesButton.cs` | Enum dos 8 botões do controle |
| `Cli/Program.cs` | Mostra info da ROM, roda N quadros e salva o último como BMP |
| `Tests/ApuTests.cs` | Duração dos canais, taxa de amostras, silêncio, onda do pulso e IRQ do frame |
| `Tests/ControllerTests.cs` | Leitura serial dos controles em `$4016`/`$4017` |
| `Tests/PpuTests.cs` | Testes de registradores, espelhamento, vblank/NMI e cor de fundo da PPU |
| `Tests/Cpu6502Tests.cs` | Testes pequenos com memória plana |
| `Tests/NestestTests.cs` | Compara a CPU com o log da `nestest.rom` (coloque os arquivos em `TestRoms/`) |

## Roteiro

1. [x] Carregador iNES
2. [x] CPU 6502 (oficiais) — validar com nestest
3. [x] Barramento de memória (com stubs)
4. [x] PPU (nametables, pattern tables, paleta, sprites, scroll) — ainda sem teste visual
5. [x] Controles (`$4016`) e janela (MonoGame)
6. [~] Mappers: MMC1 (1) feito; faltam UxROM (2) e CNROM (3)
7. [x] APU (som) — sem teste de ouvido ainda
