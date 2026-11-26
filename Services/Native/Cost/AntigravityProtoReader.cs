using System.Text;
using System.Text.Unicode;

namespace AgentsWinBar.Services.Native.Cost;

/// <summary>
/// Port of CodexBar's AntigravityProtoReader (Sources/CodexBarCore/Providers/Antigravity/AntigravityProtoReader.swift
/// at a26ca57, MIT, Peter Steinberger). A protobuf wire-format reader for the few fields of an Antigravity (agy)
/// conversation database that carry token counts, model ids, row identities and times:
/// <list type="bullet">
/// <item><c>gen_metadata.data</c>: <c>#1</c> chat model, with <c>#1.#4</c> usage (<c>#2</c> uncached input,
/// <c>#5</c> cache read, <c>#9</c> thinking output, <c>#10</c> visible output, <c>#7</c> bot id, <c>#11</c> response id),
/// <c>#1.#9.#4</c> generation time, <c>#1.#19</c> model id and <c>#1.#21</c> model label; root <c>#4</c> step id.</item>
/// <item><c>steps.metadata</c>: <c>#1</c> creation time, <c>#9.#7</c> bot id and <c>#12</c> step id.</item>
/// </list>
/// Every other field is skipped by its length without being decoded, so no prompt, response or tool payload is ever
/// read into text. Ids are kept only as hashes. Repeated singular messages merge and scalars keep the last value, and
/// every occurrence is validated: one malformed envelope makes the whole record invalid, as upstream decides.
/// </summary>
internal sealed class AntigravityProtoReader
{
    /// <summary>The latest time a protobuf Timestamp can hold (9999-12-31T23:59:59Z).</summary>
    private const ulong MaxSeconds = 253_402_300_799;

    internal struct Usage
    {
        /// <summary>Field 2: input tokens that were not read from the cache.</summary>
        internal long NewInput;
        /// <summary>Field 5: input tokens read from the cache.</summary>
        internal long CacheRead;
        /// <summary>Field 10: visible output tokens.</summary>
        internal long Output;
        /// <summary>Field 9: thinking tokens, billed as output.</summary>
        internal long Reasoning;
        /// <summary>Field 11: the response id (hashed), or default when absent.</summary>
        internal CostKey ResponseId;
        /// <summary>Field 7: the generation's bot id (hashed), or default when absent or malformed.</summary>
        internal CostKey BotId;
        internal bool BotIdInvalid;
    }

    internal struct Turn
    {
        internal bool HasUsage;
        internal Usage Usage;
        /// <summary>Milliseconds since the Unix epoch from <c>#1.#9.#4</c>, or null when the row carries no time.</summary>
        internal long? TimestampMs;
        /// <summary>The recorded model id, normalized for the index, or null when the row names none.</summary>
        internal string? Model;
        /// <summary>The model label (hashed): only a join key between rows of one conversation.</summary>
        internal CostKey Label;
        /// <summary>The root step id (hashed) that joins this generation to <c>steps.metadata.#12</c>.</summary>
        internal CostKey StepId;
    }

    internal struct Step
    {
        internal CostKey StepId;
        internal CostKey BotId;
        internal long? TimestampMs;
    }

    private const int ModelCacheCapacity = 8;
    private readonly (byte[] Raw, string Model)[] _models = new (byte[], string)[ModelCacheCapacity];
    private int _modelCount;
    private int _modelNext;

    /// <summary>
    /// Decodes one <c>gen_metadata.data</c> record. False when the record is malformed or has no chat model message.
    /// </summary>
    internal bool TryParseTurn(ReadOnlySpan<byte> record, out Turn turn)
    {
        turn = default;
        ulong? seconds = null;
        ulong nanos = 0;
        var foundChat = false;
        var root = new Reader(record);
        while (root.Next(out var field))
        {
            switch (field.Number)
            {
                case 1:
                    if (field.Wire != 2 || !ParseChat(field.Data, ref turn, ref seconds, ref nanos)) return false;
                    foundChat = true;
                    break;
                case 4:
                    if (!ReadId(field, out turn.StepId)) return false;
                    break;
            }
        }
        if (root.Malformed || !foundChat) return false;
        turn.TimestampMs = seconds is { } s ? (long)s * 1000 + (long)(nanos / 1_000_000) : null;
        return true;
    }

    /// <summary>Decodes one <c>steps.metadata</c> record. False when the record itself is malformed.</summary>
    internal static bool TryParseStep(ReadOnlySpan<byte> record, out Step step)
    {
        step = default;
        ulong? seconds = null;
        ulong nanos = 0;
        var timeValid = true;
        var botValid = true;
        var root = new Reader(record);
        while (root.Next(out var field))
        {
            switch (field.Number)
            {
                case 1:
                    // A malformed time only withholds this step's time, as upstream decides.
                    if (field.Wire != 2 || !ParseTimestamp(field.Data, ref seconds, ref nanos)) timeValid = false;
                    break;
                case 9:
                    // A malformed bot id disables exact matching for this step, even if a later envelope is valid.
                    if (field.Wire != 2 || !ParseStepBot(field.Data, ref step.BotId, ref botValid))
                    {
                        botValid = false;
                        step.BotId = default;
                    }
                    break;
                case 12:
                    if (!ReadId(field, out step.StepId)) return false;
                    break;
            }
        }
        if (root.Malformed) return false;
        if (!botValid) step.BotId = default;
        step.TimestampMs = timeValid && seconds is { } s ? (long)s * 1000 + (long)(nanos / 1_000_000) : null;
        return true;
    }

    private static bool ParseStepBot(ReadOnlySpan<byte> message, ref CostKey botId, ref bool valid)
    {
        var reader = new Reader(message);
        while (reader.Next(out var field))
        {
            if (field.Number != 7) continue;
            if (!ReadId(field, out var value)) return false;
            if (valid) botId = value;
        }
        return !reader.Malformed;
    }

    private bool ParseChat(ReadOnlySpan<byte> message, ref Turn turn, ref ulong? seconds, ref ulong nanos)
    {
        var reader = new Reader(message);
        while (reader.Next(out var field))
        {
            switch (field.Number)
            {
                case 4:
                    if (field.Wire != 2 || !ParseUsage(field.Data, ref turn.Usage)) return false;
                    turn.HasUsage = true;
                    break;
                case 9:
                    if (field.Wire != 2 || !ParseGeneration(field.Data, ref seconds, ref nanos)) return false;
                    break;
                case 19:
                    if (!ReadModel(field, out var model)) return false;
                    turn.Model = model;
                    break;
                case 21:
                    if (!ReadId(field, out turn.Label)) return false;
                    break;
            }
        }
        return !reader.Malformed;
    }

    private static bool ParseUsage(ReadOnlySpan<byte> message, ref Usage usage)
    {
        var reader = new Reader(message);
        while (reader.Next(out var field))
        {
            switch (field.Number)
            {
                // Field 1 is the model enum id, never a token count; it only has to be a valid counter.
                case 1: if (!Counter(field, out _)) return false; break;
                case 2: if (!Counter(field, out usage.NewInput)) return false; break;
                case 5: if (!Counter(field, out usage.CacheRead)) return false; break;
                case 7:
                    if (!ReadId(field, out var bot))
                    {
                        usage.BotIdInvalid = true;
                        usage.BotId = default;
                    }
                    else if (!usage.BotIdInvalid) usage.BotId = bot;
                    break;
                case 9: if (!Counter(field, out usage.Reasoning)) return false; break;
                case 10: if (!Counter(field, out usage.Output)) return false; break;
                case 11: if (!ReadId(field, out usage.ResponseId)) return false; break;
            }
        }
        return !reader.Malformed;
    }

    private static bool ParseGeneration(ReadOnlySpan<byte> message, ref ulong? seconds, ref ulong nanos)
    {
        var reader = new Reader(message);
        while (reader.Next(out var field))
        {
            if (field.Number != 4) continue;
            if (field.Wire != 2 || !ParseTimestamp(field.Data, ref seconds, ref nanos)) return false;
        }
        return !reader.Malformed;
    }

    private static bool ParseTimestamp(ReadOnlySpan<byte> message, ref ulong? seconds, ref ulong nanos)
    {
        var reader = new Reader(message);
        while (reader.Next(out var field))
        {
            switch (field.Number)
            {
                case 1:
                    if (field.Wire != 0 || field.Value is 0 or > MaxSeconds) return false;
                    seconds = field.Value;
                    break;
                case 2:
                    if (field.Wire != 0 || field.Value > 999_999_999) return false;
                    nanos = field.Value;
                    break;
            }
        }
        return !reader.Malformed;
    }

    /// <summary>
    /// A token counter. Values above <see cref="int.MaxValue"/> are rejected as a malformed record (the JSON log
    /// parser applies the same bound), which keeps every sum in the index far from overflow.
    /// </summary>
    private static bool Counter(Field field, out long value)
    {
        value = 0;
        if (field.Wire != 0 || field.Value > int.MaxValue) return false;
        value = (long)field.Value;
        return true;
    }

    /// <summary>
    /// A string field kept only as a hash of its exact bytes. Invalid UTF-8 or the wrong wire type is malformed; an
    /// empty or whitespace-only value is absent (default), as upstream decides.
    /// </summary>
    private static bool ReadId(Field field, out CostKey id)
    {
        id = default;
        if (field.Wire != 2 || !Utf8.IsValid(field.Data)) return false;
        if (!IsBlank(field.Data)) id = CostKey.Hash(field.Data);
        return true;
    }

    private bool ReadModel(Field field, out string? model)
    {
        model = null;
        if (field.Wire != 2 || !Utf8.IsValid(field.Data)) return false;
        if (IsBlank(field.Data)) return true;
        for (var i = 0; i < _modelCount; i++)
        {
            if (!field.Data.SequenceEqual(_models[i].Raw)) continue;
            model = _models[i].Model;
            return true;
        }
        model = CostPricing.Normalize(Encoding.UTF8.GetString(field.Data), CostProvider.Gemini);
        _models[_modelNext] = (field.Data.ToArray(), model);
        if (_modelCount < ModelCacheCapacity) _modelCount++;
        _modelNext = (_modelNext + 1) % ModelCacheCapacity;
        return true;
    }

    private static bool IsBlank(ReadOnlySpan<byte> utf8)
    {
        foreach (var b in utf8)
        {
            if (b >= 0x80) return string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(utf8));
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or 0x0C)) return false;
        }
        return true;
    }

    internal readonly ref struct Field(int number, int wire, ReadOnlySpan<byte> data, ulong value)
    {
        internal int Number { get; } = number;
        internal int Wire { get; } = wire;
        internal ReadOnlySpan<byte> Data { get; } = data;
        internal ulong Value { get; } = value;
    }

    /// <summary>Walks the fields of one message. <see cref="Malformed"/> is set when the bytes end mid-field.</summary>
    internal ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;

        internal bool Malformed { get; private set; }

        internal bool Next(out Field field)
        {
            field = default;
            if (_offset >= _bytes.Length || Malformed) return false;
            if (!Varint(out var tag) || tag >> 3 is 0 or > 536_870_911)
            {
                Malformed = true;
                return false;
            }
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (wire == 0)
            {
                if (!Varint(out var value)) return false;
                field = new(number, wire, default, value);
                return true;
            }
            ulong count;
            switch (wire)
            {
                case 1: count = 8; break;
                case 5: count = 4; break;
                case 2:
                    if (!Varint(out count)) return false;
                    break;
                default:
                    // Groups (3 and 4) and the unused wire types are not part of these records.
                    Malformed = true;
                    return false;
            }
            if (count > (ulong)(_bytes.Length - _offset))
            {
                Malformed = true;
                return false;
            }
            field = new(number, wire, _bytes.Slice(_offset, (int)count), 0);
            _offset += (int)count;
            return true;
        }

        private bool Varint(out ulong value)
        {
            value = 0;
            for (var index = 0; index < 10 && _offset < _bytes.Length; index++)
            {
                var b = _bytes[_offset++];
                // The tenth byte may only carry the 64th bit.
                if (index == 9 && b > 1) break;
                value |= (ulong)(b & 0x7F) << (index * 7);
                if ((b & 0x80) == 0) return true;
            }
            Malformed = true;
            return false;
        }
    }
}
