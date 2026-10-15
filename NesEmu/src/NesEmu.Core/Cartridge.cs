namespace NesEmu.Core;

public enum Mirroring { Horizontal, Vertical, FourScreen, SingleScreenA, SingleScreenB }

/// <summary>
/// Cartucho carregado de um arquivo no formato iNES (.nes).
///
/// Cabeçalho (16 bytes):
///   0-3  "NES" + 0x1A
///   4    tamanho da PRG-ROM em blocos de 16 KB
///   5    tamanho da CHR-ROM em blocos de 8 KB (0 = o cartucho usa CHR-RAM)
///   6    flags 6: bit0 espelhamento vertical, bit1 bateria, bit2 trainer,
///                 bit3 4 telas, bits 4-7 = nibble baixo do mapper
///   7    flags 7: bits 4-7 = nibble alto do mapper
/// </summary>
public sealed class Cartridge
{
    public byte[] PrgRom { get; }
    public byte[] ChrMem { get; }   // CHR-ROM ou, se ChrIsRam, 8 KB de CHR-RAM
    public bool ChrIsRam { get; }
    public byte[] PrgRam { get; } = new byte[0x2000];
    public int MapperId { get; }
    public Mirroring Mirroring { get; }
    public bool HasBattery { get; }

    private Cartridge(byte[] prg, byte[] chr, bool chrIsRam, int mapperId, Mirroring mirroring, bool battery)
    {
        PrgRom = prg;
        ChrMem = chr;
        ChrIsRam = chrIsRam;
        MapperId = mapperId;
        Mirroring = mirroring;
        HasBattery = battery;
    }

    public static Cartridge Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static Cartridge FromBytes(byte[] d)
    {
        if (d.Length < 16 || d[0] != 'N' || d[1] != 'E' || d[2] != 'S' || d[3] != 0x1A)
            throw new InvalidDataException("Arquivo não é iNES: cabeçalho 'NES\\x1A' ausente.");

        int prgSize = d[4] * 16 * 1024;
        int chrBanks = d[5];
        byte flags6 = d[6];
        byte flags7 = d[7];

        bool hasTrainer = (flags6 & 0x04) != 0;
        var mirroring = (flags6 & 0x08) != 0 ? Mirroring.FourScreen
                      : (flags6 & 0x01) != 0 ? Mirroring.Vertical
                      : Mirroring.Horizontal;
        int mapperId = (flags7 & 0xF0) | (flags6 >> 4);

        int offset = 16 + (hasTrainer ? 512 : 0);
        int chrSize = chrBanks * 8 * 1024;
        if (d.Length < offset + prgSize + chrSize)
            throw new InvalidDataException("ROM truncada: o arquivo é menor do que o cabeçalho informa.");

        var prg = new byte[prgSize];
        Array.Copy(d, offset, prg, 0, prgSize);

        bool chrIsRam = chrBanks == 0;
        var chr = new byte[chrIsRam ? 8 * 1024 : chrSize];
        if (!chrIsRam) Array.Copy(d, offset + prgSize, chr, 0, chrSize);

        return new Cartridge(prg, chr, chrIsRam, mapperId, mirroring, (flags6 & 0x02) != 0);
    }
}
