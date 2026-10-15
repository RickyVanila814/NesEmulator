namespace NesEmu.Core;

/// <summary>Botões do controle do NES. A ordem dos bits é a ordem em que o jogo os lê em $4016.</summary>
[Flags]
public enum NesButton : byte
{
    None = 0,
    A = 1,
    B = 2,
    Select = 4,
    Start = 8,
    Up = 16,
    Down = 32,
    Left = 64,
    Right = 128
}
