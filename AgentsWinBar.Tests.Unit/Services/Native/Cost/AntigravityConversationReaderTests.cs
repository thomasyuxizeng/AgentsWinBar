using AwesomeAssertions;
using AgentsWinBar.Services.Native.Cost;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.AntigravityFixtures;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

[TestClass]
public sealed class AntigravityConversationReaderTests
{
    private string _directory = null!;
    private string Database => Path.Combine(_directory, "conversation.db");

    [TestInitialize]
    public void Initialize() => _directory = CostTestSupport.TempDirectory("agy-reader");

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static AntigravityReadResult Read(string path) => AntigravityConversationReader.Read(path, CancellationToken.None);

    [TestMethod]
    public void Read_WhenCallsCarryTheirOwnTime_ReturnsTokensModelsAndTimes()
    {
        Create(Database,
        [
            new Call { Input = 1000, CacheRead = 200, Output = 50, Thinking = 30, Model = "gemini-4-neon", Time = Noon },
            new Call { Input = 10, Output = 5, Model = "claude-sonnet-5-5", Time = Noon.AddMinutes(1) }
        ]);

        var result = Read(Database);

        result.IsHistory.Should().BeTrue();
        result.ReadThroughWal.Should().BeFalse();
        result.Rows.Should().Be(2);
        result.Calls.Should().HaveCount(2);
        var first = result.Calls[0];
        first.Model.Should().Be("gemini-4-neon");
        first.TimestampMs.Should().Be(Noon.ToUnixTimeMilliseconds());
        first.Dating.Should().Be(AntigravityDating.Embedded);
        first.Tokens.Should().Be(new CostTokens(1000, 80, 200, 0, 0, 30), "thinking bills as output and is reported within it");
        result.Calls[1].Model.Should().Be("claude-sonnet-5-5");
        result.Calls[1].Tokens.Total.Should().Be(15);
    }

    [TestMethod]
    public void Read_WhenCallsHaveOnlyStepIds_TakesEachTimeFromItsStepInOrder()
    {
        Create(Database,
            [
                new Call { Input = 1, StepId = "step-a" },
                new Call { Input = 2, StepId = "step-b" },
                new Call { Input = 3, StepId = "step-b" }
            ],
            [
                new StepRow(0, Noon, "step-a"),
                new StepRow(1, Noon.AddMinutes(1), "step-b"),
                new StepRow(2, Noon.AddMinutes(2), "step-b")
            ]);

        var result = Read(Database);

        result.Calls.Select(call => (call.Tokens.Input, call.TimestampMs, call.Dating)).Should().Equal(
            (1L, Noon.ToUnixTimeMilliseconds(), AntigravityDating.Position),
            (2L, Noon.AddMinutes(1).ToUnixTimeMilliseconds(), AntigravityDating.Position),
            (3L, Noon.AddMinutes(2).ToUnixTimeMilliseconds(), AntigravityDating.Position));
        result.Undated.Should().Be(0);
    }

    [TestMethod]
    public void Read_WhenABotIdIsUsedOnce_TakesTheTimeOfThatBotsStep()
    {
        // The steps are in the opposite order of the generations: only the bot ids can pair them correctly.
        Create(Database,
            [
                new Call { Input = 1, StepId = "step-a", BotId = "bot-1" },
                new Call { Input = 2, StepId = "step-a", BotId = "bot-2" }
            ],
            [
                new StepRow(0, Noon.AddMinutes(5), "step-a", "bot-2"),
                new StepRow(1, Noon, "step-a", "bot-1")
            ]);

        var result = Read(Database);

        result.Calls.Select(call => (call.Tokens.Input, call.TimestampMs, call.Dating)).Should().Equal(
            (1L, Noon.ToUnixTimeMilliseconds(), AntigravityDating.BotId),
            (2L, Noon.AddMinutes(5).ToUnixTimeMilliseconds(), AntigravityDating.BotId));
    }

    [TestMethod]
    public void Read_WhenAStepHasNoTime_LeavesItsCallUndated()
    {
        Create(Database,
            [new Call { Input = 1, StepId = "step-a" }, new Call { Input = 2, StepId = "step-missing" }],
            [new StepRow(0, null, "step-a")]);

        var result = Read(Database);

        result.Calls.Should().BeEmpty("a call is never dated by a guess");
        result.Undated.Should().Be(2);
    }

    [TestMethod]
    public void Read_WhenACallHasNeitherTimeNorStep_LeavesItUndated()
    {
        Create(Database, [new Call { Input = 4 }, new Call { Input = 5, Time = Noon }]);

        var result = Read(Database);

        result.Calls.Select(call => call.Tokens.Input).Should().Equal(5L);
        result.Undated.Should().Be(1);
    }

    [TestMethod]
    public void Read_WhenARecordIsMalformed_DoesNotRecoverTimesByPosition()
    {
        Create(Database,
            [new Call { Input = 1, StepId = "step-a" }, new Call { Input = 2, Time = Noon }],
            [new StepRow(0, Noon, "step-a")]);
        using (var connection = Open(Database))
            Execute(connection, "INSERT INTO gen_metadata VALUES (5, x'0a05', 2)");

        var result = Read(Database);

        result.InvalidRows.Should().Be(1);
        result.Calls.Select(call => call.Tokens.Input).Should().Equal(2L);
        result.Undated.Should().Be(1);
    }

    [TestMethod]
    public void Read_WhenAModelIsMissing_InheritsTheModelItsLabelRecorded()
    {
        Create(Database,
        [
            new Call { Input = 1, Model = "gemini-4-carbon", Label = "label-a", Time = Noon },
            new Call { Input = 2, Model = null, Label = "label-a", Time = Noon },
            new Call { Input = 3, Model = "gemini-4-neon", Label = "label-b", Time = Noon },
            new Call { Input = 4, Model = "gemini-4-carbon", Label = "label-b", Time = Noon },
            new Call { Input = 5, Model = null, Label = "label-b", Time = Noon }
        ]);

        var result = Read(Database);

        result.Calls.Select(call => call.Model).Should().Equal(
            "gemini-4-carbon", "gemini-4-carbon", "gemini-4-neon", "gemini-4-carbon", CostPricing.UnattributedModel);
    }

    [TestMethod]
    public void Read_WhenACallHasNoModelAndNoTokens_StoresTheModelLessMarker()
    {
        Create(Database, [new Call { Model = null, Time = Noon }, new Call { Input = 9, Model = null, Time = Noon }]);

        var result = Read(Database);

        result.Calls.Select(call => call.Model).Should().Equal(
            AntigravityConversationReader.ModelLessZeroUsage, CostPricing.UnattributedModel);
    }

    [TestMethod]
    public void Read_WhenAResponseIdRepeats_CountsTheResponseOnce()
    {
        Create(Database,
        [
            new Call { Input = 5, ResponseId = "response-1", Time = Noon },
            new Call { Input = 5, ResponseId = "response-1", Time = Noon },
            new Call { Input = 6, Time = Noon },
            new Call { Input = 6, Time = Noon }
        ]);

        var result = Read(Database);

        result.Calls.Select(call => call.Tokens.Input).Should().Equal(5L, 6L, 6L);
        result.Calls.Select(call => call.Key).Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void Read_WhenTheDatabaseIsCopiedUnderTheSameName_GivesTheSameKeys()
    {
        Create(Database, [new Call { Input = 5, ResponseId = "response-1", Time = Noon }, new Call { Input = 6, Time = Noon }]);
        var copyDirectory = Directory.CreateDirectory(Path.Combine(_directory, "copy")).FullName;
        var copy = Path.Combine(copyDirectory, "conversation.db");
        File.Copy(Database, copy);

        Read(copy).Calls.Select(call => call.Key).Should().Equal(Read(Database).Calls.Select(call => call.Key));
    }

    [TestMethod]
    public void Read_WhenTheDatabaseHasNoGenMetadata_IsNotHistory()
    {
        using (var connection = Open(Database))
            Execute(connection, "CREATE TABLE other(idx INTEGER, data BLOB); INSERT INTO other VALUES (1, x'01')");

        var result = Read(Database);

        result.IsHistory.Should().BeFalse();
        result.Calls.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("CREATE TABLE gen_metadata(idx INTEGER, payload BLOB)")]
    [DataRow("CREATE TABLE gen_metadata(idx INTEGER, data BLOB, extra INTEGER GENERATED ALWAYS AS (idx + 1))")]
    [DataRow("CREATE TABLE source(idx INTEGER, data BLOB); CREATE VIEW gen_metadata AS SELECT idx, data FROM source")]
    public void Read_WhenGenMetadataHasAnotherShape_IsHistoryWithoutCalls(string schema)
    {
        using (var connection = Open(Database)) Execute(connection, schema);

        var result = Read(Database);

        result.IsHistory.Should().BeTrue();
        result.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_WhenTheDatabaseIsClosed_ChangesNoFile()
    {
        Create(Database, [new Call { Input = 1, Time = Noon }], [new StepRow(0, Noon, "step-a", PayloadBytes: 4096)]);
        var before = Snapshot(_directory);

        Read(Database).Calls.Should().HaveCount(1);

        Snapshot(_directory).Should().BeEquivalentTo(before, "no sidecar file is created and no byte or time changes");
    }

    [TestMethod]
    public void Read_WhenAWriterHoldsUncheckpointedRows_ReadsThemAndChangesNoFile()
    {
        using var writer = Open(Database);
        Execute(writer, "PRAGMA wal_autocheckpoint=0");
        CreateSchema(writer);
        Insert(writer, [(0, new Call { Input = 7, Time = Noon }), (1, new Call { Input = 8, Time = Noon })], []);
        File.Exists(Database + "-wal").Should().BeTrue();
        File.Exists(Database + "-shm").Should().BeTrue();
        var before = Snapshot(_directory);

        var result = Read(Database);

        result.ReadThroughWal.Should().BeTrue();
        result.Calls.Select(call => call.Tokens.Input).Should().Equal(7L, 8L);
        Snapshot(_directory).Should().BeEquivalentTo(before, "the reader writes neither a read mark nor a checkpoint");
    }

    [TestMethod]
    public void Read_WhenTheLogHasNoSharedMemoryFile_SkipsWithoutCreatingIt()
    {
        Create(Database, [new Call { Input = 1, Time = Noon }]);
        File.WriteAllBytes(Database + "-wal", []);

        var read = () => Read(Database);

        read.Should().Throw<AntigravityDatabaseUnavailableException>();
        File.Exists(Database + "-shm").Should().BeFalse();
    }

    [TestMethod]
    public void Read_WhenARollbackJournalExists_Skips()
    {
        Create(Database, [new Call { Input = 1, Time = Noon }]);
        File.WriteAllBytes(Database + "-journal", [1, 2, 3]);

        var read = () => Read(Database);

        read.Should().Throw<AntigravityDatabaseUnavailableException>();
    }

    [TestMethod]
    public void Read_WhenTheFileIsLockedExclusively_SkipsAndReadsItOnceUnlocked()
    {
        Create(Database, [new Call { Input = 1, Time = Noon }]);

        using (new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var read = () => Read(Database);
            read.Should().Throw<AntigravityDatabaseUnavailableException>();
        }

        Read(Database).Calls.Should().HaveCount(1);
    }

    [TestMethod]
    public void Read_WhenTheFileIsNotADatabase_Skips()
    {
        File.WriteAllBytes(Database, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

        var read = () => Read(Database);

        read.Should().Throw<AntigravityDatabaseUnavailableException>();
    }

    [TestMethod]
    public void Uri_WhenThePathHasReservedCharacters_EscapesThem()
    {
        var uri = AntigravityConversationReader.Uri(@"C:\data\a b%#?.db", "mode=ro");

        uri.Should().Be("file:///C:/data/a%20b%25%23%3f.db?mode=ro");
    }

    [TestMethod]
    public void Read_WhenThePathHasReservedCharacters_ReadsTheDatabase()
    {
        var path = Path.Combine(_directory, "a b%#.db");
        Create(path, [new Call { Input = 4, Time = Noon }]);

        Read(path).Calls.Should().HaveCount(1);
    }
}
