using AwesomeAssertions;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class SignInLauncherTests
{
    private const string LocalAppData = @"C:\Users\Test\AppData\Local";
    private const string ToolFolder = @"C:\Program Files\Synthetic npm";
    private const string System32 = @"C:\Windows\System32";
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Wt = @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps\wt.exe";
    private const string AntigravityApp = @"C:\Users\Test\AppData\Local\Programs\antigravity\Antigravity.exe";

    /// <summary>Records starts instead of running anything; a start throws for file names in <see cref="Failing"/>.</summary>
    private sealed class FakeSystem : ISignInSystem
    {
        public HashSet<string> OnPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SignInProcess> Started { get; } = [];
        public List<SignInProcess> Attempts { get; } = [];
        public string LocalAppData => SignInLauncherTests.LocalAppData;
        public string SystemDirectory => System32;

        public string? FindOnPath(string command) => command switch
        {
            "wt" when OnPath.Contains("wt") => Wt,
            _ when OnPath.Contains(command) => $@"{ToolFolder}\{command}.cmd",
            _ => null
        };

        public bool FileExists(string path) => Files.Contains(path);

        public void Start(SignInProcess process)
        {
            Attempts.Add(process);
            if (Failing.Contains(process.FileName)) throw new System.ComponentModel.Win32Exception(2);
            Started.Add(process);
        }
    }

    private readonly FakeSystem _system = new();
    private SignInLauncher Launcher() => new(_system, NullLogger<SignInLauncher>.Instance);

    [TestMethod]
    public void FindOnPath_WhenRelativeEntriesComeFirst_ReturnsFullyQualifiedShim()
    {
        var candidates = new List<string>();
        var result = WindowsSignInSystem.FindOnPath("provider", $"tools;C:relative;\\rooted;\"{ToolFolder}\"", ".CMD",
            candidate => { candidates.Add(candidate); return true; });

        result.Should().Be($@"{ToolFolder}\provider.CMD");
        candidates.Should().ContainSingle().Which.Should().Be(result);
    }

    [TestMethod]
    [DataRow("claude", "auth login")]
    [DataRow("codex", "login")]
    public void Launch_WhenToolOnPathAndWindowsTerminal_OpensLoginInWindowsTerminal(string provider, string arguments)
    {
        _system.OnPath.UnionWith([provider, "wt"]);

        var result = Launcher().Launch(provider);

        result.Route.Should().Be(SignInRoute.WindowsTerminal);
        result.Started.Should().BeTrue();
        var started = _system.Started.Single();
        started.FileName.Should().Be(Wt);
        started.Arguments.Should().Contain($"--startingDirectory \"{System32}\"")
            .And.Contain($"\"{Cmd}\" /d /v:off /k ")
            .And.EndWith($"& \"{ToolFolder}\\{provider}.cmd\" {arguments}");
        started.WorkingDirectory.Should().Be(System32);
    }

    [TestMethod]
    [DataRow("claude", "auth login")]
    [DataRow("CODEX", "login")]
    public void Launch_WhenToolOnPathWithoutWindowsTerminal_OpensLoginInCommandPrompt(string provider, string arguments)
    {
        _system.OnPath.Add(provider.ToLowerInvariant());

        var result = Launcher().Launch(provider);

        result.Route.Should().Be(SignInRoute.CommandPrompt);
        var started = _system.Started.Single();
        started.FileName.Should().Be(Cmd);
        started.Arguments.Should().StartWith("/d /v:off /k ")
            .And.EndWith($"& \"{ToolFolder}\\{provider.ToLowerInvariant()}.cmd\" {arguments}");
        started.WorkingDirectory.Should().Be(System32);
    }

    [TestMethod]
    [DataRow("claude", "https://code.claude.com/docs/en/setup")]
    [DataRow("codex", "https://developers.openai.com/codex/cli/")]
    public void Launch_WhenToolNotOnPath_OpensSetupPageInBrowser(string provider, string url)
    {
        _system.OnPath.Add("wt");   // a terminal alone is not enough: the tool itself is missing

        var result = Launcher().Launch(provider);

        result.Route.Should().Be(SignInRoute.Browser);
        _system.Started.Single().Should().Be(new SignInProcess(url, string.Empty));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Launch_GeminiWhenAppInstalled_OpensAntigravityApp(bool agyOnPath)
    {
        _system.Files.Add(AntigravityApp);
        _system.OnPath.Add("wt");
        if (agyOnPath) _system.OnPath.Add("agy");

        var result = Launcher().Launch("gemini");

        result.Route.Should().Be(SignInRoute.DesktopApp);
        _system.Started.Single().Should().Be(new SignInProcess(AntigravityApp, string.Empty));
    }

    [TestMethod]
    public void Launch_GeminiWhenOnlyAgyOnPath_OpensAgyInTerminalWithSignInLine()
    {
        _system.OnPath.UnionWith(["agy", "wt"]);

        var result = Launcher().Launch("gemini");

        result.Route.Should().Be(SignInRoute.WindowsTerminal);
        var arguments = _system.Started.Single().Arguments;
        arguments.Should().Contain("echo Sign in with Google").And.EndWith($"& \"{ToolFolder}\\agy.cmd\"");
    }

    [TestMethod]
    public void Launch_GeminiWhenOnlyAgyWithoutWindowsTerminal_OpensAgyInCommandPrompt()
    {
        _system.OnPath.Add("agy");

        var result = Launcher().Launch("gemini");

        result.Route.Should().Be(SignInRoute.CommandPrompt);
        _system.Started.Single().Arguments.Should().EndWith($"& \"{ToolFolder}\\agy.cmd\"").And.Contain("echo Sign in with Google");
    }

    [TestMethod]
    public void Launch_GeminiWhenNeitherAppNorAgy_OpensAntigravityPageInBrowser()
    {
        _system.OnPath.Add("wt");

        var result = Launcher().Launch("gemini");

        result.Route.Should().Be(SignInRoute.Browser);
        _system.Started.Single().Should().Be(new SignInProcess("https://antigravity.google", string.Empty));
    }

    [TestMethod]
    public void Launch_WhenWindowsTerminalFailsToStart_FallsBackToCommandPrompt()
    {
        _system.OnPath.UnionWith(["claude", "wt"]);
        _system.Failing.Add(Wt);

        var result = Launcher().Launch("claude");

        result.Route.Should().Be(SignInRoute.CommandPrompt);
        _system.Attempts.Select(a => a.FileName).Should().Equal(Wt, Cmd);
    }

    [TestMethod]
    public void Launch_WhenGeminiAppFailsToStart_FallsBackToAgyThenBrowser()
    {
        _system.Files.Add(AntigravityApp);
        _system.OnPath.Add("agy");
        _system.Failing.UnionWith([AntigravityApp, Cmd]);

        var result = Launcher().Launch("gemini");

        result.Route.Should().Be(SignInRoute.Browser);
        _system.Attempts.Select(a => a.FileName).Should().Equal(AntigravityApp, Cmd, "https://antigravity.google");
    }

    [TestMethod]
    public void Launch_WhenEveryRouteFails_ReturnsNone()
    {
        _system.Failing.Add("https://code.claude.com/docs/en/setup");

        var result = Launcher().Launch("claude");

        result.Route.Should().Be(SignInRoute.None);
        result.Started.Should().BeFalse();
        _system.Started.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("openai")]
    [DataRow("claude & calc")]
    [DataRow("")]
    public void Launch_WhenInvalidProvider_ThrowsAndStartsNothing(string provider)
    {
        _system.OnPath.UnionWith(["claude", "wt"]);

        var act = () => Launcher().Launch(provider);

        act.Should().Throw<ArgumentException>();
        _system.Attempts.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("gemini")]
    public void Launch_AnyRoute_UsesOnlyFixedCommandLines(string provider)
    {
        _system.OnPath.UnionWith(["claude", "codex", "agy", "wt"]);

        Launcher().Launch(provider);

        var arguments = _system.Started.Single().Arguments;
        arguments.Should().NotContain(";", "Windows Terminal would split the command at a semicolon");
        arguments.Should().NotContainAny("|", ">", "<", "%");
    }
}
