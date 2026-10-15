using static NesEmu.Core.AddrMode;
using static NesEmu.Core.Op;

namespace NesEmu.Core;

public enum AddrMode { Imp, Acc, Imm, Zp, ZpX, ZpY, Abs, AbsX, AbsY, Ind, IndX, IndY, Rel }

public enum Op
{
    ADC, AND, ASL, BCC, BCS, BEQ, BIT, BMI, BNE, BPL, BRK, BVC, BVS, CLC, CLD, CLI, CLV, CMP, CPX, CPY,
    DEC, DEX, DEY, EOR, INC, INX, INY, JMP, JSR, LDA, LDX, LDY, LSR, NOP, ORA, PHA, PHP, PLA, PLP,
    ROL, ROR, RTI, RTS, SBC, SEC, SED, SEI, STA, STX, STY, TAX, TAY, TSX, TXA, TXS, TYA,
    XXX // opcode não oficial / ainda não implementado
}

public readonly record struct Instruction(Op Op, AddrMode Mode, int Cycles, bool PageCrossPenalty);

/// <summary>
/// CPU 6502 do NES (Ricoh 2A03: igual ao 6502, mas sem o modo decimal).
/// Implementa as 151 instruções oficiais. Opcodes "ilegais" lançam NotSupportedException.
/// </summary>
public sealed class Cpu6502
{
    // Bits do registrador de status P
    private const byte FlagC = 0x01, FlagZ = 0x02, FlagI = 0x04, FlagD = 0x08,
                       FlagB = 0x10, FlagU = 0x20, FlagV = 0x40, FlagN = 0x80;

    private readonly IBus _bus;

    public byte A, X, Y, SP, P;
    public ushort PC;
    public long Cycles;

    public Cpu6502(IBus bus) => _bus = bus;

    public void Reset()
    {
        A = X = Y = 0;
        SP = 0xFD;
        P = FlagI | FlagU;          // 0x24
        PC = ReadWord(0xFFFC);
        Cycles = 7;
    }

    public void Nmi() => Interrupt(0xFFFA);
    /// <summary>Atende um IRQ se as interrupções estiverem habilitadas. Devolve true se foi atendido.</summary>
    public bool Irq()
    {
        if (GetFlag(FlagI)) return false;
        Interrupt(0xFFFE);
        return true;
    }

    private void Interrupt(ushort vector)
    {
        PushWord(PC);
        Push((byte)((P & ~FlagB) | FlagU));
        SetFlag(FlagI, true);
        PC = ReadWord(vector);
        Cycles += 7;
    }

    public bool NextOpcodeIsOfficial => Table[_bus.Read(PC)].Op != XXX;

    /// <summary>Linha de depuração: PC antes de executar + registradores. Útil p/ comparar com nestest.log.</summary>
    public string TraceLine() =>
        $"{PC:X4}  A:{A:X2} X:{X:X2} Y:{Y:X2} P:{P:X2} SP:{SP:X2} CYC:{Cycles}";

    /// <summary>Executa uma instrução e devolve quantos ciclos ela gastou.</summary>
    public int Step()
    {
        ushort opcodeAddress = PC;
        byte opcode = _bus.Read(PC++);
        var ins = Table[opcode];
        if (ins.Op == XXX)
        {
            PC = opcodeAddress;
            throw new NotSupportedException($"Opcode {opcode:X2} em ${opcodeAddress:X4} não é oficial / não implementado.");
        }

        int cycles = ins.Cycles;
        ushort addr = ResolveAddress(ins.Mode, out bool crossed);
        if (crossed && ins.PageCrossPenalty) cycles++;

        switch (ins.Op)
        {
            // --- aritmética e lógica ---
            case ADC: Adc(_bus.Read(addr)); break;
            case SBC: Adc((byte)~_bus.Read(addr)); break;
            case AND: A &= _bus.Read(addr); SetZN(A); break;
            case ORA: A |= _bus.Read(addr); SetZN(A); break;
            case EOR: A ^= _bus.Read(addr); SetZN(A); break;
            case CMP: Compare(A, _bus.Read(addr)); break;
            case CPX: Compare(X, _bus.Read(addr)); break;
            case CPY: Compare(Y, _bus.Read(addr)); break;
            case BIT:
            {
                byte m = _bus.Read(addr);
                SetFlag(FlagZ, (A & m) == 0);
                SetFlag(FlagV, (m & 0x40) != 0);
                SetFlag(FlagN, (m & 0x80) != 0);
                break;
            }

            // --- deslocamentos e rotações (acumulador ou memória) ---
            case ASL:
            {
                byte v = ReadOperand(ins.Mode, addr);
                SetFlag(FlagC, (v & 0x80) != 0);
                v = (byte)(v << 1);
                WriteOperand(ins.Mode, addr, v); SetZN(v); break;
            }
            case LSR:
            {
                byte v = ReadOperand(ins.Mode, addr);
                SetFlag(FlagC, (v & 0x01) != 0);
                v = (byte)(v >> 1);
                WriteOperand(ins.Mode, addr, v); SetZN(v); break;
            }
            case ROL:
            {
                byte v = ReadOperand(ins.Mode, addr);
                int carryIn = GetFlag(FlagC) ? 1 : 0;
                SetFlag(FlagC, (v & 0x80) != 0);
                v = (byte)((v << 1) | carryIn);
                WriteOperand(ins.Mode, addr, v); SetZN(v); break;
            }
            case ROR:
            {
                byte v = ReadOperand(ins.Mode, addr);
                int carryIn = GetFlag(FlagC) ? 0x80 : 0;
                SetFlag(FlagC, (v & 0x01) != 0);
                v = (byte)((v >> 1) | carryIn);
                WriteOperand(ins.Mode, addr, v); SetZN(v); break;
            }

            // --- incremento / decremento ---
            case INC: { byte v = (byte)(_bus.Read(addr) + 1); _bus.Write(addr, v); SetZN(v); break; }
            case DEC: { byte v = (byte)(_bus.Read(addr) - 1); _bus.Write(addr, v); SetZN(v); break; }
            case INX: X++; SetZN(X); break;
            case INY: Y++; SetZN(Y); break;
            case DEX: X--; SetZN(X); break;
            case DEY: Y--; SetZN(Y); break;

            // --- loads, stores e transferências ---
            case LDA: A = _bus.Read(addr); SetZN(A); break;
            case LDX: X = _bus.Read(addr); SetZN(X); break;
            case LDY: Y = _bus.Read(addr); SetZN(Y); break;
            case STA: _bus.Write(addr, A); break;
            case STX: _bus.Write(addr, X); break;
            case STY: _bus.Write(addr, Y); break;
            case TAX: X = A; SetZN(X); break;
            case TAY: Y = A; SetZN(Y); break;
            case TXA: A = X; SetZN(A); break;
            case TYA: A = Y; SetZN(A); break;
            case TSX: X = SP; SetZN(X); break;
            case TXS: SP = X; break;

            // --- pilha ---
            case PHA: Push(A); break;
            case PHP: Push((byte)(P | FlagB | FlagU)); break;
            case PLA: A = Pop(); SetZN(A); break;
            case PLP: P = (byte)((Pop() & ~FlagB) | FlagU); break;

            // --- flags ---
            case CLC: SetFlag(FlagC, false); break;
            case SEC: SetFlag(FlagC, true); break;
            case CLI: SetFlag(FlagI, false); break;
            case SEI: SetFlag(FlagI, true); break;
            case CLD: SetFlag(FlagD, false); break;
            case SED: SetFlag(FlagD, true); break;
            case CLV: SetFlag(FlagV, false); break;

            // --- desvios ---
            case BCC: cycles += Branch(!GetFlag(FlagC), addr); break;
            case BCS: cycles += Branch(GetFlag(FlagC), addr); break;
            case BNE: cycles += Branch(!GetFlag(FlagZ), addr); break;
            case BEQ: cycles += Branch(GetFlag(FlagZ), addr); break;
            case BPL: cycles += Branch(!GetFlag(FlagN), addr); break;
            case BMI: cycles += Branch(GetFlag(FlagN), addr); break;
            case BVC: cycles += Branch(!GetFlag(FlagV), addr); break;
            case BVS: cycles += Branch(GetFlag(FlagV), addr); break;

            // --- saltos e sub-rotinas ---
            case JMP: PC = addr; break;
            case JSR: PushWord((ushort)(PC - 1)); PC = addr; break;
            case RTS: PC = (ushort)(PopWord() + 1); break;
            case RTI: P = (byte)((Pop() & ~FlagB) | FlagU); PC = PopWord(); break;
            case BRK:
                PC++;                                   // BRK pula o byte "assinatura"
                PushWord(PC);
                Push((byte)(P | FlagB | FlagU));
                SetFlag(FlagI, true);
                PC = ReadWord(0xFFFE);
                break;
            case NOP: break;
        }

        Cycles += cycles;
        return cycles;
    }

    // ---------- modos de endereçamento ----------

    private ushort ResolveAddress(AddrMode mode, out bool pageCrossed)
    {
        pageCrossed = false;
        switch (mode)
        {
            case Imm: return PC++;
            case Zp: return _bus.Read(PC++);
            case ZpX: return (byte)(_bus.Read(PC++) + X);
            case ZpY: return (byte)(_bus.Read(PC++) + Y);
            case Abs: { ushort a = ReadWord(PC); PC += 2; return a; }
            case AbsX:
            {
                ushort b = ReadWord(PC); PC += 2;
                ushort a = (ushort)(b + X);
                pageCrossed = (a & 0xFF00) != (b & 0xFF00);
                return a;
            }
            case AbsY:
            {
                ushort b = ReadWord(PC); PC += 2;
                ushort a = (ushort)(b + Y);
                pageCrossed = (a & 0xFF00) != (b & 0xFF00);
                return a;
            }
            case Ind: // só o JMP usa; tem o famoso bug: o ponteiro não atravessa a página
            {
                ushort p = ReadWord(PC); PC += 2;
                byte lo = _bus.Read(p);
                byte hi = _bus.Read((ushort)((p & 0xFF00) | ((p + 1) & 0x00FF)));
                return (ushort)(lo | (hi << 8));
            }
            case IndX:
            {
                byte z = (byte)(_bus.Read(PC++) + X);
                return (ushort)(_bus.Read(z) | (_bus.Read((byte)(z + 1)) << 8));
            }
            case IndY:
            {
                byte z = _bus.Read(PC++);
                ushort b = (ushort)(_bus.Read(z) | (_bus.Read((byte)(z + 1)) << 8));
                ushort a = (ushort)(b + Y);
                pageCrossed = (a & 0xFF00) != (b & 0xFF00);
                return a;
            }
            case Rel:
            {
                sbyte offset = (sbyte)_bus.Read(PC++);
                return (ushort)(PC + offset);
            }
            default: return 0; // Imp e Acc não usam endereço
        }
    }

    // ---------- helpers ----------

    private ushort ReadWord(ushort address) => (ushort)(_bus.Read(address) | (_bus.Read((ushort)(address + 1)) << 8));

    private byte ReadOperand(AddrMode mode, ushort addr) => mode == Acc ? A : _bus.Read(addr);

    private void WriteOperand(AddrMode mode, ushort addr, byte value)
    {
        if (mode == Acc) A = value; else _bus.Write(addr, value);
    }

    private void Push(byte value) { _bus.Write((ushort)(0x0100 + SP), value); SP--; }
    private byte Pop() { SP++; return _bus.Read((ushort)(0x0100 + SP)); }
    private void PushWord(ushort value) { Push((byte)(value >> 8)); Push((byte)value); }
    private ushort PopWord() { byte lo = Pop(); byte hi = Pop(); return (ushort)(lo | (hi << 8)); }

    private bool GetFlag(byte flag) => (P & flag) != 0;
    private void SetFlag(byte flag, bool on) => P = on ? (byte)(P | flag) : (byte)(P & ~flag);
    private void SetZN(byte v) { SetFlag(FlagZ, v == 0); SetFlag(FlagN, (v & 0x80) != 0); }

    private void Adc(byte v)
    {
        int sum = A + v + (GetFlag(FlagC) ? 1 : 0);
        SetFlag(FlagC, sum > 0xFF);
        SetFlag(FlagV, ((A ^ sum) & (v ^ sum) & 0x80) != 0);
        A = (byte)sum;
        SetZN(A);
    }

    private void Compare(byte reg, byte v)
    {
        SetFlag(FlagC, reg >= v);
        SetZN((byte)(reg - v));
    }

    /// <summary>Desvio tomado custa +1 ciclo; +1 extra se cruzar página. Devolve os ciclos extras.</summary>
    private int Branch(bool condition, ushort target)
    {
        if (!condition) return 0;
        int extra = (PC & 0xFF00) != (target & 0xFF00) ? 2 : 1;
        PC = target;
        return extra;
    }

    // ---------- tabela de opcodes ----------

    private static readonly Instruction[] Table = BuildTable();

    private static Instruction[] BuildTable()
    {
        var t = new Instruction[256];
        Array.Fill(t, new Instruction(XXX, Imp, 2, false));

        void D(int opcode, Op op, AddrMode mode, int cycles, bool pageCross = false)
            => t[opcode] = new Instruction(op, mode, cycles, pageCross);

        // Grupo "ALU" (8 modos): base + 09 imm, 05 zp, 15 zpx, 0D abs, 1D absx, 19 absy, 01 indx, 11 indy
        void Alu(Op op, int b)
        {
            D(b + 0x09, op, Imm, 2);
            D(b + 0x05, op, Zp, 3);
            D(b + 0x15, op, ZpX, 4);
            D(b + 0x0D, op, Abs, 4);
            D(b + 0x1D, op, AbsX, 4, true);
            D(b + 0x19, op, AbsY, 4, true);
            D(b + 0x01, op, IndX, 6);
            D(b + 0x11, op, IndY, 5, true);
        }
        Alu(ORA, 0x00); Alu(AND, 0x20); Alu(EOR, 0x40); Alu(ADC, 0x60);
        Alu(LDA, 0xA0); Alu(CMP, 0xC0); Alu(SBC, 0xE0);

        // STA: mesmo padrão, sem imediato e sem penalidade de página (ciclos fixos)
        D(0x85, STA, Zp, 3); D(0x95, STA, ZpX, 4); D(0x8D, STA, Abs, 4);
        D(0x9D, STA, AbsX, 5); D(0x99, STA, AbsY, 5); D(0x81, STA, IndX, 6); D(0x91, STA, IndY, 6);

        // Deslocamentos: base + 0A acc, 06 zp, 16 zpx, 0E abs, 1E absx
        void Shift(Op op, int b)
        {
            D(b + 0x0A, op, Acc, 2);
            D(b + 0x06, op, Zp, 5);
            D(b + 0x16, op, ZpX, 6);
            D(b + 0x0E, op, Abs, 6);
            D(b + 0x1E, op, AbsX, 7);
        }
        Shift(ASL, 0x00); Shift(ROL, 0x20); Shift(LSR, 0x40); Shift(ROR, 0x60);

        // INC/DEC: sem modo acumulador
        void IncDec(Op op, int b)
        {
            D(b + 0x06, op, Zp, 5);
            D(b + 0x16, op, ZpX, 6);
            D(b + 0x0E, op, Abs, 6);
            D(b + 0x1E, op, AbsX, 7);
        }
        IncDec(DEC, 0xC0); IncDec(INC, 0xE0);

        // LDX / LDY
        D(0xA2, LDX, Imm, 2); D(0xA6, LDX, Zp, 3); D(0xB6, LDX, ZpY, 4); D(0xAE, LDX, Abs, 4); D(0xBE, LDX, AbsY, 4, true);
        D(0xA0, LDY, Imm, 2); D(0xA4, LDY, Zp, 3); D(0xB4, LDY, ZpX, 4); D(0xAC, LDY, Abs, 4); D(0xBC, LDY, AbsX, 4, true);

        // STX / STY
        D(0x86, STX, Zp, 3); D(0x96, STX, ZpY, 4); D(0x8E, STX, Abs, 4);
        D(0x84, STY, Zp, 3); D(0x94, STY, ZpX, 4); D(0x8C, STY, Abs, 4);

        // CPX / CPY / BIT
        D(0xE0, CPX, Imm, 2); D(0xE4, CPX, Zp, 3); D(0xEC, CPX, Abs, 4);
        D(0xC0, CPY, Imm, 2); D(0xC4, CPY, Zp, 3); D(0xCC, CPY, Abs, 4);
        D(0x24, BIT, Zp, 3); D(0x2C, BIT, Abs, 4);

        // Desvios (todos relativos, 2 ciclos base)
        D(0x10, BPL, Rel, 2); D(0x30, BMI, Rel, 2); D(0x50, BVC, Rel, 2); D(0x70, BVS, Rel, 2);
        D(0x90, BCC, Rel, 2); D(0xB0, BCS, Rel, 2); D(0xD0, BNE, Rel, 2); D(0xF0, BEQ, Rel, 2);

        // Saltos
        D(0x4C, JMP, Abs, 3); D(0x6C, JMP, Ind, 5); D(0x20, JSR, Abs, 6);
        D(0x60, RTS, Imp, 6); D(0x40, RTI, Imp, 6); D(0x00, BRK, Imp, 7);

        // Implícitos
        D(0x18, CLC, Imp, 2); D(0x38, SEC, Imp, 2); D(0x58, CLI, Imp, 2); D(0x78, SEI, Imp, 2);
        D(0xD8, CLD, Imp, 2); D(0xF8, SED, Imp, 2); D(0xB8, CLV, Imp, 2);
        D(0xE8, INX, Imp, 2); D(0xC8, INY, Imp, 2); D(0xCA, DEX, Imp, 2); D(0x88, DEY, Imp, 2);
        D(0xAA, TAX, Imp, 2); D(0xA8, TAY, Imp, 2); D(0x8A, TXA, Imp, 2); D(0x98, TYA, Imp, 2);
        D(0xBA, TSX, Imp, 2); D(0x9A, TXS, Imp, 2);
        D(0x48, PHA, Imp, 3); D(0x08, PHP, Imp, 3); D(0x68, PLA, Imp, 4); D(0x28, PLP, Imp, 4);
        D(0xEA, NOP, Imp, 2);

        return t;
    }
}
