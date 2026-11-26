using System.Text;
using AwesomeAssertions;
using AgentsWinBar.Services.Native.Cost;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.AntigravityFixtures;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

[TestClass]
public sealed class AntigravityProtoReaderTests
{
    private static CostKey Id(string value) => CostKey.Hash(Encoding.UTF8.GetBytes(value));

    [TestMethod]
    public void TryParseTurn_WhenRecordHasUsage_ReadsTokenFieldsModelTimeAndIds()
    {
        var record = Encode(new Call
        {
            Input = 1200, CacheRead = 300, Output = 40, Thinking = 25, Model = " gemini-4-neon ", Label = "label-a",
            Time = Noon.AddMilliseconds(250), StepId = "step-1", BotId = "bot-1", ResponseId = "response-1"
        });

        new AntigravityProtoReader().TryParseTurn(record, out var turn).Should().BeTrue();

        turn.HasUsage.Should().BeTrue();
        turn.Usage.NewInput.Should().Be(1200);
        turn.Usage.CacheRead.Should().Be(300);
        turn.Usage.Output.Should().Be(40, "field 10 is the visible output");
        turn.Usage.Reasoning.Should().Be(25, "field 9 is the thinking output");
        turn.Model.Should().Be("gemini-4-neon");
        turn.TimestampMs.Should().Be(Noon.AddMilliseconds(250).ToUnixTimeMilliseconds());
        turn.Label.Should().Be(Id("label-a"));
        turn.StepId.Should().Be(Id("step-1"));
        turn.Usage.BotId.Should().Be(Id("bot-1"));
        turn.Usage.ResponseId.Should().Be(Id("response-1"));
    }

    [TestMethod]
    public void TryParseTurn_WhenRecordHasNoTimeModelOrIds_LeavesThemAbsent()
    {
        var record = Encode(new Call { Input = 7, Model = null });

        new AntigravityProtoReader().TryParseTurn(record, out var turn).Should().BeTrue();

        turn.Usage.NewInput.Should().Be(7);
        turn.TimestampMs.Should().BeNull();
        turn.Model.Should().BeNull();
        turn.StepId.Should().Be(default(CostKey));
        turn.Usage.ResponseId.Should().Be(default(CostKey));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  \t")]
    public void TryParseTurn_WhenModelOrIdIsBlank_TreatsItAsAbsent(string blank)
    {
        var record = Encode(new Call { Input = 1, Model = blank, StepId = blank, ResponseId = blank });

        new AntigravityProtoReader().TryParseTurn(record, out var turn).Should().BeTrue();

        turn.Model.Should().BeNull();
        turn.StepId.Should().Be(default(CostKey));
        turn.Usage.ResponseId.Should().Be(default(CostKey));
    }

    [TestMethod]
    public void TryParseTurn_WhenModelHasCharactersAnIdCannotHold_RecordsItUnattributed()
    {
        var record = Encode(new Call { Input = 1, Model = "model with spaces" });

        new AntigravityProtoReader().TryParseTurn(record, out var turn).Should().BeTrue();

        turn.Model.Should().Be(CostPricing.UnattributedModel);
    }

    [TestMethod]
    public void TryParseTurn_WhenRecordHasNoUsage_ParsesWithoutUsage()
    {
        new AntigravityProtoReader().TryParseTurn(Encode(new Call { HasUsage = false, Time = Noon }), out var turn)
            .Should().BeTrue();

        turn.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenRecordHasNoChatMessage_ReturnsFalse()
    {
        var record = Concat([Text(4, "step-1"), Bytes(2, [1, 2, 3])]);

        new AntigravityProtoReader().TryParseTurn(record, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenRecordIsTruncated_ReturnsFalse()
    {
        var record = Encode(new Call { Input = 5, Time = Noon });

        new AntigravityProtoReader().TryParseTurn(record.AsSpan(0, record.Length - 3), out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenCounterExceedsInt32_ReturnsFalse()
    {
        var record = Message(1, Message(4, Varint(2, (ulong)int.MaxValue + 1)));

        new AntigravityProtoReader().TryParseTurn(record, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenCounterHasTheWrongWireType_ReturnsFalse()
    {
        var record = Message(1, Message(4, Text(2, "12")));

        new AntigravityProtoReader().TryParseTurn(record, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenRecordUsesAGroupWireType_ReturnsFalse()
    {
        var record = Concat([Message(1, Message(4, Varint(2, 1))), Raw((5UL << 3) | 3)]);

        new AntigravityProtoReader().TryParseTurn(record, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenTimeIsZeroOrOutOfRange_ReturnsFalse()
    {
        var zero = Message(1, Message(4, Varint(2, 1)), Message(9, Message(4, Varint(1, 0))));
        var nanos = Message(1, Message(4, Varint(2, 1)), Message(9, Message(4, Varint(1, 1), Varint(2, 1_000_000_000))));

        new AntigravityProtoReader().TryParseTurn(zero, out _).Should().BeFalse();
        new AntigravityProtoReader().TryParseTurn(nanos, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseTurn_WhenUnrelatedFieldsCarryText_SkipsThemByLength()
    {
        var record = Encode(new Call { Input = 3, ContentBytes = 100_000, Time = Noon });

        new AntigravityProtoReader().TryParseTurn(record, out var turn).Should().BeTrue();

        turn.Usage.NewInput.Should().Be(3);
    }

    [TestMethod]
    public void TryParseTurn_WhenModelRepeatsAcrossRecords_ReturnsTheSameNormalizedId()
    {
        var reader = new AntigravityProtoReader();
        var models = new List<string?>();
        foreach (var model in new[] { "gemini-4-neon", "claude-sonnet-5-5", "gemini-4-neon" })
        {
            reader.TryParseTurn(Encode(new Call { Input = 1, Model = model }), out var turn).Should().BeTrue();
            models.Add(turn.Model);
        }

        models.Should().Equal("gemini-4-neon", "claude-sonnet-5-5", "gemini-4-neon");
    }

    [TestMethod]
    public void TryParseStep_WhenMetadataHasTimeBotAndStepId_ReadsThem()
    {
        var record = EncodeStep(new StepRow(4, Noon, "step-1", "bot-1"));

        AntigravityProtoReader.TryParseStep(record, out var step).Should().BeTrue();

        step.TimestampMs.Should().Be(Noon.ToUnixTimeMilliseconds());
        step.StepId.Should().Be(Id("step-1"));
        step.BotId.Should().Be(Id("bot-1"));
    }

    [TestMethod]
    public void TryParseStep_WhenTimeIsMalformed_WithholdsOnlyTheTime()
    {
        var record = Concat([Message(1, Varint(1, 0)), Text(12, "step-1")]);

        AntigravityProtoReader.TryParseStep(record, out var step).Should().BeTrue();

        step.TimestampMs.Should().BeNull();
        step.StepId.Should().Be(Id("step-1"));
    }

    [TestMethod]
    public void TryParseStep_WhenBotIdIsMalformed_DropsTheBotIdEvenIfALaterOneIsValid()
    {
        var record = Concat([Message(9, Varint(7, 1)), Message(9, Text(7, "bot-1")), Text(12, "step-1")]);

        AntigravityProtoReader.TryParseStep(record, out var step).Should().BeTrue();

        step.BotId.Should().Be(default(CostKey));
    }

    [TestMethod]
    public void TryParseStep_WhenRecordIsTruncated_ReturnsFalse()
    {
        var record = EncodeStep(new StepRow(1, Noon, "step-1"));

        AntigravityProtoReader.TryParseStep(record.AsSpan(0, record.Length - 4), out _).Should().BeFalse();
    }
}
