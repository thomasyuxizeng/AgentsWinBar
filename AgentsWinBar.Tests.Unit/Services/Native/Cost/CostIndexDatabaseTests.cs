using AwesomeAssertions;
using AgentsWinBar.Services.Native.Cost;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

[TestClass]
public sealed class CostIndexDatabaseTests
{
    private string _directory = null!;
    private string DatabasePath => Path.Combine(_directory, "test.sqlite");

    [TestInitialize]
    public void Initialize() => _directory = TempDirectory("cost-database");

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void Write_WhenStepFails_ResetsTheStatementBeforeThrowing()
    {
        using var database = new CostIndexDatabase(DatabasePath);
        database.Execute("CREATE TABLE item (id INTEGER PRIMARY KEY)");
        using var insert = database.Prepare("INSERT INTO item (id) VALUES (1)");
        insert.Write();

        var act = insert.Write;

        act.Should().Throw<CostIndexDatabase.SqliteException>();
        // An unreset statement still holds the error of the failed step, and Reset reports it again.
        var reset = insert.Reset;
        reset.Should().NotThrow("Write already reset the statement");
    }

    [TestMethod]
    public void Write_WhenStepSucceeds_CanRunAgainWithNewBindings()
    {
        using var database = new CostIndexDatabase(DatabasePath);
        database.Execute("CREATE TABLE item (id INTEGER PRIMARY KEY)");
        using var insert = database.Prepare("INSERT INTO item (id) VALUES (?)");

        insert.Bind(1, 1);
        insert.Write();
        insert.Bind(1, 2);
        insert.Write();

        using var count = database.Prepare("SELECT COUNT(*) FROM item");
        count.Step().Should().BeTrue();
        count.Number(0).Should().Be(2);
    }

    [TestMethod]
    public void Dispose_WhenAStatementIsStillOpen_ClosesTheConnectionOnceTheStatementIsDisposed()
    {
        var database = new CostIndexDatabase(DatabasePath);
        database.Execute("CREATE TABLE item (id INTEGER PRIMARY KEY)");
        var statement = database.Prepare("SELECT id FROM item");

        database.Dispose();
        statement.Dispose();

        // sqlite3_close would have returned BUSY and leaked the connection, which keeps the file open.
        var delete = () => File.Delete(DatabasePath);
        delete.Should().NotThrow("the database file is no longer held open");
        File.Exists(DatabasePath).Should().BeFalse();
    }
}
