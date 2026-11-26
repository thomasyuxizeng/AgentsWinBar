using System.Diagnostics;
using System.Text;
using AwesomeAssertions;

namespace AgentsWinBar.Tests.Unit.Tools;

[TestClass]
public class CostLedgerReportTests
{
    [TestMethod]
    [DataRow("packaged", "C:\\LedgerTest\\Packages\\AgentsWinBar_test\\LocalCache\\Local\\AgentsWinBar\\cost-ledger.json")]
    [DataRow("unpackaged", "C:\\LedgerTest\\AgentsWinBar\\cost-ledger.json")]
    [DataRow("unavailable", "C:\\LedgerTest\\AgentsWinBar\\cost-ledger.json")]
    [DataRow("override", "C:\\Explicit\\cost-ledger.json")]
    public async Task ResolveCostLedgerPath_PackageAndOverrideAvailability_SelectsExpectedPath(string scenario, string expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "tools", "cost-ledger-report.ps1"))) root = root.Parent;
        root.Should().NotBeNull();
        var scriptPath = Path.Combine(root!.FullName, "tools", "cost-ledger-report.ps1").Replace("'", "''");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{scriptPath}}', [ref]$null, [ref]$null)
            $helper = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Resolve-CostLedgerPath' }, $true)
            if (-not $helper) { throw 'Path resolver is missing.' }
            . ([scriptblock]::Create($helper.Extent.Text))
            function Get-AppxPackage {
                param($Name)
                if ('{{scenario}}' -in @('unavailable', 'override')) { throw 'Package lookup unavailable' }
                [pscustomobject]@{ Version = [version]'1.0'; PackageFamilyName = 'AgentsWinBar_old' }
                [pscustomobject]@{ Version = [version]'2.0'; PackageFamilyName = 'AgentsWinBar_test' }
            }
            function Test-Path {
                param($LiteralPath, $PathType)
                '{{scenario}}' -eq 'packaged' -and $LiteralPath -like '*AgentsWinBar_test*'
            }
            $override = if ('{{scenario}}' -eq 'override') { 'C:\Explicit\cost-ledger.json' } else { '' }
            Resolve-CostLedgerPath -OverridePath $override -LocalAppData 'C:\LedgerTest'
            """;
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        process.ExitCode.Should().Be(0, await error);
        (await output).Trim().Should().Be(expected);
    }
}
