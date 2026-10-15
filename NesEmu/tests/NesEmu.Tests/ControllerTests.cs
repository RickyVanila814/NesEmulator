using NesEmu.Core;

namespace NesEmu.Tests;

public class ControllerTests
{
    private static Bus MakeBus()
    {
        var data = new byte[16 + 0x4000 + 0x2000];
        data[0] = (byte)'N'; data[1] = (byte)'E'; data[2] = (byte)'S'; data[3] = 0x1A;
        data[4] = 1;   // 16 KB de PRG
        data[5] = 1;   // 8 KB de CHR
        return new Bus(Cartridge.FromBytes(data));
    }

    [Fact]
    public void Buttons_are_read_serially_after_strobe_and_then_return_one()
    {
        var bus = MakeBus();
        bus.Controller1 = NesButton.A | NesButton.Start;

        bus.Write(0x4016, 1);
        bus.Write(0x4016, 0);

        // ordem: A, B, Select, Start, Cima, Baixo, Esquerda, Direita
        int[] expected = { 1, 0, 0, 1, 0, 0, 0, 0 };
        foreach (int bit in expected)
            Assert.Equal(bit, bus.Read(0x4016) & 1);

        Assert.Equal(1, bus.Read(0x4016) & 1);   // 9ª leitura em diante
    }

    [Fact]
    public void Strobe_high_keeps_returning_button_a()
    {
        var bus = MakeBus();
        bus.Controller1 = NesButton.A;
        bus.Write(0x4016, 1);
        Assert.Equal(1, bus.Read(0x4016) & 1);
        Assert.Equal(1, bus.Read(0x4016) & 1);
    }

    [Fact]
    public void Second_controller_is_read_from_4017()
    {
        var bus = MakeBus();
        bus.Controller2 = NesButton.B;
        bus.Write(0x4016, 1);
        bus.Write(0x4016, 0);
        Assert.Equal(0, bus.Read(0x4017) & 1);   // A
        Assert.Equal(1, bus.Read(0x4017) & 1);   // B
    }
}
