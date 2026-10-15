namespace NesEmu.Core;

/// <summary>Junta cartucho, CPU, barramento, PPU e APU, mantendo a proporção de 3 pontos de PPU por ciclo de CPU.</summary>
public sealed class Nes
{
    public Cartridge Cartridge { get; }
    public Bus Bus { get; }
    public Cpu6502 Cpu { get; }
    public Ppu Ppu => Bus.Ppu;
    public Apu Apu => Bus.Apu;

    public Nes(Cartridge cartridge)
    {
        Cartridge = cartridge;
        Bus = new Bus(cartridge);
        Cpu = new Cpu6502(Bus);
        Cpu.Reset();
    }

    /// <summary>Executa instruções até a PPU terminar um quadro (vídeo em Ppu.Frame, som em Apu.ReadSamples).</summary>
    public void RunFrame()
    {
        Ppu.FrameComplete = false;
        while (!Ppu.FrameComplete) StepInstruction();
    }

    /// <summary>Executa uma instrução da CPU e avança PPU e APU na mesma proporção. Devolve os ciclos de CPU gastos.</summary>
    public int StepInstruction()
    {
        int cycles = Cpu.Step();

        if (Bus.StallCycles > 0)
        {
            cycles += Bus.StallCycles;
            Cpu.Cycles += Bus.StallCycles;
            Bus.StallCycles = 0;
        }

        Advance(cycles);

        if (Apu.IrqActive && Cpu.Irq())
        {
            Advance(7);                       // o IRQ gasta 7 ciclos
            cycles += 7;
        }
        return cycles;
    }

    private void Advance(int cpuCycles)
    {
        for (int i = 0; i < cpuCycles; i++) Apu.Clock();

        int dots = cpuCycles * 3;
        while (dots-- > 0)
        {
            Ppu.Clock();
            if (Ppu.NmiRequested)
            {
                Ppu.NmiRequested = false;
                Cpu.Nmi();
                dots += 21;                   // o NMI gasta 7 ciclos de CPU = 21 pontos da PPU
                for (int i = 0; i < 7; i++) Apu.Clock();
            }
        }
    }
}
