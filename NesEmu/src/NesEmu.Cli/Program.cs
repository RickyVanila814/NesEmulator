using NesEmu.Core;

if (args.Length == 0)
{
    Console.WriteLine("Uso: NesEmu.Cli <arquivo.nes> [quadros (padrão 60)] [saida.bmp (padrão frame.bmp)]");
    return 1;
}

var cart = Cartridge.Load(args[0]);
Console.WriteLine(
    $"Mapper: {cart.MapperId} | PRG: {cart.PrgRom.Length / 1024} KB | " +
    $"CHR: {cart.ChrMem.Length / 1024} KB{(cart.ChrIsRam ? " (RAM)" : "")} | " +
    $"Espelhamento: {cart.Mirroring} | Bateria: {cart.HasBattery}");

Nes nes;
try { nes = new Nes(cart); }
catch (NotSupportedException e)
{
    Console.WriteLine(e.Message);
    return 2;
}

int frames = args.Length > 1 ? int.Parse(args[1]) : 60;
string output = args.Length > 2 ? args[2] : "frame.bmp";

try
{
    for (int i = 0; i < frames; i++) nes.RunFrame();
}
catch (NotSupportedException e)
{
    Console.WriteLine(e.Message + " -> salvando o último quadro mesmo assim.");
}

BmpWriter.Save(output, nes.Ppu.Frame, Ppu.Width, Ppu.Height, 3);
Console.WriteLine($"Quadro salvo em: {Path.GetFullPath(output)}");
return 0;
