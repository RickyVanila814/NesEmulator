using NesEmu.Core.Mappers;

namespace NesEmu.Core;

/// <summary>
/// Mapa de memória da CPU do NES:
///   $0000-$1FFF  RAM de 2 KB, espelhada 4x
///   $2000-$3FFF  registradores da PPU (8 bytes espelhados)
///   $4000-$4017  APU e I/O ($4014 = DMA de sprites, $4016/$4017 = controles)
///   $4020-$FFFF  cartucho (via mapper)
/// </summary>
public sealed class Bus : IBus
{
    private readonly byte[] _ram = new byte[0x800];
    private readonly IMapper _mapper;

    public Ppu Ppu { get; }
    public Apu Apu { get; }

    /// <summary>Ciclos de CPU "roubados" pelo DMA; a classe Nes os consome depois de cada instrução.</summary>
    public int StallCycles;

    /// <summary>Botões segurados agora em cada controle (quem tem o teclado/gamepad atualiza isto).</summary>
    public NesButton Controller1, Controller2;

    private byte _shift1, _shift2;   // registradores de deslocamento que o jogo lê bit a bit
    private bool _strobe;

    public Bus(Cartridge cartridge)
    {
        _mapper = MapperFactory.Create(cartridge);
        Ppu = new Ppu(_mapper);
        Apu = new Apu(Read);          // o DMC lê as amostras direto da memória da CPU
    }

    public byte Read(ushort address) => address switch
    {
        < 0x2000 => _ram[address & 0x07FF],
        < 0x4000 => Ppu.CpuRead(address & 7),
        < 0x4020 => ReadIo(address),
        _ => _mapper.CpuRead(address)
    };

    public void Write(ushort address, byte value)
    {
        if (address < 0x2000) _ram[address & 0x07FF] = value;
        else if (address < 0x4000) Ppu.CpuWrite(address & 7, value);
        else if (address < 0x4020) WriteIo(address, value);
        else _mapper.CpuWrite(address, value);
    }

    private byte ReadIo(ushort address) => address switch
    {
        0x4016 => ReadController(ref _shift1, Controller1),
        0x4017 => ReadController(ref _shift2, Controller2),
        0x4015 => Apu.ReadStatus(),
        _ => 0
    };

    /// <summary>Cada leitura devolve um botão: A, B, Select, Start, Cima, Baixo, Esquerda, Direita. Depois, 1.</summary>
    private byte ReadController(ref byte shift, NesButton live)
    {
        if (_strobe) return (byte)(0x40 | ((byte)live & 1));   // strobe ligado: sempre o botão A
        byte bit = (byte)(shift & 1);
        shift = (byte)((shift >> 1) | 0x80);                   // depois dos 8 botões o controle devolve 1
        return (byte)(0x40 | bit);
    }

    private void WriteIo(ushort address, byte value)
    {
        if (address == 0x4016)
        {
            // Bit 0 = strobe. Enquanto ligado e na hora de desligar, os botões atuais são "fotografados"
            bool strobe = (value & 1) != 0;
            if (strobe || _strobe)
            {
                _shift1 = (byte)Controller1;
                _shift2 = (byte)Controller2;
            }
            _strobe = strobe;
        }
        else if (address == 0x4014)
        {
            // DMA: copia 256 bytes da página $XX00 para a OAM; a CPU fica parada ~513 ciclos
            int page = value << 8;
            for (int i = 0; i < 256; i++) Ppu.WriteOamData(Read((ushort)(page + i)));
            StallCycles += 513;
        }
        else Apu.Write(address, value);   // $4000-$4013, $4015 e $4017 (frame counter)
    }
}
