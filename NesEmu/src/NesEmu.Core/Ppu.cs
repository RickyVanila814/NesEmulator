using NesEmu.Core.Mappers;

namespace NesEmu.Core;

/// <summary>
/// PPU 2C02 (vídeo do NES), emulada "ponto a ponto": cada chamada de Clock() é 1 ponto (dot).
/// Uma linha tem 341 pontos, um quadro tem 262 linhas (-1 = pré-render, 0-239 visíveis,
/// 240 pós-render, 241-260 vblank). A CPU roda 1 ciclo a cada 3 pontos da PPU.
///
/// Registradores internos de scroll ("loopy"):
///   v = endereço atual de VRAM (15 bits: yyy NN YYYYY XXXXX = fine Y, nametable, coarse Y, coarse X)
///   t = endereço temporário,  x = fine X (3 bits),  w = alternador do primeiro/segundo write
/// </summary>
public sealed class Ppu
{
    public const int Width = 256;
    public const int Height = 240;

    /// <summary>Quadro atual em ARGB (0xAARRGGBB), 256x240.</summary>
    public readonly uint[] Frame = new uint[Width * Height];

    public bool FrameComplete;
    /// <summary>A PPU pede um NMI; quem orquestra (classe Nes) deve chamar cpu.Nmi() e zerar este flag.</summary>
    public bool NmiRequested;

    public readonly byte[] Oam = new byte[256];   // 64 sprites x 4 bytes: Y, tile, atributos, X

    private readonly IMapper _mapper;
    private readonly byte[] _vram = new byte[2048];   // 2 nametables de 1 KB
    private readonly byte[] _palette = new byte[32];

    // $2000 PPUCTRL: bits 0-1 nametable, 2 incremento (1/32), 3 padrão dos sprites, 4 padrão do fundo,
    //                5 sprites 8x16, 7 NMI no vblank
    // $2001 PPUMASK: 0 cinza, 1 fundo na borda esq., 2 sprites na borda esq., 3 mostra fundo, 4 mostra sprites
    // $2002 PPUSTATUS: 5 overflow de sprites, 6 sprite 0 hit, 7 vblank
    private byte _ctrl, _mask, _status, _oamAddr, _readBuffer, _fineX;
    private ushort _v, _t;
    private bool _w;

    private int _scanline;
    private int _cycle;
    private bool _oddFrame;

    // Fundo: dados buscados para o próximo tile e registradores de deslocamento de 16 bits
    private byte _nextTileId, _nextAttrib, _nextLsb, _nextMsb;
    private ushort _shiftPatLo, _shiftPatHi, _shiftAttrLo, _shiftAttrHi;

    // Sprites da próxima linha (no máximo 8)
    private readonly byte[] _spriteY = new byte[8];
    private readonly byte[] _spriteId = new byte[8];
    private readonly byte[] _spriteAttr = new byte[8];
    private readonly byte[] _spriteX = new byte[8];
    private readonly byte[] _spriteShiftLo = new byte[8];
    private readonly byte[] _spriteShiftHi = new byte[8];
    private int _spriteCount;
    private bool _spriteZeroPossible;

    public Ppu(IMapper mapper) => _mapper = mapper;

    // ------------------------------------------------------------------
    // Interface com a CPU ($2000-$2007)
    // ------------------------------------------------------------------

    public byte CpuRead(int reg)
    {
        switch (reg & 7)
        {
            case 2:   // PPUSTATUS: ler limpa o vblank e o alternador w
            {
                byte data = (byte)((_status & 0xE0) | (_readBuffer & 0x1F));
                _status &= 0x7F;
                _w = false;
                return data;
            }
            case 4: return Oam[_oamAddr];
            case 7:   // PPUDATA: leituras são "atrasadas" 1 acesso, exceto paleta
            {
                byte data = _readBuffer;
                _readBuffer = PpuRead(_v);
                if ((_v & 0x3FFF) >= 0x3F00) data = _readBuffer;
                _v = (ushort)((_v + ((_ctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
                return data;
            }
            default: return 0;
        }
    }

    public void CpuWrite(int reg, byte value)
    {
        switch (reg & 7)
        {
            case 0:
            {
                bool nmiWasOn = (_ctrl & 0x80) != 0;
                _ctrl = value;
                _t = (ushort)((_t & 0xF3FF) | ((value & 0x03) << 10));
                if (!nmiWasOn && (_ctrl & 0x80) != 0 && (_status & 0x80) != 0) NmiRequested = true;
                break;
            }
            case 1: _mask = value; break;
            case 3: _oamAddr = value; break;
            case 4: WriteOamData(value); break;
            case 5:   // PPUSCROLL
                if (!_w)
                {
                    _fineX = (byte)(value & 7);
                    _t = (ushort)((_t & 0xFFE0) | (value >> 3));
                    _w = true;
                }
                else
                {
                    _t = (ushort)((_t & 0x8C1F) | ((value & 7) << 12) | ((value >> 3) << 5));
                    _w = false;
                }
                break;
            case 6:   // PPUADDR
                if (!_w)
                {
                    _t = (ushort)((_t & 0x00FF) | ((value & 0x3F) << 8));
                    _w = true;
                }
                else
                {
                    _t = (ushort)((_t & 0xFF00) | value);
                    _v = _t;
                    _w = false;
                }
                break;
            case 7:   // PPUDATA
                PpuWrite(_v, value);
                _v = (ushort)((_v + ((_ctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
                break;
        }
    }

    /// <summary>Usado por $2004 e pelo DMA de OAM ($4014).</summary>
    public void WriteOamData(byte value) => Oam[_oamAddr++] = value;

    // ------------------------------------------------------------------
    // Memória da PPU: $0000-$1FFF cartucho (CHR), $2000-$3EFF nametables, $3F00-$3FFF paleta
    // ------------------------------------------------------------------

    private byte PpuRead(int address)
    {
        address &= 0x3FFF;
        if (address < 0x2000) return _mapper.PpuRead((ushort)address);
        if (address < 0x3F00) return _vram[MirrorNametable(address)];
        return (byte)(_palette[PaletteIndex(address)] & ((_mask & 0x01) != 0 ? 0x30 : 0x3F));
    }

    private void PpuWrite(int address, byte value)
    {
        address &= 0x3FFF;
        if (address < 0x2000) _mapper.PpuWrite((ushort)address, value);
        else if (address < 0x3F00) _vram[MirrorNametable(address)] = value;
        else _palette[PaletteIndex(address)] = value;
    }

    // $3F10/$3F14/$3F18/$3F1C são espelhos de $3F00/$3F04/$3F08/$3F0C
    private static int PaletteIndex(int address)
    {
        int i = address & 0x1F;
        if ((i & 0x13) == 0x10) i &= ~0x10;
        return i;
    }

    private int MirrorNametable(int address)
    {
        int a = (address - 0x2000) & 0x0FFF;
        int table = a >> 10;
        int offset = a & 0x3FF;
        int physical = _mapper.Mirroring switch
        {
            Mirroring.Horizontal => table >> 1,
            Mirroring.Vertical => table & 1,
            Mirroring.SingleScreenA => 0,
            Mirroring.SingleScreenB => 1,
            _ => table & 1   // FourScreen precisaria de 4 KB de VRAM; aproximado como vertical
        };
        return physical * 0x400 + offset;
    }

    // ------------------------------------------------------------------
    // Relógio da PPU: 1 ponto por chamada
    // ------------------------------------------------------------------

    public void Clock()
    {
        bool rendering = (_mask & 0x18) != 0;

        if (_scanline >= -1 && _scanline < 240)
        {
            if (_scanline == -1 && _cycle == 1)
            {
                _status &= 0x1F;               // fim do vblank: limpa vblank, sprite 0 hit e overflow
                _spriteCount = 0;
                Array.Clear(_spriteShiftLo);
                Array.Clear(_spriteShiftHi);
            }

            // Busca dos tiles do fundo: 4 acessos (2 ciclos cada) por tile de 8 pixels
            if ((_cycle >= 2 && _cycle < 258) || (_cycle >= 321 && _cycle < 338))
            {
                UpdateShifters();

                switch ((_cycle - 1) % 8)
                {
                    case 0:
                        LoadBackgroundShifters();
                        _nextTileId = PpuRead(0x2000 | (_v & 0x0FFF));
                        break;
                    case 2:
                        _nextAttrib = PpuRead(0x23C0 | (_v & 0x0C00) | ((_v >> 4) & 0x38) | ((_v >> 2) & 0x07));
                        if ((_v & 0x40) != 0) _nextAttrib >>= 4;
                        if ((_v & 0x02) != 0) _nextAttrib >>= 2;
                        _nextAttrib &= 3;
                        break;
                    case 4:
                        _nextLsb = PpuRead(((_ctrl & 0x10) << 8) + (_nextTileId << 4) + ((_v >> 12) & 7));
                        break;
                    case 6:
                        _nextMsb = PpuRead(((_ctrl & 0x10) << 8) + (_nextTileId << 4) + ((_v >> 12) & 7) + 8);
                        break;
                    case 7:
                        if (rendering) IncrementScrollX();
                        break;
                }
            }

            if (rendering)
            {
                if (_cycle == 256) IncrementScrollY();
                if (_cycle == 257)
                {
                    LoadBackgroundShifters();
                    _v = (ushort)((_v & ~0x041F) | (_t & 0x041F));          // copia X de t para v
                }
                if (_scanline == -1 && _cycle >= 280 && _cycle < 305)
                    _v = (ushort)((_v & ~0x7BE0) | (_t & 0x7BE0));          // copia Y de t para v
            }

            if (_cycle == 338 || _cycle == 340) _nextTileId = PpuRead(0x2000 | (_v & 0x0FFF));

            if (_cycle == 257 && _scanline >= 0) EvaluateSprites();
            if (_cycle == 340) FetchSpritePatterns();
        }

        if (_scanline == 241 && _cycle == 1)
        {
            _status |= 0x80;                                  // começa o vblank
            if ((_ctrl & 0x80) != 0) NmiRequested = true;
        }

        if (_scanline >= 0 && _scanline < 240 && _cycle >= 1 && _cycle <= 256)
            RenderPixel();

        _cycle++;
        // Em quadros ímpares com renderização ligada, a linha pré-render tem 1 ponto a menos
        if (rendering && _scanline == -1 && _cycle == 340 && _oddFrame) _cycle++;
        if (_cycle >= 341)
        {
            _cycle = 0;
            _scanline++;
            if (_scanline >= 261)
            {
                _scanline = -1;
                FrameComplete = true;
                _oddFrame = !_oddFrame;
            }
        }
    }

    // ------------------------------------------------------------------
    // Scroll
    // ------------------------------------------------------------------

    private void IncrementScrollX()
    {
        if ((_v & 0x001F) == 31)
        {
            _v = (ushort)(_v & ~0x001F);
            _v ^= 0x0400;                     // passa para a nametable horizontal vizinha
        }
        else _v++;
    }

    private void IncrementScrollY()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v += 0x1000;                     // fine Y
            return;
        }
        _v = (ushort)(_v & ~0x7000);
        int y = (_v & 0x03E0) >> 5;           // coarse Y
        if (y == 29) { y = 0; _v ^= 0x0800; } // passa para a nametable vertical vizinha
        else if (y == 31) y = 0;
        else y++;
        _v = (ushort)((_v & ~0x03E0) | (y << 5));
    }

    // ------------------------------------------------------------------
    // Fundo
    // ------------------------------------------------------------------

    private void LoadBackgroundShifters()
    {
        _shiftPatLo = (ushort)((_shiftPatLo & 0xFF00) | _nextLsb);
        _shiftPatHi = (ushort)((_shiftPatHi & 0xFF00) | _nextMsb);
        _shiftAttrLo = (ushort)((_shiftAttrLo & 0xFF00) | ((_nextAttrib & 1) != 0 ? 0xFF : 0x00));
        _shiftAttrHi = (ushort)((_shiftAttrHi & 0xFF00) | ((_nextAttrib & 2) != 0 ? 0xFF : 0x00));
    }

    private void UpdateShifters()
    {
        if ((_mask & 0x08) != 0)
        {
            _shiftPatLo <<= 1;
            _shiftPatHi <<= 1;
            _shiftAttrLo <<= 1;
            _shiftAttrHi <<= 1;
        }

        if ((_mask & 0x10) != 0 && _cycle >= 1 && _cycle < 258)
        {
            for (int i = 0; i < _spriteCount; i++)
            {
                if (_spriteX[i] > 0) _spriteX[i]--;
                else
                {
                    _spriteShiftLo[i] <<= 1;
                    _spriteShiftHi[i] <<= 1;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Sprites
    // ------------------------------------------------------------------

    /// <summary>Escolhe até 8 sprites que aparecem na PRÓXIMA linha (o byte Y da OAM é "topo - 1").</summary>
    private void EvaluateSprites()
    {
        _spriteCount = 0;
        _spriteZeroPossible = false;
        Array.Clear(_spriteShiftLo);
        Array.Clear(_spriteShiftHi);

        int height = (_ctrl & 0x20) != 0 ? 16 : 8;
        for (int n = 0; n < 64; n++)
        {
            int diff = _scanline - Oam[n * 4];
            if (diff < 0 || diff >= height) continue;

            if (_spriteCount < 8)
            {
                if (n == 0) _spriteZeroPossible = true;
                _spriteY[_spriteCount] = Oam[n * 4];
                _spriteId[_spriteCount] = Oam[n * 4 + 1];
                _spriteAttr[_spriteCount] = Oam[n * 4 + 2];
                _spriteX[_spriteCount] = Oam[n * 4 + 3];
                _spriteCount++;
            }
            else
            {
                _status |= 0x20;              // sprite overflow
                break;
            }
        }
    }

    /// <summary>Lê da CHR as duas camadas de bits de cada sprite escolhido e as carrega nos shifters.</summary>
    private void FetchSpritePatterns()
    {
        for (int i = 0; i < _spriteCount; i++)
        {
            int row = _scanline - _spriteY[i];
            byte id = _spriteId[i];
            byte attr = _spriteAttr[i];
            bool flipV = (attr & 0x80) != 0;
            int addr;

            if ((_ctrl & 0x20) == 0)          // sprites 8x8
            {
                if (flipV) row = 7 - row;
                addr = ((_ctrl & 0x08) << 9) | (id << 4) | row;
            }
            else                              // sprites 8x16: o bit 0 do id escolhe a tabela de padrões
            {
                if (flipV) row = 15 - row;
                int tile = (id & 0xFE) + (row >= 8 ? 1 : 0);
                addr = ((id & 1) << 12) | (tile << 4) | (row & 7);
            }

            byte lo = PpuRead(addr);
            byte hi = PpuRead(addr + 8);
            if ((attr & 0x40) != 0)           // flip horizontal
            {
                lo = FlipByte(lo);
                hi = FlipByte(hi);
            }
            _spriteShiftLo[i] = lo;
            _spriteShiftHi[i] = hi;
        }
    }

    private static byte FlipByte(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        return b;
    }

    // ------------------------------------------------------------------
    // Composição do pixel
    // ------------------------------------------------------------------

    private void RenderPixel()
    {
        int x = _cycle - 1;

        int bgPixel = 0, bgPalette = 0;
        if ((_mask & 0x08) != 0 && (x >= 8 || (_mask & 0x02) != 0))
        {
            int bit = 0x8000 >> _fineX;
            int p0 = (_shiftPatLo & bit) != 0 ? 1 : 0;
            int p1 = (_shiftPatHi & bit) != 0 ? 1 : 0;
            bgPixel = (p1 << 1) | p0;
            int a0 = (_shiftAttrLo & bit) != 0 ? 1 : 0;
            int a1 = (_shiftAttrHi & bit) != 0 ? 1 : 0;
            bgPalette = (a1 << 1) | a0;
        }

        int fgPixel = 0, fgPalette = 0;
        bool fgPriority = false, spriteZeroRendered = false;
        if ((_mask & 0x10) != 0 && (x >= 8 || (_mask & 0x04) != 0))
        {
            for (int i = 0; i < _spriteCount; i++)
            {
                if (_spriteX[i] != 0) continue;
                int lo = (_spriteShiftLo[i] & 0x80) != 0 ? 1 : 0;
                int hi = (_spriteShiftHi[i] & 0x80) != 0 ? 1 : 0;
                int px = (hi << 1) | lo;
                if (px == 0) continue;        // transparente: tenta o próximo sprite
                fgPixel = px;
                fgPalette = (_spriteAttr[i] & 0x03) + 4;
                fgPriority = (_spriteAttr[i] & 0x20) == 0;
                spriteZeroRendered = i == 0;
                break;
            }
        }

        int pixel, palette;
        if (bgPixel == 0 && fgPixel == 0) { pixel = 0; palette = 0; }
        else if (bgPixel == 0) { pixel = fgPixel; palette = fgPalette; }
        else if (fgPixel == 0) { pixel = bgPixel; palette = bgPalette; }
        else
        {
            if (fgPriority) { pixel = fgPixel; palette = fgPalette; }
            else { pixel = bgPixel; palette = bgPalette; }

            // Sprite 0 hit: pixel opaco do sprite 0 sobre pixel opaco do fundo (nunca em x = 255)
            if (_spriteZeroPossible && spriteZeroRendered && (_mask & 0x18) == 0x18 && x < 255)
                _status |= 0x40;
        }

        int colorIndex = PpuRead(0x3F00 + (palette << 2) + pixel) & 0x3F;
        Frame[_scanline * Width + x] = 0xFF000000u | SystemPalette[colorIndex];
    }

    // Paleta de 64 cores do NES (0xRRGGBB). Existem várias "versões"; você pode trocar por outra.
    private static readonly uint[] SystemPalette =
    {
        0x545454, 0x001E74, 0x081090, 0x300088, 0x440064, 0x5C0030, 0x540400, 0x3C1800,
        0x202A00, 0x083A00, 0x004000, 0x003C00, 0x00323C, 0x000000, 0x000000, 0x000000,
        0x989698, 0x084CC4, 0x3032EC, 0x5C1EE4, 0x8814B0, 0xA01464, 0x982220, 0x783C00,
        0x545A00, 0x287200, 0x087C00, 0x007628, 0x006678, 0x000000, 0x000000, 0x000000,
        0xECEEEC, 0x4C9AEC, 0x787CEC, 0xB062EC, 0xE454EC, 0xEC58B4, 0xEC6A64, 0xD48820,
        0xA0AA00, 0x74C400, 0x4CD020, 0x38CC6C, 0x38B4CC, 0x3C3C3C, 0x000000, 0x000000,
        0xECEEEC, 0xA8CCEC, 0xBCBCEC, 0xD4B2EC, 0xECAEEC, 0xECAED4, 0xECB4B0, 0xE4C490,
        0xCCD278, 0xB4DE78, 0xA8E290, 0x98E2B4, 0xA0D6E4, 0xA0A2A0, 0x000000, 0x000000
    };
}
