namespace CodexBarWin.Services.Native.Cost;

internal sealed record IndexedFile(long Length, long WriteTime, long Created, long Offset, long Generation,
    CostKey FileId, CodexCursor Cursor);

internal readonly record struct IndexedDay(int Day, string Model, int Band, CostTokens Tokens, long Count);

internal sealed class CostIndex(CostIndexDatabase database) : IDisposable
{
    internal const int SchemaVersion = 2;

    // The cursor, canonical usage rows and their aggregate changes commit together.
    internal const string Schema = """
        BEGIN IMMEDIATE;
        CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY, zone TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS files(
            provider INTEGER, path1 INTEGER, path2 INTEGER,
            length INTEGER, modified INTEGER, created INTEGER, offset INTEGER, generation INTEGER,
            volume INTEGER, file_id INTEGER, model TEXT, tier INTEGER, has_total INTEGER,
            input INTEGER, output INTEGER, read INTEGER, written INTEGER, reasoning INTEGER,
            has_request INTEGER, thread1 INTEGER, thread2 INTEGER,
            PRIMARY KEY(provider,path1,path2)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS usage(
            provider INTEGER, key1 INTEGER, key2 INTEGER, path1 INTEGER, path2 INTEGER,
            rank INTEGER, incomplete INTEGER, day INTEGER, model TEXT, band INTEGER,
            input INTEGER, output INTEGER, read INTEGER, written INTEGER, one_hour INTEGER, reasoning INTEGER,
            utc_seconds INTEGER NOT NULL, offset_generation INTEGER,
            PRIMARY KEY(provider,key1,key2)
        ) WITHOUT ROWID;
        CREATE INDEX usage_by_generation ON usage(provider,path1,path2,offset_generation)
            WHERE offset_generation IS NOT NULL;
        CREATE TABLE IF NOT EXISTS days(
            provider INTEGER, day INTEGER, model TEXT, band INTEGER, count INTEGER,
            input INTEGER, output INTEGER, read INTEGER, written INTEGER, one_hour INTEGER, reasoning INTEGER,
            PRIMARY KEY(provider,day,model,band)
        ) WITHOUT ROWID;
        CREATE TRIGGER IF NOT EXISTS usage_add AFTER INSERT ON usage WHEN new.incomplete=0 BEGIN
            INSERT INTO days VALUES (new.provider,new.day,new.model,new.band,1,
                new.input,new.output,new.read,new.written,new.one_hour,new.reasoning)
            ON CONFLICT(provider,day,model,band) DO UPDATE SET
                count=count+1,input=input+new.input,output=output+new.output,read=read+new.read,
                written=written+new.written,one_hour=one_hour+new.one_hour,reasoning=reasoning+new.reasoning;
        END;
        CREATE TRIGGER usage_delete AFTER DELETE ON usage WHEN old.incomplete=0 BEGIN
            UPDATE days SET count=count-1,input=input-old.input,output=output-old.output,read=read-old.read,
                written=written-old.written,one_hour=one_hour-old.one_hour,reasoning=reasoning-old.reasoning
            WHERE provider=old.provider AND day=old.day AND model=old.model AND band=old.band;
            DELETE FROM days
            WHERE provider=old.provider AND day=old.day AND model=old.model AND band=old.band AND count=0;
        END;
        CREATE TRIGGER IF NOT EXISTS usage_remove BEFORE UPDATE OF incomplete ON usage WHEN old.incomplete=0 BEGIN
            UPDATE days SET count=count-1,input=input-old.input,output=output-old.output,read=read-old.read,
                written=written-old.written,one_hour=one_hour-old.one_hour,reasoning=reasoning-old.reasoning
            WHERE provider=old.provider AND day=old.day AND model=old.model AND band=old.band;
        END;
        CREATE TRIGGER IF NOT EXISTS usage_replace AFTER UPDATE OF incomplete ON usage WHEN new.incomplete=0 BEGIN
            INSERT INTO days VALUES (new.provider,new.day,new.model,new.band,1,
                new.input,new.output,new.read,new.written,new.one_hour,new.reasoning)
            ON CONFLICT(provider,day,model,band) DO UPDATE SET
                count=count+1,input=input+new.input,output=output+new.output,read=read+new.read,
                written=written+new.written,one_hour=one_hour+new.one_hour,reasoning=reasoning+new.reasoning;
        END;
        PRAGMA user_version=2;
        COMMIT;
        """;

    private CostIndexDatabase.Statement? _save;
    private CostIndexDatabase.Statement? _add;
    private CostIndexDatabase.Statement? _remove;

    internal static long ReadVersion(CostIndexDatabase database)
    {
        using var row = database.Prepare("PRAGMA user_version");
        return row.Step() ? row.Number(0) : 0;
    }

    internal string ReadZone()
    {
        using var row = database.Prepare("SELECT zone FROM settings WHERE id=1");
        return row.Step() ? row.Text(0) : "";
    }

    internal void SetZone(TimeZoneInfo zone, CancellationToken token)
    {
        var previous = ReadZone();
        if (previous == zone.Id) return;
        if (previous.Length > 0)
        {
            database.Execute("CREATE TEMP TABLE local_days(utc_seconds INTEGER PRIMARY KEY, day INTEGER NOT NULL)");
            using (var times = database.Prepare("SELECT DISTINCT utc_seconds FROM usage"))
            using (var map = database.Prepare("INSERT INTO local_days VALUES (?,?)"))
            {
                while (times.Step())
                {
                    token.ThrowIfCancellationRequested();
                    var seconds = times.Number(0);
                    map.Bind(1, seconds);
                    map.Bind(2, LocalDay(DateTimeOffset.FromUnixTimeSeconds(seconds), zone).DayNumber);
                    map.Write();
                }
            }

            // Day-only updates bypass the upsert triggers. Rebuild the aggregates in one pass.
            database.Execute("""
                UPDATE usage SET day=(SELECT day FROM local_days WHERE local_days.utc_seconds=usage.utc_seconds);
                DROP TABLE local_days;
                DELETE FROM days;
                INSERT INTO days
                SELECT provider,day,model,band,COUNT(*),SUM(input),SUM(output),SUM(read),
                    SUM(written),SUM(one_hour),SUM(reasoning)
                FROM usage WHERE incomplete=0 GROUP BY provider,day,model,band;
                """);
        }
        token.ThrowIfCancellationRequested();
        using var update = database.Prepare("INSERT OR REPLACE INTO settings VALUES (1, ?)");
        update.Bind(1, zone.Id);
        update.Write();
    }

    internal static DateOnly LocalDay(DateTimeOffset time, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime);

    internal void RemoveOffsetUsage(CostProvider provider, CostKey path, long generation)
    {
        var row = _remove ??= database.Prepare("""
            DELETE FROM usage WHERE provider=? AND path1=? AND path2=? AND offset_generation=?
            """);
        row.Bind(1, (int)provider);
        row.Bind(2, path.First);
        row.Bind(3, path.Second);
        row.Bind(4, generation);
        row.Write();
    }

    internal Dictionary<(CostProvider, CostKey), IndexedFile> ReadFiles()
    {
        using var row = database.Prepare("SELECT * FROM files");
        var files = new Dictionary<(CostProvider, CostKey), IndexedFile>();
        while (row.Step())
        {
            var total = new CostTokens(row.Number(13), row.Number(14), row.Number(15), row.Number(16), 0, row.Number(17));
            var model = row.Text(10);
            files.Add(((CostProvider)row.Number(0), new(row.Number(1), row.Number(2))),
                new(row.Number(3), row.Number(4), row.Number(5), row.Number(6), row.Number(7),
                    new(row.Number(8), row.Number(9)), new CodexCursor
                    {
                        Model = model.Length == 0 ? null : model,
                        Tier = (int)row.Number(11),
                        Total = row.Number(12) != 0 ? total : null,
                        HasRequest = row.Number(18) != 0,
                        Thread = new(row.Number(19), row.Number(20))
                    }));
        }
        return files;
    }

    internal void Save(CostProvider provider, CostKey path, IndexedFile file)
    {
        var row = _save ??= database.Prepare("""
            INSERT OR REPLACE INTO files VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """);
        row.Bind(1, (int)provider);
        row.Bind(2, path.First);
        row.Bind(3, path.Second);
        row.Bind(4, file.Length);
        row.Bind(5, file.WriteTime);
        row.Bind(6, file.Created);
        row.Bind(7, file.Offset);
        row.Bind(8, file.Generation);
        row.Bind(9, file.FileId.First);
        row.Bind(10, file.FileId.Second);
        row.Bind(11, file.Cursor.Model ?? "");
        row.Bind(12, file.Cursor.Tier);
        row.Bind(13, file.Cursor.Total.HasValue ? 1 : 0);
        var total = file.Cursor.Total ?? default;
        row.Bind(14, total.Input);
        row.Bind(15, total.Output);
        row.Bind(16, total.Read);
        row.Bind(17, total.Write);
        row.Bind(18, total.Reasoning);
        row.Bind(19, file.Cursor.HasRequest ? 1 : 0);
        row.Bind(20, file.Cursor.Thread.First);
        row.Bind(21, file.Cursor.Thread.Second);
        row.Write();
    }

    internal void Add(CostProvider provider, CostKey path, long generation, long offset, DateOnly day, CostEvent evt)
    {
        var row = _add ??= database.Prepare("""
            INSERT INTO usage VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(provider,key1,key2) DO UPDATE SET
                path1=excluded.path1,path2=excluded.path2,rank=excluded.rank,incomplete=excluded.incomplete,
                day=excluded.day,model=excluded.model,band=excluded.band,input=excluded.input,output=excluded.output,
                read=excluded.read,written=excluded.written,one_hour=excluded.one_hour,reasoning=excluded.reasoning,
                utc_seconds=excluded.utc_seconds,offset_generation=excluded.offset_generation
            WHERE excluded.provider=0 AND (
                (excluded.path1=usage.path1 AND excluded.path2=usage.path2 AND excluded.incomplete<=usage.incomplete)
                OR excluded.rank>usage.rank
                OR (excluded.rank=usage.rank AND (excluded.path1,excluded.path2)<(usage.path1,usage.path2)))
            """);
        var count = evt.Tokens;
        var seconds = evt.Time.ToUnixTimeSeconds();
        var key = evt.Key == default ? path.Combine(new(generation, offset)) : evt.Key;
        var band = (count.Input + count.Read + count.Write > 200_000 ? 1 : 0) |
                   (count.Input + count.Read + count.Write > 272_000 ? 2 : 0) |
                   (CostPricing.Historical(evt.Model, seconds) ? 4 : 0) | (evt.Tier << 3);

        row.Bind(1, (int)provider);
        row.Bind(2, key.First);
        row.Bind(3, key.Second);
        row.Bind(4, path.First);
        row.Bind(5, path.Second);
        row.Bind(6, evt.Rank);
        row.Bind(7, evt.Incomplete ? 1 : 0);
        row.Bind(8, day.DayNumber);
        row.Bind(9, evt.Model);
        row.Bind(10, band);
        row.Bind(11, count.Input);
        row.Bind(12, count.Output);
        row.Bind(13, count.Read);
        row.Bind(14, count.Write);
        row.Bind(15, count.OneHour);
        row.Bind(16, count.Reasoning);
        row.Bind(17, seconds);
        if (evt.Key == default) row.Bind(18, generation);
        else row.BindNull(18);
        row.Write();
    }

    internal List<IndexedDay> ReadDays(CostProvider provider, int first, int last, CancellationToken token)
    {
        using var row = database.Prepare("""
            SELECT day,model,band,count,input,output,read,written,one_hour,reasoning
            FROM days WHERE provider=? AND day BETWEEN ? AND ? AND count>0 ORDER BY day,model,band
            """);
        row.Bind(1, (int)provider);
        row.Bind(2, first);
        row.Bind(3, last);
        var days = new List<IndexedDay>();
        while (row.Step())
        {
            token.ThrowIfCancellationRequested();
            days.Add(new((int)row.Number(0), row.Text(1), (int)row.Number(2),
                new(row.Number(4), row.Number(5), row.Number(6), row.Number(7), row.Number(8), row.Number(9)),
                row.Number(3)));
        }
        return days;
    }

    public void Dispose()
    {
        _save?.Dispose();
        _add?.Dispose();
        _remove?.Dispose();
    }
}
