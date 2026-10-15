using NesEmu.Core;
using NesEmu.Core.Mappers;

namespace NesEmu.Tests;

public class Mapper1Tests
{
    /// <summary>ROM iNES sintética: mapper 1, 8 bancos de PRG (cada banco preenchido com o seu número), CHR-RAM.</summary>
    private static IMapper Make()
    {
        const int banks = 8;
        var data = new byte[16 + banks * 0x4000];
        data[0] = (byte)'N'; data[1] = (byte)'E'; data[2] = (byte)'S'; data[3] = 0x1A;
        data[4] = banks;
        data[5] = 0;          // sem CHR-ROM => CHR-RAM
        data[6] = 0x10;       // nibble baixo do mapper = 1
        for (int b = 0; b < banks; b++) Array.Fill(data, (byte)b, 16 + b * 0x4000, 0x4000);
        return MapperFactory.Create(Cartridge.FromBytes(data));
    }

    /// <summary>Escreve um valor de 5 bits no registrador serial, bit 0 primeiro.</summary>
    private static void WriteRegister(IMapper m, ushort address, int value)
    {
        for (int i = 0; i < 5; i++) m.CpuWrite(address, (byte)((value >> i) & 1));
    }

    [Fact]
    public void Power_on_fixes_the_last_bank_at_C000()
    {
        var m = Make();
        Assert.Equal(7, m.CpuRead(0xC000));
        Assert.Equal(0, m.CpuRead(0x8000));
    }

    [Fact]
    public void Prg_register_switches_the_bank_at_8000_in_mode_3()
    {
        var m = Make();
        WriteRegister(m, 0xE000, 2);
        Assert.Equal(2, m.CpuRead(0x8000));
        Assert.Equal(7, m.CpuRead(0xC000));
    }

    [Fact]
    public void Prg_mode_2_fixes_the_first_bank_at_8000()
    {
        var m = Make();
        WriteRegister(m, 0x8000, 0x0A);     // PRG modo 2, espelhamento vertical
        WriteRegister(m, 0xE000, 3);
        Assert.Equal(0, m.CpuRead(0x8000));
        Assert.Equal(3, m.CpuRead(0xC000));
    }

    [Fact]
    public void Control_register_selects_mirroring()
    {
        var m = Make();
        WriteRegister(m, 0x8000, 0x0E);     // modo PRG 3, espelhamento 2 = vertical
        Assert.Equal(Mirroring.Vertical, m.Mirroring);
        WriteRegister(m, 0x8000, 0x0F);     // espelhamento 3 = horizontal
        Assert.Equal(Mirroring.Horizontal, m.Mirroring);
    }

    [Fact]
    public void Write_with_bit_7_resets_the_serial_register()
    {
        var m = Make();
        m.CpuWrite(0xE000, 1);              // 1 bit já entrou...
        m.CpuWrite(0xE000, 0x80);           // ...e o reset descarta
        WriteRegister(m, 0xE000, 4);        // 5 escritas completas depois do reset
        Assert.Equal(4, m.CpuRead(0x8000));
    }
}
