using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class WindowAccentTests
{
    [TestMethod]
    [DataRow(null, WindowAccent.None)]
    [DataRow("", WindowAccent.None)]
    [DataRow("   ", WindowAccent.None)]
    [DataRow("Fable only", WindowAccent.Claude)]
    [DataRow("Current week (Sonnet)", WindowAccent.Claude)]
    [DataRow("Opus weekly", WindowAccent.Claude)]
    [DataRow("Claude only", WindowAccent.Claude)]
    [DataRow("GPT-5 weekly", WindowAccent.OpenAi)]
    [DataRow("Codex spark", WindowAccent.OpenAi)]
    [DataRow("Claude and GPT 5-hour", WindowAccent.ClaudeAndOpenAi)]
    [DataRow("Claude/GPT weekly", WindowAccent.ClaudeAndOpenAi)]
    [DataRow("Additional", WindowAccent.Other)]
    [DataRow("Gemini Pro", WindowAccent.Other)]
    public void FromTitle_ClassifiesByTheModelsNamed(string? title, WindowAccent expected)
    {
        WindowAccents.FromTitle(title).Should().Be(expected);
    }

    [TestMethod]
    public void FromTitle_IsCaseInsensitive()
    {
        WindowAccents.FromTitle("FABLE ONLY").Should().Be(WindowAccent.Claude);
        WindowAccents.FromTitle("claude AND gpt weekly").Should().Be(WindowAccent.ClaudeAndOpenAi);
    }
}
