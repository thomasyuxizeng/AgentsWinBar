using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class CostCommandBuilderTests
{
    [TestMethod]
    [DataRow("C:\\Users\\A B\\.claude", "/mnt/c/Users/A B/.claude")]
    [DataRow("D:/Profile/.codex", "/mnt/d/Profile/.codex")]
    [DataRow("c:\\", "/mnt/c/")]
    public void ToWslPath_WhenLocalDrive_ConvertsDriveAndSlashes(string input, string expected) =>
        CostCommandBuilder.ToWslPath(input).Should().Be(expected);

    [TestMethod]
    [DataRow("C:\\Users\\a'b")]
    [DataRow("C:\\Users\\a\"b")]
    [DataRow("C:\\Users\\a\nb")]
    [DataRow("C:\\Users\\a\rb")]
    [DataRow("C:\\Users\\a\0b")]
    [DataRow("C:\\Users\\a\tb")]
    [DataRow("\\\\server\\share")]
    [DataRow("relative/path")]
    [DataRow("C:relative")]
    [DataRow("")]
    public void ToWslPath_WhenUnsafeOrNotAbsolute_Rejects(string input)
    {
        Action action = () => CostCommandBuilder.ToWslPath(input);
        action.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    [DataRow("claude", "CLAUDE_CONFIG_DIR", ".claude")]
    [DataRow("codex", "CODEX_HOME", ".codex")]
    public void Build_WhenWindowsFolderExists_QuotesEnvironmentForCostOnly(string provider, string variable, string folder)
    {
        var command = CostCommandBuilder.Build(provider, "C:\\Users\\A $name & (x)", path => path.EndsWith(folder));
        command.Should().Be($"env {variable}='/mnt/c/Users/A $name & (x)/{folder}' codexbar cost --provider {provider} --format json --refresh");
    }

    [TestMethod]
    public void Build_WhenFolderMissing_UsesWslHome() =>
        CostCommandBuilder.Build("claude", "C:\\Users\\A", _ => false).Should().Be("codexbar cost --provider claude --format json --refresh");

    [TestMethod]
    public void Build_WhenGemini_MapsToAntigravityWithoutEnvironment() =>
        CostCommandBuilder.Build(" GEMINI ", "irrelevant", _ => throw new Exception()).Should().Be("codexbar cost --provider antigravity --format json --refresh");

    [TestMethod]
    [DataRow("bad")]
    [DataRow("claude; echo secret")]
    public void Build_WhenProviderInvalid_Rejects(string provider)
    {
        Action action = () => CostCommandBuilder.Build(provider, "C:\\Users\\A", _ => true);
        action.Should().Throw<ArgumentException>();
    }
}
