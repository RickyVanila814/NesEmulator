using NesEmu.Core;

if (args.Length == 0)
{
    Console.WriteLine("Uso: NesEmu.Game <arquivo.nes>");
    return 1;
}

Nes nes;
try
{
    nes = new Nes(Cartridge.Load(args[0]));
}
catch (Exception e) when (e is NotSupportedException or InvalidDataException or IOException)
{
    Console.WriteLine(e.Message);
    return 2;
}

using var game = new NesGame(nes, Path.GetFileNameWithoutExtension(args[0]));
game.Run();
return 0;
