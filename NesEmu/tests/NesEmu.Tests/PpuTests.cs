using NesEmu.Core;
using NesEmu.Core.Mappers;

namespace NesEmu.Tests;

public class PpuTests
{
    private sealed class FakeMapper : IMapper
    {
        private readonly byte[] _chr = new byte[0x2000];
        public Mirroring Mirroring { get; set; } = Mirroring.Vertical;
        public byte CpuRead(ushort address) => 0;
        public void CpuWrite(ushort address, byte value) { }
        public byte PpuRead(ushort address) => _chr[address];
        public void PpuWrite(ushort address, byte value) => _chr[address] = value;
    }

    private static void SetAddress(Ppu ppu, int address)
    {
        ppu.CpuWrite(6, (byte)(address >> 8));
        ppu.CpuWrite(6, (byte)address);
    }

    private static void ClockDots(Ppu ppu, int dots)
    {
        for (int i = 0; i < dots; i++) ppu.Clock();
    }

    [Fact]
    public void Vblank_flag_is_set_at_scanline_241_and_cleared_by_reading_status()
    {
        var ppu = new Ppu(new FakeMapper());
        ClockDots(ppu, 241 * 341 + 2);               // processa (linha 241, ponto 1)

        Assert.NotEqual(0, ppu.CpuRead(2) & 0x80);   // vblank ligado
        Assert.Equal(0, ppu.CpuRead(2) & 0x80);      // a leitura anterior o limpou
    }

    [Fact]
    public void Nmi_is_requested_at_vblank_only_when_enabled()
    {
        var ppu = new Ppu(new FakeMapper());
        ClockDots(ppu, 241 * 341 + 2);
        Assert.False(ppu.NmiRequested);

        var ppu2 = new Ppu(new FakeMapper());
        ppu2.CpuWrite(0, 0x80);
        ClockDots(ppu2, 241 * 341 + 2);
        Assert.True(ppu2.NmiRequested);
    }

    [Fact]
    public void Ppudata_reads_are_delayed_by_one_access()
    {
        var ppu = new Ppu(new FakeMapper());
        SetAddress(ppu, 0x2000);
        ppu.CpuWrite(7, 0x42);

        SetAddress(ppu, 0x2000);
        Assert.Equal(0x00, ppu.CpuRead(7));          // devolve o buffer antigo
        Assert.Equal(0x42, ppu.CpuRead(7));          // agora devolve o valor carregado
    }

    [Fact]
    public void Palette_mirrors_3F10_to_3F00_and_reads_are_not_delayed()
    {
        var ppu = new Ppu(new FakeMapper());
        SetAddress(ppu, 0x3F10);
        ppu.CpuWrite(7, 0x21);

        SetAddress(ppu, 0x3F00);
        Assert.Equal(0x21, ppu.CpuRead(7));
    }

    [Fact]
    public void Vertical_mirroring_maps_2000_and_2800_to_the_same_byte()
    {
        var ppu = new Ppu(new FakeMapper { Mirroring = Mirroring.Vertical });
        SetAddress(ppu, 0x2000);
        ppu.CpuWrite(7, 0x11);

        SetAddress(ppu, 0x2800);
        ppu.CpuRead(7);                              // descarta o buffer antigo
        Assert.Equal(0x11, ppu.CpuRead(7));
    }

    [Fact]
    public void Horizontal_mirroring_maps_2000_and_2400_to_the_same_byte()
    {
        var ppu = new Ppu(new FakeMapper { Mirroring = Mirroring.Horizontal });
        SetAddress(ppu, 0x2000);
        ppu.CpuWrite(7, 0x22);

        SetAddress(ppu, 0x2400);
        ppu.CpuRead(7);
        Assert.Equal(0x22, ppu.CpuRead(7));
    }

    [Fact]
    public void Backdrop_color_fills_the_frame_when_rendering_is_off()
    {
        var ppu = new Ppu(new FakeMapper());
        SetAddress(ppu, 0x3F00);
        ppu.CpuWrite(7, 0x21);                       // cor $21 = azul claro
        ClockDots(ppu, 262 * 341);                   // um quadro inteiro

        Assert.True(ppu.FrameComplete);
        Assert.Equal(0xFF4C9AECu, ppu.Frame[0]);
        Assert.Equal(0xFF4C9AECu, ppu.Frame[Ppu.Width * Ppu.Height - 1]);
    }
}
