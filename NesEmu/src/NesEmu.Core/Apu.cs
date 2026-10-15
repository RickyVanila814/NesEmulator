namespace NesEmu.Core;

/// <summary>
/// APU 2A03 (som do NES): 2 canais de onda quadrada (pulse), 1 triangular, 1 de ruído e o DMC
/// (amostras de 1 bit). É clockada 1 vez por ciclo de CPU e gera amostras PCM de 16 bits, mono.
/// O "frame counter" (quadro de 4 ou 5 passos) clocka os envelopes e os contadores de duração.
/// </summary>
public sealed class Apu
{
    public const int SampleRate = 44100;
    private const double CpuHz = 1789773.0;

    /// <summary>Quantos ciclos de CPU geram 1 amostra de saída. O frontend pode ajustar para casar com o vídeo.</summary>
    public double CyclesPerSample { get; set; } = CpuHz / SampleRate;

    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30
    };

    private static readonly byte[][] DutyTable =
    {
        new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 },   // 12,5%
        new byte[] { 0, 1, 1, 0, 0, 0, 0, 0 },   // 25%
        new byte[] { 0, 1, 1, 1, 1, 0, 0, 0 },   // 50%
        new byte[] { 1, 0, 0, 1, 1, 1, 1, 1 }    // 75% (25% invertido)
    };

    private static readonly byte[] TriangleSequence =
    {
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
    };

    private static readonly int[] NoisePeriods =
        { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };

    private static readonly int[] DmcRates =
        { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54 };

    // Mixagem não linear aproximada por tabelas (como no circuito analógico do NES)
    private static readonly float[] PulseTable = BuildTable(31, 95.52, 8128.0);
    private static readonly float[] TndTable = BuildTable(203, 163.67, 24329.0);

    private static float[] BuildTable(int size, double a, double b)
    {
        var table = new float[size];
        for (int i = 1; i < size; i++) table[i] = (float)(a / (b / i + 100.0));
        return table;
    }

    private readonly Pulse _pulse1 = new(true);
    private readonly Pulse _pulse2 = new(false);
    private readonly Triangle _triangle = new();
    private readonly Noise _noise = new();
    private readonly Dmc _dmc;

    private bool _oddCycle;
    private int _frameCycle;
    private bool _fiveStep, _irqInhibit, _frameIrq;

    // Geração de amostras: média de todas as saídas entre duas amostras (filtro simples contra aliasing)
    private double _sampleTimer, _mixSum;
    private int _mixCount;
    private float _hpPrevIn, _hpPrevOut;

    private readonly short[] _ring = new short[16384];
    private int _ringRead, _ringWrite, _ringCount;

    public Apu(Func<ushort, byte> readMemory) => _dmc = new Dmc(readMemory);

    /// <summary>True quando o APU está pedindo um IRQ (frame counter ou fim de amostra do DMC).</summary>
    public bool IrqActive => _frameIrq || _dmc.IrqFlag;

    // ------------------------------------------------------------------
    // Registradores $4000-$4017
    // ------------------------------------------------------------------

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4000 and <= 0x4003: _pulse1.WriteReg(address & 3, value); break;
            case >= 0x4004 and <= 0x4007: _pulse2.WriteReg(address & 3, value); break;
            case >= 0x4008 and <= 0x400B: _triangle.WriteReg(address & 3, value); break;
            case >= 0x400C and <= 0x400F: _noise.WriteReg(address & 3, value); break;
            case >= 0x4010 and <= 0x4013: _dmc.WriteReg(address & 3, value); break;
            case 0x4015:
                _pulse1.Len.SetEnabled((value & 0x01) != 0);
                _pulse2.Len.SetEnabled((value & 0x02) != 0);
                _triangle.Len.SetEnabled((value & 0x04) != 0);
                _noise.Len.SetEnabled((value & 0x08) != 0);
                _dmc.SetEnabled((value & 0x10) != 0);
                break;
            case 0x4017:
                _fiveStep = (value & 0x80) != 0;
                _irqInhibit = (value & 0x40) != 0;
                if (_irqInhibit) _frameIrq = false;
                _frameCycle = 0;
                if (_fiveStep) { ClockQuarterFrame(); ClockHalfFrame(); }
                break;
        }
    }

    /// <summary>$4015: bits 0-3 = canais com duração restante, 4 = DMC tocando, 6 = IRQ do frame, 7 = IRQ do DMC.</summary>
    public byte ReadStatus()
    {
        int s = 0;
        if (_pulse1.Len.Value > 0) s |= 0x01;
        if (_pulse2.Len.Value > 0) s |= 0x02;
        if (_triangle.Len.Value > 0) s |= 0x04;
        if (_noise.Len.Value > 0) s |= 0x08;
        if (_dmc.Active) s |= 0x10;
        if (_frameIrq) s |= 0x40;
        if (_dmc.IrqFlag) s |= 0x80;
        _frameIrq = false;                // ler o status limpa o IRQ do frame
        return (byte)s;
    }

    // ------------------------------------------------------------------
    // Relógio: 1 chamada por ciclo de CPU
    // ------------------------------------------------------------------

    public void Clock()
    {
        _triangle.ClockTimer();           // triângulo, ruído e DMC contam em ciclos de CPU
        _noise.ClockTimer();
        _dmc.ClockTimer();
        if (_oddCycle)                    // os pulsos contam a cada 2 ciclos de CPU
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
        }
        _oddCycle = !_oddCycle;

        ClockFrameCounter();

        int pulse = _pulse1.Output + _pulse2.Output;
        int tnd = 3 * _triangle.Output + 2 * _noise.Output + _dmc.Output;
        _mixSum += PulseTable[pulse] + TndTable[tnd];
        _mixCount++;

        _sampleTimer += 1.0;
        if (_sampleTimer >= CyclesPerSample)
        {
            _sampleTimer -= CyclesPerSample;
            PushSample((float)(_mixSum / _mixCount));
            _mixSum = 0;
            _mixCount = 0;
        }
    }

    private void ClockFrameCounter()
    {
        _frameCycle++;
        if (_frameCycle == 7457) ClockQuarterFrame();
        else if (_frameCycle == 14913) { ClockQuarterFrame(); ClockHalfFrame(); }
        else if (_frameCycle == 22371) ClockQuarterFrame();
        else if (!_fiveStep && _frameCycle == 29829)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
            if (!_irqInhibit) _frameIrq = true;
            _frameCycle = 0;
        }
        else if (_fiveStep && _frameCycle == 37281)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
            _frameCycle = 0;
        }
    }

    private void ClockQuarterFrame()      // envelopes e contador linear do triângulo
    {
        _pulse1.Env.Clock();
        _pulse2.Env.Clock();
        _noise.Env.Clock();
        _triangle.ClockLinear();
    }

    private void ClockHalfFrame()         // contadores de duração e sweeps
    {
        _pulse1.Len.Clock();
        _pulse2.Len.Clock();
        _triangle.Len.Clock();
        _noise.Len.Clock();
        _pulse1.ClockSweep();
        _pulse2.ClockSweep();
    }

    // ------------------------------------------------------------------
    // Saída de áudio
    // ------------------------------------------------------------------

    private void PushSample(float x)
    {
        // Filtro passa-altas (~28 Hz) para tirar o nível DC da mixagem (que só vai de 0 a 1)
        float y = x - _hpPrevIn + 0.996f * _hpPrevOut;
        _hpPrevIn = x;
        _hpPrevOut = y;

        if (_ringCount == _ring.Length) return;   // ninguém está consumindo (CLI/testes): descarta
        _ring[_ringWrite] = (short)Math.Clamp(y * 30000f, -32768f, 32767f);
        _ringWrite = (_ringWrite + 1) % _ring.Length;
        _ringCount++;
    }

    /// <summary>Copia as amostras geradas até agora (PCM 16 bits mono, 44100 Hz) e devolve quantas foram.</summary>
    public int ReadSamples(short[] destination)
    {
        int n = Math.Min(destination.Length, _ringCount);
        for (int i = 0; i < n; i++)
        {
            destination[i] = _ring[_ringRead];
            _ringRead = (_ringRead + 1) % _ring.Length;
        }
        _ringCount -= n;
        return n;
    }

    // ------------------------------------------------------------------
    // Partes compartilhadas
    // ------------------------------------------------------------------

    private sealed class Envelope
    {
        public bool Start, Loop, Constant;
        public int Period, Divider, Decay;

        public int Volume => Constant ? Period : Decay;

        public void Clock()
        {
            if (Start)
            {
                Start = false;
                Decay = 15;
                Divider = Period;
            }
            else if (Divider > 0) Divider--;
            else
            {
                Divider = Period;
                if (Decay > 0) Decay--;
                else if (Loop) Decay = 15;
            }
        }
    }

    private sealed class LengthCounter
    {
        public int Value;
        public bool Halt, Enabled;

        public void Load(int index) { if (Enabled) Value = LengthTable[index]; }
        public void SetEnabled(bool enabled) { Enabled = enabled; if (!enabled) Value = 0; }
        public void Clock() { if (!Halt && Value > 0) Value--; }
    }

    // ------------------------------------------------------------------
    // Canais
    // ------------------------------------------------------------------

    private sealed class Pulse
    {
        private readonly bool _isPulse1;
        public readonly Envelope Env = new();
        public readonly LengthCounter Len = new();

        private int _duty, _timerPeriod, _timer, _seqPos;
        private bool _sweepEnabled, _sweepNegate, _sweepReload;
        private int _sweepPeriod, _sweepShift, _sweepDivider;

        public Pulse(bool isPulse1) => _isPulse1 = isPulse1;

        public void WriteReg(int reg, byte v)
        {
            switch (reg)
            {
                case 0:
                    _duty = v >> 6;
                    Len.Halt = (v & 0x20) != 0;
                    Env.Loop = Len.Halt;
                    Env.Constant = (v & 0x10) != 0;
                    Env.Period = v & 0x0F;
                    break;
                case 1:
                    _sweepEnabled = (v & 0x80) != 0;
                    _sweepPeriod = (v >> 4) & 7;
                    _sweepNegate = (v & 0x08) != 0;
                    _sweepShift = v & 7;
                    _sweepReload = true;
                    break;
                case 2:
                    _timerPeriod = (_timerPeriod & 0x700) | v;
                    break;
                case 3:
                    _timerPeriod = (_timerPeriod & 0x0FF) | ((v & 7) << 8);
                    Len.Load(v >> 3);
                    _seqPos = 0;
                    Env.Start = true;
                    break;
            }
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _timerPeriod;
                _seqPos = (_seqPos + 1) & 7;
            }
            else _timer--;
        }

        // O pulso 1 usa complemento de 1 no sweep negativo; o pulso 2, complemento de 2
        private int TargetPeriod()
        {
            int change = _timerPeriod >> _sweepShift;
            if (_sweepNegate) return _timerPeriod - change - (_isPulse1 ? 1 : 0);
            return _timerPeriod + change;
        }

        private bool Muted => _timerPeriod < 8 || TargetPeriod() > 0x7FF;

        public void ClockSweep()
        {
            if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && !Muted)
                _timerPeriod = Math.Max(0, TargetPeriod());

            if (_sweepDivider == 0 || _sweepReload)
            {
                _sweepDivider = _sweepPeriod;
                _sweepReload = false;
            }
            else _sweepDivider--;
        }

        public int Output =>
            (Len.Value == 0 || Muted || DutyTable[_duty][_seqPos] == 0) ? 0 : Env.Volume;
    }

    private sealed class Triangle
    {
        public readonly LengthCounter Len = new();
        private int _timerPeriod, _timer, _seqPos, _linearReload, _linearCounter;
        private bool _reloadFlag, _control;

        public void WriteReg(int reg, byte v)
        {
            switch (reg)
            {
                case 0:
                    _control = (v & 0x80) != 0;
                    Len.Halt = _control;
                    _linearReload = v & 0x7F;
                    break;
                case 2:
                    _timerPeriod = (_timerPeriod & 0x700) | v;
                    break;
                case 3:
                    _timerPeriod = (_timerPeriod & 0x0FF) | ((v & 7) << 8);
                    Len.Load(v >> 3);
                    _reloadFlag = true;
                    break;
            }
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _timerPeriod;
                if (Len.Value > 0 && _linearCounter > 0) _seqPos = (_seqPos + 1) & 31;
            }
            else _timer--;
        }

        public void ClockLinear()
        {
            if (_reloadFlag) _linearCounter = _linearReload;
            else if (_linearCounter > 0) _linearCounter--;
            if (!_control) _reloadFlag = false;
        }

        public int Output => TriangleSequence[_seqPos];
    }

    private sealed class Noise
    {
        public readonly Envelope Env = new();
        public readonly LengthCounter Len = new();
        private int _timerPeriod = NoisePeriods[0], _timer;
        private ushort _shift = 1;     // registrador de deslocamento de 15 bits (LFSR)
        private bool _mode;

        public void WriteReg(int reg, byte v)
        {
            switch (reg)
            {
                case 0:
                    Len.Halt = (v & 0x20) != 0;
                    Env.Loop = Len.Halt;
                    Env.Constant = (v & 0x10) != 0;
                    Env.Period = v & 0x0F;
                    break;
                case 2:
                    _mode = (v & 0x80) != 0;
                    _timerPeriod = NoisePeriods[v & 0x0F];
                    break;
                case 3:
                    Len.Load(v >> 3);
                    Env.Start = true;
                    break;
            }
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _timerPeriod - 1;
                int feedback = (_shift & 1) ^ ((_shift >> (_mode ? 6 : 1)) & 1);
                _shift = (ushort)((_shift >> 1) | (feedback << 14));
            }
            else _timer--;
        }

        public int Output => (Len.Value == 0 || (_shift & 1) != 0) ? 0 : Env.Volume;
    }

    private sealed class Dmc
    {
        private readonly Func<ushort, byte> _read;
        public bool IrqFlag;

        private bool _irqEnabled, _loop;
        private int _rate = DmcRates[0], _timer;
        private int _output;
        private ushort _sampleAddress, _currentAddress;
        private int _sampleLength, _bytesRemaining;
        private byte _buffer, _shift;
        private bool _bufferEmpty = true, _silence = true;
        private int _bitsRemaining = 8;

        public Dmc(Func<ushort, byte> read) => _read = read;

        public int Output => _output;
        public bool Active => _bytesRemaining > 0;

        public void WriteReg(int reg, byte v)
        {
            switch (reg)
            {
                case 0:
                    _irqEnabled = (v & 0x80) != 0;
                    if (!_irqEnabled) IrqFlag = false;
                    _loop = (v & 0x40) != 0;
                    _rate = DmcRates[v & 0x0F];
                    break;
                case 1: _output = v & 0x7F; break;
                case 2: _sampleAddress = (ushort)(0xC000 | (v << 6)); break;
                case 3: _sampleLength = (v << 4) | 1; break;
            }
        }

        public void SetEnabled(bool enabled)
        {
            IrqFlag = false;
            if (!enabled) _bytesRemaining = 0;
            else if (_bytesRemaining == 0)
            {
                _currentAddress = _sampleAddress;
                _bytesRemaining = _sampleLength;
            }
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _rate - 1;
                if (!_silence)
                {
                    if ((_shift & 1) != 0) { if (_output <= 125) _output += 2; }
                    else if (_output >= 2) _output -= 2;
                }
                _shift >>= 1;
                if (--_bitsRemaining == 0)
                {
                    _bitsRemaining = 8;
                    if (_bufferEmpty) _silence = true;
                    else
                    {
                        _silence = false;
                        _shift = _buffer;
                        _bufferEmpty = true;
                    }
                }
            }
            else _timer--;

            // Leitor de memória: busca o próximo byte da amostra quando o buffer esvazia
            if (_bufferEmpty && _bytesRemaining > 0)
            {
                _buffer = _read(_currentAddress);
                _bufferEmpty = false;
                _currentAddress = (ushort)(_currentAddress == 0xFFFF ? 0x8000 : _currentAddress + 1);
                if (--_bytesRemaining == 0)
                {
                    if (_loop)
                    {
                        _currentAddress = _sampleAddress;
                        _bytesRemaining = _sampleLength;
                    }
                    else if (_irqEnabled) IrqFlag = true;
                }
            }
        }
    }
}
