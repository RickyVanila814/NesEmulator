namespace NesEmu.Core.Mappers;

/// <summary>NROM: sem troca de bancos. 16 KB de PRG aparecem espelhados em $8000 e $C000.</summary>
public sealed class Mapper0 : IMapper
{
    private readonly Cartridge _cart;

    public Mapper0(Cartridge cart) => _cart = cart;

    public Mirroring Mirroring => _cart.Mirroring;

    public byte CpuRead(ushort address)
    {
        if (address >= 0x8000) return _cart.PrgRom[(address - 0x8000) % _cart.PrgRom.Length];
        if (address >= 0x6000) return _cart.PrgRam[address - 0x6000];
        return 0;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x8000) _cart.PrgRam[address - 0x6000] = value;
    }

    public byte PpuRead(ushort address) => address < 0x2000 ? _cart.ChrMem[address] : (byte)0;

    public void PpuWrite(ushort address, byte value)
    {
        if (address < 0x2000 && _cart.ChrIsRam) _cart.ChrMem[address] = value;
    }
}
