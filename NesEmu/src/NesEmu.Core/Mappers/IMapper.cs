namespace NesEmu.Core.Mappers;

/// <summary>
/// O mapper é o "circuito" dentro do cartucho que decide como os endereços da
/// CPU e da PPU enxergam PRG/CHR (troca de bancos, etc.).
/// </summary>
public interface IMapper
{
    byte CpuRead(ushort address);      // $4020-$FFFF
    void CpuWrite(ushort address, byte value);
    byte PpuRead(ushort address);      // $0000-$1FFF (pattern tables)
    void PpuWrite(ushort address, byte value);

    /// <summary>Espelhamento das nametables. Mappers como o MMC1 podem mudá-lo em tempo de execução.</summary>
    Mirroring Mirroring { get; }
}

public static class MapperFactory
{
    public static IMapper Create(Cartridge cart) => cart.MapperId switch
    {
        0 => new Mapper0(cart),
        1 => new Mapper1(cart),
        // TODO: 2 (UxROM), 3 (CNROM)
        _ => throw new NotSupportedException(
            $"Mapper {cart.MapperId} ainda não implementado (planejados: 2/UxROM, 3/CNROM).")
    };
}
