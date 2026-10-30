using Joust.Assembler;

namespace Joust.Tests;

public class AssemblerTests
{
    private static byte[] Assemble(string source, int origin = 0x1000)
    {
        string dir = Path.Combine(Path.GetTempPath(), "joustx-asm-tests");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid() + ".asm");
        File.WriteAllText(file, source);
        try
        {
            var result = new Assembler.Assembler().AssembleFile(file);
            Assert.True(result.Success, string.Join("\n", result.Errors));
            int end = origin;
            while (end < 0x10000 && result.Written[end]) end++;
            return result.Slice(origin, end - origin);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(" LDA ,X", new byte[] { 0xA6, 0x84 })]
    [InlineData(" LDA 0,X", new byte[] { 0xA6, 0x84 })]
    [InlineData(" LDA 5,U", new byte[] { 0xA6, 0x45 })]
    [InlineData(" LDA -16,Y", new byte[] { 0xA6, 0x30 })]
    [InlineData(" LDA 100,X", new byte[] { 0xA6, 0x88, 0x64 })]
    [InlineData(" LDA 1000,X", new byte[] { 0xA6, 0x89, 0x03, 0xE8 })]
    [InlineData(" STD ,X++", new byte[] { 0xED, 0x81 })]
    [InlineData(" LDB ,-S", new byte[] { 0xE6, 0xE2 })]
    [InlineData(" LDX [2,U]", new byte[] { 0xAE, 0xD8, 0x02 })]
    [InlineData(" JMP [$A010]", new byte[] { 0x6E, 0x9F, 0xA0, 0x10 })]
    [InlineData(" LEAX D,X", new byte[] { 0x30, 0x8B })]
    [InlineData(" PSHS D,X,U,PC", new byte[] { 0x34, 0xD6 })]
    [InlineData(" PULU CC,D,DP,X,Y", new byte[] { 0x37, 0x3F })]
    [InlineData(" TFR A,DP", new byte[] { 0x1F, 0x8B })]
    [InlineData(" LDY #$1234", new byte[] { 0x10, 0x8E, 0x12, 0x34 })]
    [InlineData(" CMPU #1", new byte[] { 0x11, 0x83, 0x00, 0x01 })]
    [InlineData(" ANDA #~$03", new byte[] { 0x84, 0xFC })]
    // AS precedence: & binds tighter than +, and >> tighter than ! (xor).
    [InlineData(" FCB 2+3&1,$11!$0404>>8", new byte[] { 0x03, 0x15 })]
    public void EncodesInstructions(string line, byte[] expected)
    {
        Assert.Equal(expected, Assemble($" ORG $1000\n{line}\n"));
    }

    [Fact]
    public void UsesDirectPageForAssumedPage()
    {
        var code = Assemble(" ASSUME DPR:$A0\nVAR EQU $A012\n ORG $1000\n LDA VAR\n LDA $B000\n");
        Assert.Equal(new byte[] { 0x96, 0x12, 0xB6, 0xB0, 0x00 }, code);
    }

    [Fact]
    public void ResolvesLocalLabelsPerScope()
    {
        var code = Assemble(" ORG $1000\nONE BRA .1S\n.1S NOP\nTWO BRA .1S\n NOP\n.1S RTS\n");
        Assert.Equal(new byte[] { 0x20, 0x00, 0x12, 0x20, 0x01, 0x12, 0x39 }, code);
    }

    [Fact]
    public void StarInDataListIsStartOfStatement()
    {
        // Like Joust's STATE macro: every item measures from the start of the FCB line.
        var code = Assemble(" ORG $1000\n FCB 0,*-$1000,*-$1000\n");
        Assert.Equal(new byte[] { 0, 0, 0 }, code);
    }

    [Fact]
    public void ShrinksForwardReferencesAcrossPasses()
    {
        var code = Assemble(" ASSUME DPR:$A0\n ORG $1000\n LDA LATER\n RTS\nLATER EQU $A055\n");
        Assert.Equal(new byte[] { 0x96, 0x55, 0x39 }, code);
    }

    [Fact]
    public void JoustSourceBuildsAndMatchesOriginalRomChecksums()
    {
        string? dir = JoustRomBuild.FindSourceDirectory(AppContext.BaseDirectory);
        Assert.NotNull(dir);
        var result = JoustRomBuild.Assemble(dir!);
        Assert.True(result.Success, string.Join("\n", result.Errors.Take(10)));
        var sums = JoustRomBuild.VerifyChecksums(result);
        Assert.Equal(12, sums.Count);
        Assert.All(sums, s => Assert.True(s.Ok, $"ROM ${s.Base:X4}: sum ${s.Sum:X2} expected ${s.Expected:X2}"));
    }
}
