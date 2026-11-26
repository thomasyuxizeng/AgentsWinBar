using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentsWinBar.Services.Native.Cost;

internal readonly record struct CostKey(long First, long Second)
{
    internal static CostKey Hash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return new(BinaryPrimitives.ReadInt64LittleEndian(digest), BinaryPrimitives.ReadInt64LittleEndian(digest[8..]));
    }

    internal CostKey Combine(CostKey other)
    {
        Span<byte> input = stackalloc byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(input, First);
        BinaryPrimitives.WriteInt64LittleEndian(input[8..], Second);
        BinaryPrimitives.WriteInt64LittleEndian(input[16..], other.First);
        BinaryPrimitives.WriteInt64LittleEndian(input[24..], other.Second);
        return Hash(input);
    }
}

internal readonly record struct CostEvent(CostKey Key, DateTimeOffset Time, string Model,
    CostTokens Tokens, int Tier, int Rank, bool Incomplete);

internal struct CodexCursor
{
    internal string? Model;
    internal int Tier;
    internal CostTokens? Total;
    internal bool HasRequest;
    internal CostKey Thread;
}

internal sealed class CostLogParser(CostProvider provider, bool subagent, CodexCursor cursor)
{
    private const int ModelCacheCapacity = 8;
    private readonly (byte[] RawUtf8, string Canonical)[] _modelCache = new (byte[], string)[ModelCacheCapacity];
    private int _modelCacheCount;
    private int _modelCacheNextWrite;
    internal CodexCursor Cursor = cursor;
    internal long BadLines { get; private set; }

    internal CostEvent? Parse(ReadOnlySpan<byte> line)
    {
        // Reject ordinary prompt/tool lines using ASCII markers before running the JSON reader.
        if (provider == CostProvider.Claude)
        {
            if (line.IndexOf("\"assistant\""u8) < 0 || line.IndexOf("\"usage\""u8) < 0) return null;
        }
        else if (line.IndexOf("\"token_count\""u8) < 0 && line.IndexOf("\"turn_context\""u8) < 0 &&
                 line.IndexOf("\"token_usage_record\""u8) < 0 && line.IndexOf("\"thread_settings_applied\""u8) < 0 &&
                 line.IndexOf("\"session_meta\""u8) < 0)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 256 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            var record = new Record();
            while (ReadField(ref reader, out var name))
            {
                if (name.SequenceEqual("type"u8)) record.Type = ReadType(ref reader);
                else if (name.SequenceEqual("timestamp"u8) && reader.TokenType == JsonTokenType.String)
                    reader.TryGetDateTimeOffset(out record.Time);
                else if (name.SequenceEqual("model"u8)) record.Model = Model(ref reader);
                else if (provider == CostProvider.Claude)
                {
                    if (name.SequenceEqual("message"u8)) ReadMessage(ref reader, ref record);
                    else if (name.SequenceEqual("requestId"u8) || name.SequenceEqual("request_id"u8))
                        record.Request = Id(ref reader);
                    else if (name.SequenceEqual("sessionId"u8) || name.SequenceEqual("session_id"u8))
                        record.Session = Id(ref reader);
                    else if (name.SequenceEqual("isSidechain"u8)) record.Sidechain = reader.TokenType == JsonTokenType.True;
                    else if (name.SequenceEqual("metadata"u8)) ReadSessionMetadata(ref reader, ref record);
                }
                else if (name.SequenceEqual("payload"u8)) ReadPayload(ref reader, ref record);
                reader.Skip();
            }
            if (reader.Read()) return null;

            return provider == CostProvider.Claude ? ClaudeEvent(record) : CodexEvent(record);
        }
        catch (JsonException)
        {
            BadLines++;
            return null;
        }
    }

    private CostEvent? ClaudeEvent(Record record)
    {
        if (record.Type != RecordType.Assistant || record.Time == default || record.Usage is not { } tokens ||
            tokens.Total == 0 || record.Model is null) return null;
        var key = record.Message != default && (record.Request != default || record.Session != default)
            ? record.Message.Combine(record.Request != default ? record.Request : record.Session.Combine(new(1, 0)))
            : default;
        var incomplete = record.NullStop && tokens.Input > 0 && tokens.Output == 0 && !record.HasCacheFields;
        var rank = (incomplete ? 0 : 4) + (record.Sidechain ? 0 : 2) + (subagent ? 0 : 1);
        return new(key, record.Time, record.Model, incomplete ? default : tokens, 0, rank, incomplete);
    }

    private CostEvent? CodexEvent(Record record)
    {
        switch (record.Type)
        {
            case RecordType.Session:
                Cursor.Thread = record.Session;
                return null;
            case RecordType.Context:
                if (record.Model is not null) Cursor.Model = record.Model;
                return null;
            case RecordType.Event when record.PayloadType == RecordType.Settings:
                Cursor.Tier = record.Tier;
                return null;
        }

        if (record.Time == default) return null;
        CostTokens tokens;
        CostKey key;
        if (record.Type == RecordType.Request && record.Request != default &&
            record.Thread != default && (Cursor.Thread == default || Cursor.Thread == record.Thread) &&
            record.Usage is { } request && record.Total is { } requestTotal && requestTotal.Contains(request))
        {
            tokens = request;
            key = record.Thread.Combine(record.Request);
            Cursor.HasRequest = true;
            Cursor.Total = requestTotal;
        }
        else if (record.Type == RecordType.Event && record.PayloadType == RecordType.Count)
        {
            if (record.Total is { } total)
            {
                if (Cursor.Total is { } previous)
                {
                    // The request and its immediately following legacy mirror describe the same usage.
                    if (!total.Contains(previous) || SameLegacyCounters(total, previous)) return null;
                    var delta = total.Subtract(previous);
                    tokens = record.Last is { } last && !last.Contains(delta) ? last : delta;
                }
                else
                {
                    tokens = record.Last ?? total;
                }
                Cursor.Total = total;
            }
            else
            {
                if (record.Last is not { } last || Cursor.HasRequest) return null;
                tokens = last;
                Cursor.Total = (Cursor.Total ?? default).Add(last);
            }
            key = Cursor.Thread == default ? default : Cursor.Thread.Combine(
                new(record.Time.UtcTicks, record.Total?.Total ?? tokens.Total));
        }
        else return null;

        var model = record.Type == RecordType.Request ? record.Model ?? Cursor.Model : Cursor.Model ?? record.Model;
        return tokens.Total == 0 ? null : new(key, record.Time, model ?? "unknown", tokens.Uncached(),
            Cursor.Tier, 0, false);
    }

    private static bool SameLegacyCounters(CostTokens left, CostTokens right) =>
        left.Input == right.Input && left.Output == right.Output && left.Read == right.Read;

    private static bool ReadField(ref Utf8JsonReader reader, out ReadOnlySpan<byte> name)
    {
        name = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName) return false;
        name = reader.ValueSpan;
        return reader.Read();
    }

    private static RecordType ReadType(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String) return RecordType.Unknown;
        if (reader.ValueTextEquals("assistant"u8)) return RecordType.Assistant;
        if (reader.ValueTextEquals("event_msg"u8)) return RecordType.Event;
        if (reader.ValueTextEquals("token_count"u8)) return RecordType.Count;
        if (reader.ValueTextEquals("token_usage_record"u8)) return RecordType.Request;
        if (reader.ValueTextEquals("turn_context"u8)) return RecordType.Context;
        if (reader.ValueTextEquals("session_meta"u8)) return RecordType.Session;
        if (reader.ValueTextEquals("thread_settings_applied"u8)) return RecordType.Settings;
        return RecordType.Unknown;
    }

    private static CostKey Id(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String && !reader.ValueSpan.IsEmpty
            ? CostKey.Hash(reader.ValueSpan) : default;

    private string? Model(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 256) return null;
        for (var i = 0; i < _modelCacheCount; i++)
        {
            if (reader.ValueTextEquals(_modelCache[i].RawUtf8))
            {
                return _modelCache[i].Canonical;
            }
        }
        var raw = reader.GetString() ?? "";
        var canonical = CostPricing.Normalize(raw, provider);
        _modelCache[_modelCacheNextWrite] = (Encoding.UTF8.GetBytes(raw), canonical);
        if (_modelCacheCount < ModelCacheCapacity) _modelCacheCount++;
        _modelCacheNextWrite = (_modelCacheNextWrite + 1) % ModelCacheCapacity;
        return canonical;
    }

    private static void ReadSessionMetadata(ref Utf8JsonReader reader, ref Record record)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;
        while (ReadField(ref reader, out var name))
        {
            if (name.SequenceEqual("sessionId"u8) && record.Session == default) record.Session = Id(ref reader);
            reader.Skip();
        }
    }

    private void ReadMessage(ref Utf8JsonReader reader, ref Record record)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;
        while (ReadField(ref reader, out var name))
        {
            if (name.SequenceEqual("id"u8)) record.Message = Id(ref reader);
            else if (name.SequenceEqual("model"u8)) record.Model = Model(ref reader);
            else if (name.SequenceEqual("stop_reason"u8)) record.NullStop = reader.TokenType == JsonTokenType.Null;
            else if (name.SequenceEqual("metadata"u8)) ReadSessionMetadata(ref reader, ref record);
            else if (name.SequenceEqual("usage"u8))
                record.Usage = ReadTokens(ref reader, out record.HasCacheFields);
            reader.Skip();
        }
    }

    private void ReadPayload(ref Utf8JsonReader reader, ref Record record)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;
        while (ReadField(ref reader, out var name))
        {
            if (name.SequenceEqual("id"u8)) record.Session = Id(ref reader);
            else if (name.SequenceEqual("thread_id"u8)) record.Thread = Id(ref reader);
            else if (name.SequenceEqual("response_id"u8)) record.Request = Id(ref reader);
            else if (name.SequenceEqual("type"u8)) record.PayloadType = ReadType(ref reader);
            else if (name.SequenceEqual("model"u8) || name.SequenceEqual("model_name"u8))
                record.Model = Model(ref reader) ?? record.Model;
            else if (name.SequenceEqual("usage"u8)) record.Usage = ReadTokens(ref reader, out _);
            else if (name.SequenceEqual("thread_token_usage"u8)) record.Total = ReadTokens(ref reader, out _);
            else if (name.SequenceEqual("info"u8)) ReadInfo(ref reader, ref record);
            else if (name.SequenceEqual("thread_settings"u8)) record.Tier = ReadTier(ref reader);
            reader.Skip();
        }
    }

    private void ReadInfo(ref Utf8JsonReader reader, ref Record record)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;
        while (ReadField(ref reader, out var name))
        {
            if (name.SequenceEqual("total_token_usage"u8)) record.Total = ReadTokens(ref reader, out _);
            else if (name.SequenceEqual("last_token_usage"u8)) record.Last = ReadTokens(ref reader, out _);
            else if (name.SequenceEqual("model"u8) || name.SequenceEqual("model_name"u8))
                record.Model = Model(ref reader) ?? record.Model;
            reader.Skip();
        }
    }

    private static int ReadTier(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return 0;
        var tier = 0;
        while (ReadField(ref reader, out var name))
        {
            if (name.SequenceEqual("service_tier"u8) && reader.TokenType == JsonTokenType.String)
                tier = reader.ValueTextEquals("priority"u8) ? 1 : reader.ValueTextEquals("ultrafast"u8) ? 2 : 0;
            reader.Skip();
        }
        return tier;
    }

    private static CostTokens? ReadTokens(ref Utf8JsonReader reader, out bool hasCacheFields)
    {
        hasCacheFields = false;
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        long input = 0, output = 0, read = 0, write = 0, oneHour = 0, reasoning = 0;
        while (ReadField(ref reader, out var name))
        {
            var count = Number(ref reader);
            if (name.SequenceEqual("input_tokens"u8)) input = count;
            else if (name.SequenceEqual("output_tokens"u8)) output = count;
            else if (name.SequenceEqual("cache_read_input_tokens"u8) || name.SequenceEqual("cached_input_tokens"u8))
            {
                read = Math.Max(read, count);
                hasCacheFields = true;
            }
            else if (name.SequenceEqual("cache_creation_input_tokens"u8) || name.SequenceEqual("cache_write_input_tokens"u8))
            {
                write = count;
                hasCacheFields = true;
            }
            else if (name.SequenceEqual("reasoning_output_tokens"u8)) reasoning = count;
            else if (name.SequenceEqual("cache_creation"u8) && reader.TokenType == JsonTokenType.StartObject)
            {
                while (ReadField(ref reader, out var bucket))
                {
                    if (bucket.SequenceEqual("ephemeral_1h_input_tokens"u8)) oneHour = Number(ref reader);
                    reader.Skip();
                }
            }
            reader.Skip();
        }
        return new(input, output, read, write, Math.Min(oneHour, write), Math.Min(reasoning, output));
    }

    private static long Number(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value) && value is > 0 and <= int.MaxValue
            ? value : 0;

    private enum RecordType { Unknown, Assistant, Event, Count, Request, Context, Session, Settings }

    private struct Record
    {
        internal DateTimeOffset Time;
        internal RecordType Type, PayloadType;
        internal string? Model;
        internal CostKey Request, Message, Session, Thread;
        internal CostTokens? Usage, Last, Total;
        internal bool NullStop, HasCacheFields, Sidechain;
        internal int Tier;
    }
}
