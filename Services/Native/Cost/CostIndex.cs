namespace CodexBarWin.Services.Native.Cost;

internal sealed record IndexedFile(long Length, long WriteTime, long Created, long Offset, long Generation,
    CostKey FileId, CodexCursor Cursor);

internal readonly record struct IndexedDay(int Day, string Model, int Band, CostTokens Tokens, long Count);

internal sealed class CostIndex(CostIndexDatabase database) : IDisposable
{
    // The cursor, canonical usage rows and their aggregate changes commit together. Rotation
    // retains observations already recorded, including observations of files that disappear.
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
            PRIMARY KEY(provider,key1,key2)
        ) WITHOUT ROWID;
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
        CREATE TRIGGER IF NOT EXISTS usage_remove BEFORE UPDATE ON usage WHEN old.incomplete=0 BEGIN
            UPDATE days SET count=count-1,input=input-old.input,output=output-old.output,read=read-old.read,
                written=written-old.written,one_hour=one_hour-old.one_hour,reasoning=reasoning-old.reasoning
            WHERE provider=old.provider AND day=old.day AND model=old.model AND band=old.band;
        END;
        CREATE TRIGGER IF NOT EXISTS usage_replace AFTER UPDATE ON usage WHEN new.incomplete=0 BEGIN
            INSERT INTO days VALUES (new.provider,new.day,new.model,new.band,1,
                new.input,new.output,new.read,new.written,new.one_hour,new.reasoning)
            ON CONFLICT(provider,day,model,band) DO UPDATE SET
                count=count+1,input=input+new.input,output=output+new.output,read=read+new.read,
                written=written+new.written,one_hour=one_hour+new.one_hour,reasoning=reasoning+new.reasoning;
        END;
        PRAGMA user_version=1;
        COMMIT;
        """;

    private CostIndexDatabase.Statement? _save;
    private CostIndexDatabase.Statement? _add;

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

    internal void Add(CostProvider provider, CostKey path, CostKey key, DateOnly day, CostEvent evt)
    {
        var row = _add ??= database.Prepare("""
            INSERT INTO usage VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(provider,key1,key2) DO UPDATE SET
                path1=excluded.path1,path2=excluded.path2,rank=excluded.rank,incomplete=excluded.incomplete,
                day=excluded.day,model=excluded.model,band=excluded.band,input=excluded.input,output=excluded.output,
                read=excluded.read,written=excluded.written,one_hour=excluded.one_hour,reasoning=excluded.reasoning
            WHERE excluded.provider=0 AND (
                (excluded.path1=usage.path1 AND excluded.path2=usage.path2 AND excluded.incomplete<=usage.incomplete)
                OR excluded.rank>usage.rank
                OR (excluded.rank=usage.rank AND (excluded.path1,excluded.path2)<(usage.path1,usage.path2)))
            """);
        var count = evt.Tokens;
        var band = (count.Input + count.Read + count.Write > 200_000 ? 1 : 0) |
                   (count.Input + count.Read + count.Write > 272_000 ? 2 : 0) |
                   (CostPricing.Historical(evt.Model, evt.Time.ToUnixTimeSeconds()) ? 4 : 0) | (evt.Tier << 3);

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
    }
}
