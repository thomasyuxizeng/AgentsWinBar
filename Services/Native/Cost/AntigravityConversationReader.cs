using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentsWinBar.Services.Native.Cost;

/// <summary>
/// An Antigravity database that cannot be read right now (locked, busy, damaged, or changed while it was read). It is
/// never an index error: the scanner skips the database and tries it again on a later update.
/// </summary>
internal sealed class AntigravityDatabaseUnavailableException(string reason) : IOException(reason);

/// <summary>How a call got its time: its own generation record, its step by bot id, or its step by position.</summary>
internal enum AntigravityDating { Embedded, BotId, Position }

/// <summary>One model call of a conversation, ready for the index. Its key is the same in every copy of the database.</summary>
internal readonly record struct AntigravityCall(CostKey Key, long TimestampMs, string Model, CostTokens Tokens,
    AntigravityDating Dating);

/// <summary>What one database held. Counts are aggregates only; nothing here names a conversation.</summary>
internal sealed class AntigravityReadResult
{
    internal List<AntigravityCall> Calls { get; } = [];
    /// <summary>The database has a gen_metadata table: it is Antigravity history even when it holds no usable call.</summary>
    internal bool IsHistory { get; set; }
    /// <summary>The database was read through its write-ahead log, so its writer may still be adding calls.</summary>
    internal bool ReadThroughWal { get; set; }
    internal int Rows { get; set; }
    /// <summary>Rows that were malformed or had no usage.</summary>
    internal int InvalidRows { get; set; }
    /// <summary>Calls whose time could not be established; they are not counted, never dated by a guess.</summary>
    internal int Undated { get; set; }
}

/// <summary>
/// Reads the model calls of one Antigravity (agy) conversation database. A port of CodexBar's local history reader
/// (AntigravityLocalSQLite.swift, AntigravityLocalSQLiteSchema.swift and the per-conversation parts of
/// AntigravityLocalReader.swift at a26ca57, MIT, Peter Steinberger), through the Windows SQLite library the cost index
/// already uses.
/// <para>
/// Read-only, always. agy may be writing the database at the same moment, and nothing here may create, change, lock for
/// writing or checkpoint its files:
/// <list type="bullet">
/// <item>Without a <c>-wal</c> file (a closed conversation) the main file alone is the database. It is opened
/// <c>immutable=1</c>, which takes no lock and never creates sidecar files (a plain read-only open of a WAL database
/// creates and leaves <c>-wal</c> and <c>-shm</c> files on Windows). The result counts only when the file's size, time,
/// identity and header, and the absence of a <c>-wal</c>, are the same afterwards, as upstream requires of this mode.</item>
/// <item>With <c>-wal</c> and <c>-shm</c> files (agy has the conversation open) it is opened <c>mode=ro&amp;readonly_shm=1</c>,
/// which reads committed rows from the log without writing even a read mark, and with checkpoint-on-close turned off.</item>
/// <item>A <c>-wal</c> without its <c>-shm</c>, or a <c>-journal</c> (a write in progress, or one interrupted), is skipped:
/// every way of opening it would create a file or could read a half-written state.</item>
/// </list>
/// </para>
/// <para>
/// Privacy: only <c>gen_metadata.idx/data</c> and <c>steps.idx/metadata</c> are selected (never <c>step_payload</c>), and
/// of those records only token counts, model ids, ids (kept as hashes) and times are decoded.
/// </para>
/// </summary>
internal static class AntigravityConversationReader
{
    /// <summary>
    /// The model stored for a call that names no model and used no tokens. Upstream counts such a call for its day at
    /// $0 without a model row; a model id can never be empty, so the report can tell these rows apart.
    /// </summary>
    internal const string ModelLessZeroUsage = "";

    /// <summary>Records larger than this are not read (upstream's per-record bound).</summary>
    internal const int MaxRecordBytes = 16 * 1024 * 1024;
    /// <summary>Upstream's bounds on the schema walk.</summary>
    private const int MaxSchemaEntries = 128;
    private const int MaxSchemaColumns = 64;

    internal static AntigravityReadResult Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(path + "-journal")) throw new AntigravityDatabaseUnavailableException("A write is in progress.");
        var wal = File.Exists(path + "-wal");
        if (wal)
        {
            if (!File.Exists(path + "-shm"))
                throw new AntigravityDatabaseUnavailableException("The write-ahead log has no shared-memory file.");
            var live = ReadDatabase(path, "mode=ro&readonly_shm=1", token);
            live.ReadThroughWal = true;
            return live;
        }

        var before = IdleState.Read(path) ?? throw new AntigravityDatabaseUnavailableException("The database is in use.");
        var result = ReadDatabase(path, "mode=ro&immutable=1", token);
        // An immutable connection neither locks nor notices changes: trust it only if nothing changed meanwhile.
        if (IdleState.Read(path) != before)
            throw new AntigravityDatabaseUnavailableException("The database changed while it was read.");
        return result;
    }

    private static AntigravityReadResult ReadDatabase(string path, string query, CancellationToken token)
    {
        var status = Native.sqlite3_open_v2(Uri(path, query), out var database, OpenReadOnly | OpenUri, IntPtr.Zero);
        try
        {
            if (status != 0) throw Unavailable(status);
            // A read-only connection cannot checkpoint, and this makes sure it never tries on close either.
            Native.sqlite3_db_config(database, NoCheckpointOnClose, 1, IntPtr.Zero);
            Execute(database, "PRAGMA busy_timeout=250; PRAGMA cache_size=-512");
            // One read transaction: the schema, the calls and the steps come from the same snapshot.
            Execute(database, "BEGIN");
            try
            {
                return ReadCalls(database, Path.GetFileNameWithoutExtension(path), token);
            }
            finally
            {
                Native.sqlite3_exec(database, "ROLLBACK", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            if (database != IntPtr.Zero) Native.sqlite3_close_v2(database);
        }
    }

    private static AntigravityReadResult ReadCalls(IntPtr database, string session, CancellationToken token)
    {
        var result = new AntigravityReadResult();
        var schema = InspectSchema(database, token);
        result.IsHistory = schema.HasGenerations;
        if (!schema.GenerationsReadable) return result;

        var proto = new AntigravityProtoReader();
        var complete = true;
        var dated = new List<(long Row, AntigravityProtoReader.Turn Turn, AntigravityDating Dating)>();
        var pending = new List<(long Row, AntigravityProtoReader.Turn Turn)>();
        var occurrences = new Dictionary<CostKey, List<Occurrence>>();
        var botUses = new Dictionary<CostKey, int>();
        using (var rows = Prepare(database, $"""
            SELECT idx, CASE WHEN typeof(data)='blob' AND length(data)<={MaxRecordBytes} THEN data END FROM main.gen_metadata
            """))
        {
            while (Step(rows))
            {
                token.ThrowIfCancellationRequested();
                result.Rows++;
                if (!TryRow(rows, out var row, out var record) || !proto.TryParseTurn(record, out var turn))
                {
                    complete = false;
                    result.InvalidRows++;
                    continue;
                }
                var bot = turn.Usage.BotId;
                if (bot != default) botUses[bot] = botUses.GetValueOrDefault(bot) + 1;
                if (turn.StepId != default)
                {
                    if (!occurrences.TryGetValue(turn.StepId, out var list)) occurrences[turn.StepId] = list = [];
                    list.Add(new(row, turn.TimestampMs, bot));
                }
                if (turn.TimestampMs is null && turn.StepId != default)
                {
                    pending.Add((row, turn));
                    continue;
                }
                if (!turn.HasUsage || turn.TimestampMs is null)
                {
                    // No usage, or no time and no step to find one: never counted, never guessed.
                    complete = false;
                    if (turn.HasUsage) result.Undated++;
                    else result.InvalidRows++;
                    continue;
                }
                dated.Add((row, turn, AntigravityDating.Embedded));
            }
        }

        if (pending.Count > 0)
        {
            // Positional recovery is safe only when every generation took part in the occurrence lists: a malformed
            // row could still carry a reused step id, so a partial first pass must not realign later rows.
            var recovered = complete && schema.StepsReadable
                ? RecoverTimes(database, pending, occurrences, botUses, dated, token)
                : 0;
            result.Undated += pending.Count(p => p.Turn.HasUsage) - recovered;
        }

        BuildCalls(result, session, dated);
        return result;
    }

    /// <summary>
    /// Upstream's step-time recovery: a generation without its own time takes the time of its step
    /// (<c>steps.metadata.#1</c>, joined on the step id). A bot id used by exactly one generation selects its step
    /// exactly; otherwise the generations of one step id take that step's rows in order. Ambiguous or conflicting
    /// evidence leaves a generation undated. Returns how many generations got a time.
    /// </summary>
    private static int RecoverTimes(IntPtr database, List<(long Row, AntigravityProtoReader.Turn Turn)> pending,
        Dictionary<CostKey, List<Occurrence>> occurrences, Dictionary<CostKey, int> botUses,
        List<(long Row, AntigravityProtoReader.Turn Turn, AntigravityDating Dating)> dated, CancellationToken token)
    {
        var needed = new Dictionary<CostKey, List<Occurrence>>();
        foreach (var (_, turn) in pending) needed.TryAdd(turn.StepId, occurrences.GetValueOrDefault(turn.StepId) ?? []);

        var stepTimes = new Dictionary<CostKey, List<Occurrence>>();
        var exact = new Dictionary<CostKey, (CostKey StepId, long TimestampMs)>();
        var ambiguous = new HashSet<CostKey>();
        var stepsValid = true;
        var unidentified = false;
        using (var steps = Prepare(database, $"""
            SELECT idx, CASE WHEN typeof(metadata)='blob' AND length(metadata)<={MaxRecordBytes} THEN metadata END FROM main.steps
            """))
        {
            while (Step(steps))
            {
                token.ThrowIfCancellationRequested();
                if (!TryRow(steps, out var row, out var record) || !AntigravityProtoReader.TryParseStep(record, out var step))
                {
                    stepsValid = false;
                    continue;
                }
                if (step.BotId != default) RecordExactBot(step, exact, ambiguous);
                // Rows without a step id supply no position, but their bot ids still count as evidence (above).
                if (step.StepId == default)
                {
                    unidentified = true;
                    continue;
                }
                if (!needed.ContainsKey(step.StepId)) continue;
                if (!stepTimes.TryGetValue(step.StepId, out var list)) stepTimes[step.StepId] = list = [];
                list.Add(new(row, step.TimestampMs, step.BotId));
            }
        }
        if (!stepsValid || !EmbeddedTimesAgree(needed, exact, ambiguous, botUses)) return 0;
        var resolved = ResolveStepTimes(stepTimes, needed, ambiguous, unidentified);

        var offsets = new Dictionary<CostKey, Dictionary<long, int>>();
        foreach (var (stepId, list) in occurrences)
        {
            var rows = list.Select(o => o.Row).Order().ToArray();
            if (rows.Distinct().Count() != rows.Length) continue;
            var map = new Dictionary<long, int>(rows.Length);
            for (var i = 0; i < rows.Length; i++) map[rows[i]] = i;
            offsets[stepId] = map;
        }

        var recovered = 0;
        foreach (var (row, turn) in pending.OrderBy(p => p.Row))
        {
            var bot = turn.Usage.BotId;
            if (bot != default)
            {
                // Conflicting step times for one bot id: withhold rather than guess.
                if (ambiguous.Contains(bot)) continue;
                if (exact.TryGetValue(bot, out var match))
                {
                    if (match.StepId != turn.StepId) continue;
                    if (botUses.GetValueOrDefault(bot) == 1)
                    {
                        if (Recover(row, turn, match.TimestampMs, AntigravityDating.BotId)) recovered++;
                        continue;
                    }
                }
            }
            if (!resolved.TryGetValue(turn.StepId, out var times) || !offsets.TryGetValue(turn.StepId, out var positions) ||
                !positions.TryGetValue(row, out var offset) || offset >= times.Length) continue;
            if (Recover(row, turn, times[offset], AntigravityDating.Position)) recovered++;
        }
        return recovered;

        bool Recover(long row, AntigravityProtoReader.Turn turn, long timestampMs, AntigravityDating dating)
        {
            if (!turn.HasUsage) return false;
            turn.TimestampMs = timestampMs;
            dated.Add((row, turn, dating));
            return true;
        }
    }

    private static void RecordExactBot(AntigravityProtoReader.Step step,
        Dictionary<CostKey, (CostKey StepId, long TimestampMs)> exact, HashSet<CostKey> ambiguous)
    {
        var bot = step.BotId;
        if (ambiguous.Contains(bot)) return;
        if (step.StepId == default || step.TimestampMs is not { } time)
        {
            exact.Remove(bot);
            ambiguous.Add(bot);
            return;
        }
        if (exact.TryGetValue(bot, out var existing))
        {
            if (existing.TimestampMs == time && existing.StepId == step.StepId) return;
            exact.Remove(bot);
            ambiguous.Add(bot);
            return;
        }
        exact[bot] = (step.StepId, time);
    }

    /// <summary>Generations that carry their own time must agree with what the steps say about their bot ids.</summary>
    private static bool EmbeddedTimesAgree(Dictionary<CostKey, List<Occurrence>> needed,
        Dictionary<CostKey, (CostKey StepId, long TimestampMs)> exact, HashSet<CostKey> ambiguous,
        Dictionary<CostKey, int> botUses)
    {
        foreach (var (stepId, list) in needed)
        {
            foreach (var occurrence in list)
            {
                if (occurrence.TimestampMs is not { } time || occurrence.BotId == default) continue;
                if (ambiguous.Contains(occurrence.BotId)) return false;
                if (exact.TryGetValue(occurrence.BotId, out var match) &&
                    (match.StepId != stepId || botUses.GetValueOrDefault(occurrence.BotId) == 1 && match.TimestampMs != time))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// For each step id: the times of its first steps (by <c>steps.idx</c>), one per generation using it (by
    /// <c>gen_metadata.idx</c>). One timed step stands for every generation only when the census of step ids is
    /// complete. Duplicate indices, missing times or a time that contradicts a generation's own leave the id unresolved.
    /// </summary>
    private static Dictionary<CostKey, long[]> ResolveStepTimes(Dictionary<CostKey, List<Occurrence>> stepTimes,
        Dictionary<CostKey, List<Occurrence>> needed, HashSet<CostKey> ambiguous, bool unidentified)
    {
        var resolved = new Dictionary<CostKey, long[]>();
        foreach (var (stepId, steps) in stepTimes)
        {
            if (!needed.TryGetValue(stepId, out var occurrenceList)) continue;
            var generations = occurrenceList.OrderBy(o => o.Row).ToArray();
            if (generations.Select(o => o.Row).Distinct().Count() != generations.Length) continue;
            var ordered = steps.OrderBy(o => o.Row).ToArray();
            var duplicate = false;
            for (var i = 1; i < ordered.Length; i++) duplicate |= ordered[i].Row == ordered[i - 1].Row;
            if (duplicate) continue;
            // Ambiguous slots keep their place so later times cannot slide into them.
            var times = ordered.Select(o => o.BotId != default && ambiguous.Contains(o.BotId) ? null : o.TimestampMs).ToArray();
            long[] selected;
            if (times.Length == 1 && (generations.Length == 1 || !unidentified) && times[0] is { } shared)
            {
                selected = Enumerable.Repeat(shared, generations.Length).ToArray();
            }
            else
            {
                if (times.Length < generations.Length || times.Take(generations.Length).Any(t => t is null)) continue;
                selected = times.Take(generations.Length).Select(t => t!.Value).ToArray();
            }
            var agrees = true;
            for (var i = 0; i < generations.Length; i++)
                agrees &= generations[i].TimestampMs is not { } own || own == selected[i];
            if (agrees) resolved[stepId] = selected;
        }
        return resolved;
    }

    /// <summary>
    /// Turns the dated generations of one conversation into calls. A generation without a model id inherits the id
    /// that other generations with the same label recorded, unless they disagree. The key is the conversation (the
    /// file name, as upstream) plus the response id, or the row index when there is none, so a copied database adds
    /// nothing and a repeated response counts once.
    /// </summary>
    private static void BuildCalls(AntigravityReadResult result, string session,
        List<(long Row, AntigravityProtoReader.Turn Turn, AntigravityDating Dating)> dated)
    {
        dated.Sort((left, right) => left.Row.CompareTo(right.Row));
        var labels = new Dictionary<CostKey, string>();
        var conflicts = new HashSet<CostKey>();
        foreach (var (_, turn, _) in dated)
        {
            if (turn.Label == default || turn.Model is null) continue;
            if (labels.TryGetValue(turn.Label, out var prior) && prior != turn.Model) conflicts.Add(turn.Label);
            else labels[turn.Label] = turn.Model;
        }

        var sessionKey = CostKey.Hash(Encoding.UTF8.GetBytes("antigravity:" + session));
        var seen = new HashSet<CostKey>();
        foreach (var (row, turn, dating) in dated)
        {
            var model = turn.Model ??
                (turn.Label != default && !conflicts.Contains(turn.Label) ? labels.GetValueOrDefault(turn.Label) : null) ??
                CostPricing.UnattributedModel;
            var key = turn.Usage.ResponseId != default
                ? sessionKey.Combine(turn.Usage.ResponseId)
                : sessionKey.Combine(new(row, -1));
            if (!seen.Add(key)) continue;
            var usage = turn.Usage;
            // Visible and thinking output are separate counts; both bill as output, and thinking is reported within it.
            var tokens = new CostTokens(usage.NewInput, usage.Output + usage.Reasoning, usage.CacheRead, 0, 0, usage.Reasoning);
            if (model == CostPricing.UnattributedModel && tokens.Total == 0) model = ModelLessZeroUsage;
            result.Calls.Add(new(key, turn.TimestampMs!.Value, model, tokens, dating));
        }
    }

    private readonly record struct Occurrence(long Row, long? TimestampMs, CostKey BotId);

    private readonly record struct Schema(bool HasGenerations, bool GenerationsReadable, bool StepsReadable);

    /// <summary>
    /// Upstream's schema check: an ordinary table (not a view or virtual table) whose stored columns include the index
    /// and the payload, and no generated or hidden column. A database without a gen_metadata table is not history.
    /// </summary>
    private static Schema InspectSchema(IntPtr database, CancellationToken token)
    {
        bool generations = false, generationsTable = false, stepsTable = false;
        var entries = 0;
        using (var schema = Prepare(database, $"SELECT name, type, rootpage FROM main.sqlite_master LIMIT {MaxSchemaEntries + 1}"))
        {
            while (Step(schema))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaxSchemaEntries) return new(generations, false, false);
                var name = schema.Text(0);
                var ordinaryTable = schema.Text(1) == "table" && schema.Type(2) == SqliteInteger && schema.Number(2) > 0;
                if (string.Equals(name, "gen_metadata", StringComparison.OrdinalIgnoreCase))
                {
                    generations = true;
                    generationsTable = ordinaryTable;
                }
                else if (string.Equals(name, "steps", StringComparison.OrdinalIgnoreCase)) stepsTable = ordinaryTable;
            }
        }
        return new(generations,
            generationsTable && HasStoredColumns(database, "gen_metadata", "data", token),
            stepsTable && HasStoredColumns(database, "steps", "metadata", token));
    }

    private static bool HasStoredColumns(IntPtr database, string table, string payload, CancellationToken token)
    {
        bool index = false, data = false;
        var count = 0;
        using var columns = Prepare(database, $"PRAGMA main.table_xinfo('{table}')");
        // table_info leaves generated columns out; a SQLite without table_xinfo returns nothing and fails closed.
        while (Step(columns))
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaxSchemaColumns || columns.Type(6) != SqliteInteger || columns.Number(6) != 0) return false;
            var name = columns.Text(1);
            index |= string.Equals(name, "idx", StringComparison.OrdinalIgnoreCase);
            data |= string.Equals(name, payload, StringComparison.OrdinalIgnoreCase);
        }
        return index && data;
    }

    /// <summary>A row of (idx, payload): a non-negative integer index and a non-empty BLOB within the record bound.</summary>
    private static unsafe bool TryRow(Statement statement, out long row, out ReadOnlySpan<byte> record)
    {
        row = 0;
        record = default;
        if (statement.Type(0) != SqliteInteger || (row = statement.Number(0)) < 0 || statement.Type(1) != SqliteBlob)
            return false;
        var bytes = statement.Blob(1, out var length);
        if (bytes == IntPtr.Zero || length <= 0) return false;
        // The span lives only until the next step, which is all the parsers need.
        record = new ReadOnlySpan<byte>((void*)bytes, length);
        return true;
    }

    /// <summary>
    /// The SQLite URI for a read-only open with <paramref name="query"/>. The path is escaped where a URI needs it;
    /// "file:///C:/..." is SQLite's form for a Windows drive path.
    /// </summary>
    internal static string Uri(string path, string query)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var uri = new StringBuilder("file:", full.Length + query.Length + 16);
        uri.Append(full.StartsWith("//", StringComparison.Ordinal) ? "//" : "///");
        foreach (var c in full)
        {
            switch (c)
            {
                case '%': uri.Append("%25"); break;
                case '?': uri.Append("%3f"); break;
                case '#': uri.Append("%23"); break;
                case ' ': uri.Append("%20"); break;
                default: uri.Append(c); break;
            }
        }
        return uri.Append('?').Append(query).ToString();
    }

    private static AntigravityDatabaseUnavailableException Unavailable(int status) =>
        new($"The database could not be read (SQLite {status & 0xFF}).");

    private static void Execute(IntPtr database, string sql)
    {
        var status = Native.sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (status != 0) throw Unavailable(status);
    }

    private static Statement Prepare(IntPtr database, string sql)
    {
        var status = Native.sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero);
        if (status != 0)
        {
            if (statement != IntPtr.Zero) Native.sqlite3_finalize(statement);
            throw Unavailable(status);
        }
        return new(statement);
    }

    private static bool Step(Statement statement)
    {
        var status = Native.sqlite3_step(statement.Handle);
        if (status == 100) return true;
        if (status == 101) return false;
        throw Unavailable(status);
    }

    private const int OpenReadOnly = 0x01;
    private const int OpenUri = 0x40;
    private const int NoCheckpointOnClose = 1006;
    private const int SqliteInteger = 1;
    private const int SqliteBlob = 4;

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        internal IntPtr Handle { get; private set; } = handle;
        internal int Type(int column) => Native.sqlite3_column_type(Handle, column);
        internal long Number(int column) => Native.sqlite3_column_int64(Handle, column);
        internal string Text(int column) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(Handle, column)) ?? "";

        internal IntPtr Blob(int column, out int length)
        {
            // Read the pointer before the size, as SQLite documents.
            var pointer = Native.sqlite3_column_blob(Handle, column);
            length = Native.sqlite3_column_bytes(Handle, column);
            return pointer;
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            Native.sqlite3_finalize(Handle);
            Handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The identity of a database at rest: no <c>-wal</c>, and the main file's size, write time, file id and 100-byte
    /// header (upstream's SQLiteIdleDatabaseState). Null while a <c>-wal</c> exists or the file cannot be examined.
    /// </summary>
    private readonly record struct IdleState(long Length, long WriteTime, CostKey FileId, CostKey Header)
    {
        internal static IdleState? Read(string path)
        {
            if (File.Exists(path + "-wal")) return null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) return null;
                Span<byte> header = stackalloc byte[100];
                var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                return new(((long)info.SizeHigh << 32) | info.SizeLow, ((long)info.WriteHigh << 32) | info.WriteLow,
                    new(info.Volume, ((long)info.IndexHigh << 32) | info.IndexLow), CostKey.Hash(header[..read]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <summary>The Windows SQLite library (winsqlite3.dll), as the cost index uses it.</summary>
    private static class Native
    {
        private const string Library = "winsqlite3.dll";

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_close_v2(IntPtr db);
        // Variadic, so cdecl on x86; the two extra arguments are an int and an int pointer.
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_db_config(IntPtr db, int op, int value, IntPtr result);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, IntPtr error);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int index);
    }
}
