namespace NesEmu.Core.Mappers;

/// <summary>
/// MMC1 (Mapper 1): usado por Mega Man 2, Zelda, Metroid... Todas as configurações entram por um
/// registrador serial: 5 escritas em $8000-$FFFF (1 bit cada, bit 0 primeiro). Na 5ª escrita o valor
/// vai para um de 4 registradores, escolhido pelos bits 13-14 do endereço dessa escrita:
///   $8000 controle | $A000 banco CHR 0 | $C000 banco CHR 1 | $E000 banco PRG
/// Escrever um valor com o bit 7 ligado reinicia o registrador serial.
/// </summary>
public sealed class Mapper1 : IMapper
{
    private readonly Cartridge _cart;

    private byte _shift = 0x10;   // o bit 4 ligado marca "vazio"; quando chega ao bit 0, são 5 bits lidos
    private byte _control = 0x0C; // ao ligar: PRG no modo 3 (último banco fixo em $C000)
    private byte _chr0, _chr1, _prg;

    public Mapper1(Cartridge cart) => _cart = cart;

    // Controle: bits 0-1 espelhamento, bits 2-3 modo PRG, bit 4 modo CHR
    public Mirroring Mirroring => (_control & 3) switch
    {
        0 => Mirroring.SingleScreenA,
        1 => Mirroring.SingleScreenB,
        2 => Mirroring.Vertical,
        _ => Mirroring.Horizontal
    };

    public byte CpuRead(ushort address)
    {
        if (address >= 0x8000) return _cart.PrgRom[PrgIndex(address)];
        if (address >= 0x6000) return _cart.PrgRam[address - 0x6000];
        return 0;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x8000)
        {
            _cart.PrgRam[address - 0x6000] = value;
            return;
        }
        if (address < 0x8000) return;

        if ((value & 0x80) != 0)          // reset do registrador serial
        {
            _shift = 0x10;
            _control |= 0x0C;
            return;
        }

        bool complete = (_shift & 1) != 0;                       // esta é a 5ª escrita?
        _shift = (byte)((_shift >> 1) | ((value & 1) << 4));
        if (!complete) return;

        switch ((address >> 13) & 3)
        {
            case 0: _control = _shift; break;
            case 1: _chr0 = _shift; break;
            case 2: _chr1 = _shift; break;
            case 3: _prg = _shift; break;
        }
        _shift = 0x10;
    }

    public byte PpuRead(ushort address) => address < 0x2000 ? _cart.ChrMem[ChrIndex(address)] : (byte)0;

    public void PpuWrite(ushort address, byte value)
    {
        if (address < 0x2000 && _cart.ChrIsRam) _cart.ChrMem[ChrIndex(address)] = value;
    }

    /// <summary>Modo PRG: 0/1 = 32 KB trocados juntos; 2 = 1º banco fixo em $8000; 3 = último banco fixo em $C000.</summary>
    private int PrgIndex(ushort address)
    {
        int length = _cart.PrgRom.Length;
        int mode = (_control >> 2) & 3;

        if (mode <= 1)
            return ((_prg & 0x0E) * 0x4000 + (address & 0x7FFF)) % length;

        int bank;
        if (mode == 2) bank = address < 0xC000 ? 0 : (_prg & 0x0F);
        else bank = address < 0xC000 ? (_prg & 0x0F) : (length / 0x4000 - 1);

        return (bank * 0x4000 + (address & 0x3FFF)) % length;
    }

    /// <summary>Modo CHR: bit 4 do controle desligado = 8 KB juntos; ligado = dois bancos de 4 KB.</summary>
    private int ChrIndex(ushort address)
    {
        int length = _cart.ChrMem.Length;
        if ((_control & 0x10) == 0)
            return ((_chr0 & 0x1E) * 0x1000 + address) % length;

        int bank = address < 0x1000 ? _chr0 : _chr1;
        return (bank * 0x1000 + (address & 0x0FFF)) % length;
    }
}
