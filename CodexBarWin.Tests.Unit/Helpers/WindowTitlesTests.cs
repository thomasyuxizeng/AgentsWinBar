using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class WindowTitlesTests
{
    [TestMethod]
    [DataRow("Fable only", "Fable")]
    [DataRow("  Fable only  ", "Fable")]
    [DataRow("Claude and GPT 5-hour", "Claude+GPT 5h")]
    [DataRow("Claude and GPT weekly", "Claude+GPT 7d")]
    [DataRow("Claude and GPT", "Claude+GPT")]
    [DataRow("Opus only", "Opus")]
    [DataRow("Fable", "Fable")]
    [DataRow("Some other window", "Some other window")]
    public void Short_KnownTitles_AreShortened(string title, string expected) =>
        WindowTitles.Short(title).Should().Be(expected);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Short_NullOrBlank_ReturnsEmpty(string? title) =>
        WindowTitles.Short(title).Should().BeEmpty();

    [TestMethod]
    [DataRow("Fable")]
    [DataRow("Claude+GPT 5h")]
    [DataRow("Claude+GPT 7d")]
    public void Short_AlreadyShort_IsUnchanged(string title) =>
        WindowTitles.Short(title).Should().Be(title);

    [TestMethod]
    [DataRow("Fable only", WindowAccent.Claude)]
    [DataRow("Claude and GPT weekly", WindowAccent.ClaudeAndOpenAi)]
    public void Short_KeepsTheProviderAccent(string title, WindowAccent expected) =>
        WindowAccents.FromTitle(WindowTitles.Short(title)).Should().Be(expected);
}
