using System.Diagnostics;
using AwesomeAssertions;

namespace CodexBarWin.Tests.Unit.Tools;

[TestClass]
public class ReleaseSecurityTests
{
    [TestMethod]
    [DataRow("Signer")]
    [DataRow("Elevation")]
    [DataRow("Licenses")]
    [DataRow("Workflow")]
    public async Task ValidatePolicy_WhenSecurityRegressionOccurs_RejectsUnsafeInput(string group)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "tools", "test-release-security.ps1")))
            root = root.Parent;
        root.Should().NotBeNull();
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(root!.FullName, "tools", "test-release-security.ps1"), "-Group", group })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        process.ExitCode.Should().Be(0, await error);
        (await output).Should().Contain($"{group} release security tests passed.");
    }
}
