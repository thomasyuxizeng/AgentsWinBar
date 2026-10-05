using System.Globalization;
using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class WslBatchProtocolTests
{
    private static string Frame(string marker, int index, int exit, string output, string error) =>
        $"{marker}|{index}|{exit}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(output))}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(error))}";

    [TestMethod]
    public async Task ReadResultsAsync_FragmentedFramesWithoutFinalNewline_PreservesPayloadsAndInputOrder()
    {
        var marker = WslBatchProtocol.CreateMarker();
        var spoof = $"\n{marker}|0|0||\nquotes ' dollar $ backtick ` Unicode 한글";
        var text = "shell startup noise\n" + Frame(marker, 1, 3, spoof, "stderr\nno final newline") + "\n" +
            Frame(marker, 0, 0, "stdout without newline", "");
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(text));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var results = new WslResult?[2];
        var completed = new List<int>();
        await WslBatchProtocol.ReadResultsAsync(reader, marker, results, (i, _) => completed.Add(i), default);
        completed.Should().Equal(1, 0);
        results[0]!.Output.Should().Be("stdout without newline");
        results[0]!.Success.Should().BeTrue();
        results[1]!.Output.Should().Be(spoof);
        results[1]!.Error.Should().Be("stderr\nno final newline");
        results[1]!.ExitCode.Should().Be(3);
        results[1]!.Success.Should().BeFalse();
    }

    [TestMethod]
    public async Task ReadResultsAsync_FirstFrameBeforeEndOfStream_NotifiesImmediately()
    {
        var marker = WslBatchProtocol.CreateMarker();
        var lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        using var reader = new ChannelReader(lines.Reader);
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new WslResult?[2];
        var reading = WslBatchProtocol.ReadResultsAsync(reader, marker, results, (i, _) => completed.TrySetResult(i), default);
        lines.Writer.TryWrite(Frame(marker, 1, 0, "fast", ""));
        (await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
        reading.IsCompleted.Should().BeFalse();
        results[0].Should().BeNull();
        lines.Writer.TryWrite(Frame(marker, 0, 0, "slow", ""));
        lines.Writer.TryComplete();
        await reading;
    }

    [TestMethod]
    public async Task ReadResultsAsync_CanceledAfterAFrame_RetainsCompletedResult()
    {
        var marker = WslBatchProtocol.CreateMarker();
        var lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        using var reader = new ChannelReader(lines.Reader);
        using var cts = new CancellationTokenSource();
        var results = new WslResult?[2];
        lines.Writer.TryWrite(Frame(marker, 0, 0, "finished", ""));
        Func<Task> action = () => WslBatchProtocol.ReadResultsAsync(reader, marker, results, (_, _) => cts.Cancel(), cts.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        results[0]!.Output.Should().Be("finished");
        results[1].Should().BeNull();
    }

    [TestMethod]
    public void TryParseLine_Timeout_PreservesPartialOutputAndNamesDeadline()
    {
        var marker = WslBatchProtocol.CreateMarker();
        WslBatchProtocol.TryParseLine(Frame(marker, 0, 124, "partial", "diagnostic"), marker, 1,
            out var index, out var result).Should().BeTrue();
        index.Should().Be(0);
        result!.ExitCode.Should().Be(124);
        result.Error.Should().Contain("timed out");
        result.Output.Should().Be("partial");
    }

    [TestMethod]
    public async Task ReadResultsAsync_DuplicateFrame_RejectsWithoutRepeatingCallback()
    {
        var marker = WslBatchProtocol.CreateMarker();
        using var reader = new StringReader(Frame(marker, 0, 0, "one", "") + "\n" + Frame(marker, 0, 0, "two", ""));
        var callbacks = 0;
        Func<Task> action = () => WslBatchProtocol.ReadResultsAsync(reader, marker, new WslResult?[1], (_, _) => callbacks++, default);
        await action.Should().ThrowAsync<InvalidDataException>();
        callbacks.Should().Be(1);
    }

    [TestMethod]
    [DataRow("|9|0||")]
    [DataRow("|0|0|not base64!|")]
    [DataRow("|0|oops||")]
    public void TryParseLine_MalformedOwnMarker_RejectsFrame(string suffix)
    {
        var marker = WslBatchProtocol.CreateMarker();
        Action action = () => WslBatchProtocol.TryParseLine(marker + suffix, marker, 1, out _, out _);
        action.Should().Throw<InvalidDataException>();
        WslBatchProtocol.TryParseLine("CBW_other|0|0||", marker, 1, out _, out _).Should().BeFalse();
    }

    [TestMethod]
    public void BuildScript_TrickyCommandAndCulture_QuotesLiterallyAndUsesInvariantTimeout()
    {
        var command = "printf '%s' \"$HOME\"; echo `whoami`\necho 'it\"s'";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var script = WslBatchProtocol.BuildScript([new(command, TimeSpan.FromSeconds(1.5))], WslBatchProtocol.CreateMarker());
            script.Should().Contain("timeout --kill-after=2s 1.5s");
            script.Should().Contain(WslBatchProtocol.Quote(command));
            script.Should().Contain("set +e +u +m");
            script.Should().Contain("flock -x 9");
            script.Should().Contain("trap cbw_cleanup EXIT");
            script.Should().Contain("base64 -w 0");
            script.Should().Contain("cbw_exit=124");
            script.Should().NotContain("grep");
            script.Should().NotContain("--verbose");
            script.Should().NotContain(".timeout");
            script.Should().NotContain("LC_MESSAGES");
            script.Should().NotContain("LC_ALL");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        WslBatchProtocol.Quote("a'b$`\n").Should().Be("'a'\"'\"'b$`\n'");
    }

    [TestMethod]
    public void BuildScript_AnySourceLineEndings_ProducesNoCarriageReturns()
    {
        var script = WslBatchProtocol.BuildScript(
            [new("echo hi", TimeSpan.FromSeconds(5)), new("echo 'a'", TimeSpan.FromSeconds(5))], WslBatchProtocol.CreateMarker());
        script.Should().NotContain("\r");
    }

    [TestMethod]
    public void BuildScript_LiteralCommandCrLf_PreservesPayloadBytes()
    {
        var command = "printf '%s' 'first\r\nsecond'";
        var script = WslBatchProtocol.BuildScript([new(command, TimeSpan.FromSeconds(5))], WslBatchProtocol.CreateMarker());
        script.Should().Contain(WslBatchProtocol.Quote(command));
        script.Replace(WslBatchProtocol.Quote(command), "").Should().NotContain("\r");
    }

    [TestMethod]
    [TestCategory("BashIntegration")]
    public async Task BuildScript_RealBashMixedPayloadsAndStderr_PreservesResultsAndLiteralBytes()
    {
        var payload = "quotes ' \" dollars $HOME backticks `whoami`\nUnicode 한글 😀\r\nno final newline";
        var marker = WslBatchProtocol.CreateMarker();
        WslBatchCommand[] commands =
        [
            new("printf '%s' " + WslBatchProtocol.Quote(payload), TimeSpan.FromSeconds(5)),
            new("printf '%s' diagnostic >&2; printf '%s' partial; exit 7", TimeSpan.FromSeconds(5)),
            new("head -c 204800 /dev/zero | tr '\\0' x", TimeSpan.FromSeconds(5)),
            new("if read -r input; then printf unexpected; else printf eof; fi", TimeSpan.FromSeconds(2))
        ];
        var results = await RunInBashAsync(commands, marker);
        results[0]!.Output.Should().Be(payload);
        results[0]!.Success.Should().BeTrue();
        results[1]!.ExitCode.Should().Be(7);
        results[1]!.Output.Should().Be("partial");
        results[1]!.Error.Should().Be("diagnostic");
        results[2]!.Output.Should().Be(new string('x', 204800));
        results[3]!.Output.Should().Be("eof");
        results.Should().OnlyContain(result => result != null);
    }

    [TestMethod]
    [TestCategory("BashIntegration")]
    public async Task BuildScript_RealBashSleepingPastDeadline_PreservesPartialOutputAndTimesOut()
    {
        var results = await RunInBashAsync(
            [new("printf partial; printf warning >&2; sleep 20; printf late", TimeSpan.FromSeconds(1.5))],
            WslBatchProtocol.CreateMarker());
        results[0]!.ExitCode.Should().Be(124);
        results[0]!.Success.Should().BeFalse();
        results[0]!.Output.Should().Be("partial");
        results[0]!.Error.Should().Contain("warning").And.Contain("timed out");
    }

    [TestMethod]
    public void TryParseLine_InvalidUtf8_UsesReplacementCharactersWithoutLosingFrame()
    {
        var marker = WslBatchProtocol.CreateMarker();
        WslBatchProtocol.TryParseLine($"{marker}|0|0|/w==|", marker, 1, out _, out var result).Should().BeTrue();
        result!.Output.Should().Be("\uFFFD");
    }

    private static async Task<WslResult?[]> RunInBashAsync(WslBatchCommand[] commands, string marker)
    {
        const string prerequisites = "command -v timeout && command -v flock && command -v base64";
        // Prefer the real thing: WSL launched exactly like production (--exec, no default-shell re-parsing).
        // Otherwise a Unix-style bash, but never the System32 bash.exe stub, which is the WSL launcher and
        // re-expands the script through the default shell.
        var candidates = new List<(string File, string[] Prefix)>();
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wsl = Path.Combine(system32, "wsl.exe");
        if (OperatingSystem.IsWindows() && File.Exists(wsl)) candidates.Add((wsl, ["--exec", "bash", "-ic"]));
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var bash = Path.Combine(path.Trim('"'), OperatingSystem.IsWindows() ? "bash.exe" : "bash");
            if (File.Exists(bash) && !bash.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
                candidates.Add((bash, ["--noprofile", "--norc", "-ic"]));
        }
        candidates.Add((@"C:\Program Files\Gitinash.exe", ["--noprofile", "--norc", "-ic"]));

        ProcessStartInfo StartInfo((string File, string[] Prefix) shell, string script)
        {
            var info = new ProcessStartInfo(shell.File)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in shell.Prefix) info.ArgumentList.Add(argument);
            info.ArgumentList.Add(script);
            return info;
        }

        (string File, string[] Prefix)? chosen = null;
        foreach (var candidate in candidates.Where(c => File.Exists(c.File)))
        {
            using var probeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                using var probe = Process.Start(StartInfo(candidate, prerequisites))!;
                probe.StandardInput.Close();
                var probeOut = probe.StandardOutput.ReadToEndAsync(probeDeadline.Token);
                var probeErr = probe.StandardError.ReadToEndAsync(probeDeadline.Token);
                try { await probe.WaitForExitAsync(probeDeadline.Token); }
                finally { if (!probe.HasExited) probe.Kill(entireProcessTree: true); }
                await Task.WhenAll(probeOut, probeErr);
                if (probe.ExitCode == 0) { chosen = candidate; break; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or IOException) { }
        }
        if (chosen == null) Assert.Inconclusive("no bash with timeout, flock and base64 is available");
        var shell = chosen!.Value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Include both startup noise streams; they must never become provider payloads.
        using var process = Process.Start(StartInfo(shell, "printf 'startup noise\\n'; printf 'interactive warning\\n' >&2;\n" +
            WslBatchProtocol.BuildScript(commands, marker)))!;
        process.StandardInput.Close();
        var results = new WslResult?[commands.Length];
        var reading = WslBatchProtocol.ReadResultsAsync(process.StandardOutput, marker, results, null, deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(reading, errors, process.WaitForExitAsync(deadline.Token));
            process.ExitCode.Should().Be(0, await errors);
            results.Should().OnlyContain(result => result != null);
            return results;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            deadline.Cancel();
            try { await Task.WhenAll(reading, errors); } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public void BuildScript_BackgroundTimeout_CapturesSupervisorPidAndKillsGroupBeforeRemovingPidFile()
    {
        var script = WslBatchProtocol.BuildScript([new("true", TimeSpan.FromSeconds(10))], WslBatchProtocol.CreateMarker());

        script.Should().Contain("(\n cbw_start=$SECONDS\n timeout --kill-after=2s 10s bash -c");
        script.Should().Contain("'true' &\n cbw_job=$!\n printf '%s\\n' \"$cbw_job\" >\"$cbw_dir/0.pid\"");
        script.Should().Contain("wait \"$cbw_job\"\n cbw_exit=$?\n kill -KILL -- \"-$cbw_job\" 2>/dev/null || :\n rm -f -- \"$cbw_dir/0.pid\"");
    }

    [TestMethod]
    [DataRow(10.0, 9)]
    [DataRow(1.5, 1)]
    [DataRow(0.5, 0)]
    public void BuildScript_SignalExitCodes_NormalizesOnlyWhenElapsedTimeReachesDeadline(double seconds, int threshold)
    {
        var script = WslBatchProtocol.BuildScript([new("true", TimeSpan.FromSeconds(seconds))], WslBatchProtocol.CreateMarker());

        script.Should().Contain("cbw_elapsed=$((SECONDS - cbw_start))");
        script.Should().Contain($"case \"$cbw_exit\" in 137|124|143) if [ \"$cbw_elapsed\" -ge {threshold} ]; then cbw_exit=124; fi ;; esac");
    }

    [TestMethod]
    public void BuildScript_InvalidInputs_RejectsBeforeStartingProcess()
    {
        var marker = WslBatchProtocol.CreateMarker();
        Action timeout = () => WslBatchProtocol.BuildScript([new("true", TimeSpan.Zero)], marker);
        timeout.Should().Throw<ArgumentOutOfRangeException>();
        Action nul = () => WslBatchProtocol.BuildScript([new("bad\0command", TimeSpan.FromSeconds(1))], marker);
        nul.Should().Throw<ArgumentException>();
        Action unsafeMarker = () => WslBatchProtocol.BuildScript([], "CBW_'; touch anything");
        unsafeMarker.Should().Throw<ArgumentException>();
        WslBatchProtocol.CreateMarker().Should().NotBe(WslBatchProtocol.CreateMarker());
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }

    private sealed class ChannelReader(System.Threading.Channels.ChannelReader<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (!await channel.WaitToReadAsync(cancellationToken)) return null;
            return await channel.ReadAsync(cancellationToken);
        }
    }
}
