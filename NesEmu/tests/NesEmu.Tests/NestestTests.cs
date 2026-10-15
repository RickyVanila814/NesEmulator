using System.Text.RegularExpressions;
using NesEmu.Core;

namespace NesEmu.Tests;

public class NestestTests
{
    /// <summary>
    /// Compara a CPU, instrução a instrução, com o log de referência do nestest
    /// (PC, A, X, Y, P, SP e ciclos). Para no primeiro opcode não oficial.
    /// </summary>
    [SkippableFact]
    public void Cpu_matches_nestest_log_for_official_opcodes()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestRoms");
        string romPath = Path.Combine(dir, "nestest.nes");
        string logPath = Path.Combine(dir, "nestest.log");
        Skip.IfNot(File.Exists(romPath) && File.Exists(logPath),
            "Coloque nestest.nes e nestest.log em tests/NesEmu.Tests/TestRoms/");

        var cpu = new Cpu6502(new Bus(Cartridge.Load(romPath)));
        cpu.Reset();
        cpu.PC = 0xC000;   // modo "automation" do nestest

        var rx = new Regex(@"^([0-9A-F]{4}).*?A:([0-9A-F]{2}) X:([0-9A-F]{2}) Y:([0-9A-F]{2}) P:([0-9A-F]{2}) SP:([0-9A-F]{2}).*?CYC:(\d+)");
        int matched = 0;
        int lineNo = 0;

        foreach (var line in File.ReadLines(logPath))
        {
            lineNo++;
            if (!cpu.NextOpcodeIsOfficial) break;

            var m = rx.Match(line);
            Assert.True(m.Success, $"Linha {lineNo} do log não reconhecida.");
            string expected = $"{m.Groups[1].Value}  A:{m.Groups[2].Value} X:{m.Groups[3].Value} " +
                              $"Y:{m.Groups[4].Value} P:{m.Groups[5].Value} SP:{m.Groups[6].Value} CYC:{m.Groups[7].Value}";
            string actual = cpu.TraceLine();
            Assert.True(expected == actual, $"Linha {lineNo}:\n  esperado: {expected}\n  obtido:   {actual}");

            cpu.Step();
            matched++;
        }

        Assert.True(matched > 1000, $"Só {matched} instruções comparadas.");
    }
}
