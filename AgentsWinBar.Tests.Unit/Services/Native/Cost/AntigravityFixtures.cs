using System.Text;
using Microsoft.Data.Sqlite;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

/// <summary>
/// Synthetic Antigravity (agy) conversation databases: hand-encoded protobuf records in the field layout the reader
/// decodes, written with an independent SQLite build (Microsoft.Data.Sqlite) in agy's table layout. Nothing here reads
/// a real conversation.
/// </summary>
internal static class AntigravityFixtures
{
    internal static readonly DateTimeOffset Noon = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One model call as agy records it in <c>gen_metadata.data</c>.</summary>
    internal sealed record Call
    {
        internal long Input { get; init; }
        internal long Output { get; init; }
        internal long Thinking { get; init; }
        internal long CacheRead { get; init; }
        internal string? Model { get; init; } = "gemini-4-neon";
        internal string? Label { get; init; }
        /// <summary>The generation's own time (<c>#1.#9.#4</c>); null for agy builds that leave it out.</summary>
        internal DateTimeOffset? Time { get; init; }
        internal string? StepId { get; init; }
        internal string? BotId { get; init; }
        internal string? ResponseId { get; init; }
        internal bool HasUsage { get; init; } = true;
        /// <summary>Bytes of unrelated content in the record (prompt text, for example) that the reader must skip.</summary>
        internal int ContentBytes { get; init; } = 64;
    }

    /// <summary>One row of <c>steps</c>: its <c>metadata</c> and a <c>step_payload</c> the reader must never select.</summary>
    internal sealed record StepRow(long Idx, DateTimeOffset? Created, string? StepId, string? BotId = null, int PayloadBytes = 256);

    internal static byte[] Encode(Call call)
    {
        var chat = new List<byte[]>();
        if (call.HasUsage)
        {
            var usage = new List<byte[]>
            {
                Varint(1, 1298), Varint(2, (ulong)call.Input), Varint(3, (ulong)(call.Output + call.Thinking)),
                Varint(5, (ulong)call.CacheRead), Varint(9, (ulong)call.Thinking), Varint(10, (ulong)call.Output)
            };
            if (call.BotId is not null) usage.Add(Text(7, call.BotId));
            if (call.ResponseId is not null) usage.Add(Text(11, call.ResponseId));
            chat.Add(Message(4, [.. usage]));
            // agy repeats the counts under #17.#2; the reader ignores that copy.
            chat.Add(Message(17, Message(2, [.. usage])));
        }
        if (call.Time is { } time) chat.Add(Message(9, Message(4, Timestamp(time))));
        if (call.Model is not null) chat.Add(Text(19, call.Model));
        if (call.Label is not null) chat.Add(Text(21, call.Label));
        chat.Add(Bytes(3, Encoding.UTF8.GetBytes(new string('p', call.ContentBytes))));
        var root = new List<byte[]> { Message(1, [.. chat]) };
        if (call.StepId is not null) root.Add(Text(4, call.StepId));
        root.Add(Bytes(2, Encoding.UTF8.GetBytes("unrelated conversation content")));
        return Concat(root);
    }

    internal static byte[] EncodeStep(StepRow step)
    {
        var parts = new List<byte[]>();
        if (step.Created is { } created) parts.Add(Message(1, Timestamp(created)));
        if (step.BotId is not null) parts.Add(Message(9, Text(7, step.BotId)));
        if (step.StepId is not null) parts.Add(Text(12, step.StepId));
        parts.Add(Varint(2, 7));
        return Concat(parts);
    }

    internal static byte[][] Timestamp(DateTimeOffset time)
    {
        var ms = time.ToUnixTimeMilliseconds();
        return [Varint(1, (ulong)(ms / 1000)), Varint(2, (ulong)(ms % 1000 * 1_000_000))];
    }

    internal static byte[] Varint(int field, ulong value) => Concat([Raw((ulong)field << 3), Raw(value)]);

    internal static byte[] Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    internal static byte[] Bytes(int field, byte[] data) => Concat([Raw(((ulong)field << 3) | 2), Raw((ulong)data.Length), data]);

    internal static byte[] Message(int field, params byte[][] parts) => Bytes(field, Concat(parts));

    internal static byte[] Raw(ulong value)
    {
        var bytes = new List<byte>(10);
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
        return [.. bytes];
    }

    internal static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(part => part).ToArray();

    /// <summary>A connection that really closes (no pool), so a closed database has no sidecar files, as agy leaves it.</summary>
    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Creates agy's tables (in WAL mode, as agy keeps them) with the given rows.</summary>
    internal static void Create(string path, IEnumerable<Call> calls, IEnumerable<StepRow>? steps = null)
    {
        using var connection = Open(path);
        CreateSchema(connection);
        Insert(connection, calls.Select((call, index) => ((long)index, call)), steps ?? []);
    }

    internal static void CreateSchema(SqliteConnection connection)
    {
        Execute(connection, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE gen_metadata(idx INTEGER, data BLOB, size INTEGER);
            CREATE TABLE steps(idx INTEGER, step_type INTEGER, status INTEGER, has_subtrajectory INTEGER, metadata BLOB,
                error_details BLOB, permissions BLOB, task_details BLOB, render_info BLOB, step_payload BLOB, step_format INTEGER);
            CREATE TABLE trajectory_meta(trajectory_id TEXT, cascade_id TEXT, trajectory_type INTEGER, source INTEGER);
            CREATE TABLE executor_metadata(idx INTEGER, data BLOB);
            CREATE TABLE trajectory_metadata_blob(data BLOB);
            """);
    }

    internal static void Insert(SqliteConnection connection, IEnumerable<(long Idx, Call Call)> calls, IEnumerable<StepRow> steps)
    {
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO gen_metadata VALUES ($idx, $data, $size)";
            var idx = insert.Parameters.Add("$idx", SqliteType.Integer);
            var data = insert.Parameters.Add("$data", SqliteType.Blob);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            foreach (var (index, call) in calls)
            {
                var bytes = Encode(call);
                idx.Value = index;
                data.Value = bytes;
                size.Value = bytes.Length;
                insert.ExecuteNonQuery();
            }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO steps VALUES ($idx, 1, 3, 0, $metadata, NULL, NULL, NULL, NULL, $payload, 1)";
            var idx = insert.Parameters.Add("$idx", SqliteType.Integer);
            var metadata = insert.Parameters.Add("$metadata", SqliteType.Blob);
            var payload = insert.Parameters.Add("$payload", SqliteType.Blob);
            foreach (var step in steps)
            {
                idx.Value = step.Idx;
                metadata.Value = EncodeStep(step);
                payload.Value = new byte[step.PayloadBytes];
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Every file in <paramref name="directory"/> with its bytes' hash, size and last-write time.</summary>
    internal static Dictionary<string, (string Hash, long Length, DateTime Written)> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path), path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), stream.Length,
                File.GetLastWriteTimeUtc(path));
        });
}
