using NesEmu.Core;

namespace NesEmu.Tests;

public class Cpu6502Tests
{
    private sealed class FlatBus : IBus
    {
        public readonly byte[] Mem = new byte[65536];
        public byte Read(ushort address) => Mem[address];
        public void Write(ushort address, byte value) => Mem[address] = value;
    }

    /// <summary>Carrega o programa em $8000, aponta o vetor de reset para lá e dá Reset().</summary>
    private static Cpu6502 Make(params byte[] program)
    {
        var bus = new FlatBus();
        program.CopyTo(bus.Mem, 0x8000);
        bus.Mem[0xFFFC] = 0x00;
        bus.Mem[0xFFFD] = 0x80;
        var cpu = new Cpu6502(bus);
        cpu.Reset();
        return cpu;
    }

    [Fact]
    public void Reset_loads_pc_from_vector_and_sets_initial_state()
    {
        var cpu = Make(0xEA);
        Assert.Equal(0x8000, cpu.PC);
        Assert.Equal(0xFD, cpu.SP);
        Assert.Equal(0x24, cpu.P);
        Assert.Equal(7, cpu.Cycles);
    }

    [Fact]
    public void Lda_immediate_sets_zero_and_negative_flags()
    {
        var cpu = Make(0xA9, 0x00, 0xA9, 0x80);
        cpu.Step();
        Assert.Equal(0x00, cpu.A);
        Assert.Equal(0x02, cpu.P & 0x02);   // Z
        cpu.Step();
        Assert.Equal(0x80, cpu.A);
        Assert.Equal(0x80, cpu.P & 0x80);   // N
        Assert.Equal(0x00, cpu.P & 0x02);
    }

    [Fact]
    public void Adc_sets_overflow_for_signed_overflow()
    {
        var cpu = Make(0xA9, 0x50, 0x69, 0x50);   // LDA #$50 ; ADC #$50
        cpu.Step(); cpu.Step();
        Assert.Equal(0xA0, cpu.A);
        Assert.Equal(0x40, cpu.P & 0x40);   // V
        Assert.Equal(0x00, cpu.P & 0x01);   // C limpo
    }

    [Fact]
    public void Adc_sets_carry_and_zero_on_wraparound()
    {
        var cpu = Make(0xA9, 0xFF, 0x69, 0x01);   // LDA #$FF ; ADC #$01
        cpu.Step(); cpu.Step();
        Assert.Equal(0x00, cpu.A);
        Assert.Equal(0x01, cpu.P & 0x01);   // C
        Assert.Equal(0x02, cpu.P & 0x02);   // Z
    }

    [Fact]
    public void Jsr_and_rts_return_to_the_next_instruction()
    {
        // 8000: JSR $8004 ; 8003: NOP ; 8004: LDA #$42 ; 8006: RTS
        var cpu = Make(0x20, 0x04, 0x80, 0xEA, 0xA9, 0x42, 0x60);
        cpu.Step(); cpu.Step(); cpu.Step();
        Assert.Equal(0x8003, cpu.PC);
        Assert.Equal(0x42, cpu.A);
        Assert.Equal(7 + 6 + 2 + 6, cpu.Cycles);
    }

    [Fact]
    public void Taken_branch_costs_one_extra_cycle()
    {
        var cpu = Make(0xA9, 0x00, 0xF0, 0x02, 0xEA, 0xEA, 0xEA);   // LDA #0 ; BEQ +2
        cpu.Step();
        int cycles = cpu.Step();
        Assert.Equal(3, cycles);
        Assert.Equal(0x8006, cpu.PC);
    }

    [Fact]
    public void Php_plp_roundtrip_keeps_break_flag_out_of_p()
    {
        var cpu = Make(0x38, 0x08, 0x18, 0x28);   // SEC ; PHP ; CLC ; PLP
        for (int i = 0; i < 4; i++) cpu.Step();
        Assert.Equal(0x01, cpu.P & 0x01);   // carry restaurado
        Assert.Equal(0x00, cpu.P & 0x10);   // B nunca fica em P
        Assert.Equal(0x20, cpu.P & 0x20);   // U sempre ligado
    }
}
