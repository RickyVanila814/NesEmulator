using NesEmu.Core;

namespace NesEmu.Tests;

public class ApuTests
{
    private static Apu Make() => new(_ => 0);

    [Fact]
    public void Length_counter_shows_in_status_and_is_cleared_by_disabling_the_channel()
    {
        var apu = Make();
        apu.Write(0x4015, 0x01);             // liga o pulso 1
        apu.Write(0x4003, 0x08);             // carrega a duração
        Assert.Equal(0x01, apu.ReadStatus() & 0x01);

        apu.Write(0x4015, 0x00);             // desligar zera a duração
        Assert.Equal(0x00, apu.ReadStatus() & 0x01);
    }

    [Fact]
    public void Length_counter_is_not_loaded_while_the_channel_is_disabled()
    {
        var apu = Make();
        apu.Write(0x4003, 0x08);
        Assert.Equal(0x00, apu.ReadStatus() & 0x01);
    }

    [Fact]
    public void Generates_samples_at_the_configured_rate()
    {
        var apu = Make();
        for (int i = 0; i < 40586; i++) apu.Clock();   // ~1000 amostras a 44100 Hz
        var buffer = new short[4096];
        int n = apu.ReadSamples(buffer);
        Assert.InRange(n, 999, 1001);
    }

    [Fact]
    public void Silent_apu_produces_silence()
    {
        var apu = Make();
        for (int i = 0; i < 40586; i++) apu.Clock();
        var buffer = new short[4096];
        int n = apu.ReadSamples(buffer);
        Assert.All(buffer.Take(n), s => Assert.Equal(0, s));
    }

    [Fact]
    public void Pulse_channel_produces_audible_samples()
    {
        var apu = Make();
        apu.Write(0x4015, 0x01);
        apu.Write(0x4000, 0xBF);             // duty 50%, volume constante 15, duração travada
        apu.Write(0x4002, 0x64);             // período do timer = 100
        apu.Write(0x4003, 0x08);

        for (int i = 0; i < 40586; i++) apu.Clock();
        var buffer = new short[4096];
        int n = apu.ReadSamples(buffer);

        Assert.True(n > 900);
        Assert.Contains(buffer.Take(n), s => Math.Abs(s) > 500);
    }

    [Fact]
    public void Reading_status_clears_the_frame_irq()
    {
        var apu = Make();
        for (int i = 0; i < 29829; i++) apu.Clock();   // um frame completo de 4 passos
        Assert.True(apu.IrqActive);
        apu.ReadStatus();
        Assert.False(apu.IrqActive);
    }
}
