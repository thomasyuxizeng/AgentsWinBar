using System.ComponentModel;
using System.Diagnostics;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;

namespace AgentsWinBar.Tests.Unit.Services.Native;

[TestClass]
public class RenewalProcessTests
{
    [TestMethod]
    public void KillTree_WhenTreeCannotBeFullyStopped_DoesNotThrow()
    {
        using var process = new Process();

        var kill = () => RenewalProcess.KillTree(process, _ => throw new AggregateException(new Win32Exception(5)));

        kill.Should().NotThrow();
    }

    [TestMethod]
    public void KillTree_WhenRegisteredOnShutdownToken_LetsCancelReturnEvenIfTheTreeCannotBeStopped()
    {
        using var shutdown = new CancellationTokenSource();
        using var process = new Process();
        using var registration = shutdown.Token.Register(
            () => RenewalProcess.KillTree(process, _ => throw new AggregateException(new Win32Exception(5))));

        shutdown.Invoking(s => s.Cancel()).Should().NotThrow();
        shutdown.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public void KillTree_WhenProcessNeverStarted_DoesNotThrow()
    {
        using var process = new Process();

        var kill = () => RenewalProcess.KillTree(process);

        kill.Should().NotThrow();
    }
}
