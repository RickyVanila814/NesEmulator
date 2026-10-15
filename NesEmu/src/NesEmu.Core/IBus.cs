namespace NesEmu.Core;

/// <summary>
/// Barramento visto pela CPU. Separar isso da CPU permite testá-la com uma
/// memória plana de 64 KB, sem precisar de cartucho, PPU ou APU.
/// </summary>
public interface IBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}
