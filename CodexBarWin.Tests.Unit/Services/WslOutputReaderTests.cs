using AwesomeAssertions;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class WslOutputReaderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadToEndAsync_StdoutOrStderrExceedsSharedBudget_StopsReading(bool stderr)
    {
        var budget = new WslOutputReader.Budget(8);
        using var output = new StringReader(stderr ? "ok" : "too much output");
        using var error = new StringReader(stderr ? "too much error" : "ok");
        Func<Task> read = async () => await Task.WhenAll(WslOutputReader.ReadToEndAsync(output, budget, default),
            WslOutputReader.ReadToEndAsync(error, budget, default));
        await read.Should().ThrowAsync<WslOutputLimitException>();
    }

    [TestMethod]
    public async Task ReadToEndAsync_ExactUtf8Budget_PreservesPayload()
    {
        using var reader = new StringReader("hello 😀");
        (await WslOutputReader.ReadToEndAsync(reader, new WslOutputReader.Budget(10), default)).Should().Be("hello 😀");
    }

    [TestMethod]
    public async Task ReadToEndAsync_MultibytePayloadExceedsByteBudget_RejectsShortString()
    {
        using var reader = new StringReader("😀😀");
        Func<Task> read = () => WslOutputReader.ReadToEndAsync(reader, new WslOutputReader.Budget(7), default);
        await read.Should().ThrowAsync<WslOutputLimitException>();
    }

    [TestMethod]
    public async Task ReadToEndAsync_CanceledCaller_StopsBeforeReading()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        using var reader = new StringReader("payload");
        Func<Task> read = () => WslOutputReader.ReadToEndAsync(reader, new WslOutputReader.Budget(100), cts.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task ReadToEndAsync_SurrogatePairCrossesReadBoundary_CountsUtf8BytesOnce()
    {
        var text = new string('a', 4095) + "😀";
        using var reader = new StringReader(text);
        (await WslOutputReader.ReadToEndAsync(reader, new WslOutputReader.Budget(4099), default)).Should().Be(text);
    }
}
