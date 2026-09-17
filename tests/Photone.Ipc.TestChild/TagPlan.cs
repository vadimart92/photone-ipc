using System.Globalization;

namespace Photone.Ipc.TestChild;

/// <summary>Persistent tag with a variable key (the plan uses k0..k2).</summary>
public sealed class StateTag : ITag
{
    public static bool IsPersistent => true;

    public ulong Offset { get; set; }

    public string Key { get; set; } = "";

    public long Value { get; set; }
}

/// <summary>Non-persistent tag.</summary>
public sealed class LabelTag : ITag
{
    public ulong Offset { get; set; }

    public string Key { get; set; } = "label";

    public string Text { get; set; } = "";
}

/// <summary>Persistent tag with a fixed key.</summary>
public sealed class SampleRateTag : ITag
{
    public const string TagKey = "sample_rate";

    public static bool IsPersistent => true;

    public ulong Offset { get; set; }

    public string Key => TagKey;

    public double Rate { get; set; }
}

/// <summary>
/// A deterministic tag plan shared by the tag tests and the test child: which tags sit at an element offset is a pure function of the offset, so a writer
/// that commits a bucket partially re-adds the dropped tags with the next bucket, and any reader (whenever it joined, in any process) can check the tags of
/// every chunk and its <see cref="RingReader{T}.ReadLastTagValues"/> against the plan. Elements hold their own cursor (<c>data[c] == c</c>).
/// </summary>
public static class TagPlan
{
    public const int StateKeys = 3;
    private const ulong Seed = 0x9E37_79B9_7F4A_7C15;

    public static JsonTagSerializer CreateSerializer() => new JsonTagSerializer().Register<StateTag>().Register<LabelTag>().Register<SampleRateTag>();

    public static string StateKey(int k) => "k" + k.ToString(CultureInfo.InvariantCulture);

    /// <summary>The state key planned at <paramref name="offset"/> (0..2), or -1.</summary>
    public static int StateAt(long offset)
    {
        ulong h = Mix((ulong)offset);
        int k = (int)(h % 16);
        return k < StateKeys ? k : -1;
    }

    public static bool LabelAt(long offset) => (Mix((ulong)offset) >> 8) % 13 == 0;

    public static bool RateAt(long offset) => (Mix((ulong)offset) >> 16) % 97 == 0;

    public static double RateValue(long offset) => 1000 + offset;

    public static int CountAt(long offset) => (StateAt(offset) >= 0 ? 1 : 0) + (LabelAt(offset) ? 1 : 0) + (RateAt(offset) ? 1 : 0);

    /// <summary>Adds the planned tags of every element of <paramref name="bucket"/>; <paramref name="reverse"/> visits the offsets backwards (tags of one offset keep their order).</summary>
    public static int AddPlannedTags(Bucket<long> bucket, bool reverse)
    {
        int added = 0;
        long start = bucket.Cursor;
        for (int j = 0; j < bucket.Length; j++)
        {
            long o = reverse ? start + bucket.Length - 1 - j : start + j;
            int k = StateAt(o);
            if (k >= 0)
            {
                bucket.AddTag(new StateTag { Offset = (ulong)o, Key = StateKey(k), Value = o });
                added++;
            }

            if (LabelAt(o))
            {
                bucket.AddTag(new LabelTag { Offset = (ulong)o, Text = "at " + o.ToString(CultureInfo.InvariantCulture) });
                added++;
            }

            if (RateAt(o))
            {
                bucket.AddTag(new SampleRateTag { Offset = (ulong)o, Rate = RateValue(o) });
                added++;
            }
        }

        return added;
    }

    /// <summary>Writes <paramref name="count"/> elements with their planned tags in random buckets; sometimes commits only a prefix (the rest is written again).</summary>
    public static long Write(RingBuffer<long> buffer, long count, int maxBucket, Random rng)
    {
        long written = 0;
        while (written < count)
        {
            int n = (int)Math.Min(count - written, rng.Next(1, maxBucket + 1));
            using Bucket<long> bucket = buffer.GetBucket(n);
            Span<long> span = bucket.Span;
            for (int j = 0; j < span.Length; j++)
            {
                span[j] = bucket.Cursor + j;
            }

            AddPlannedTags(bucket, reverse: rng.Next(4) == 0);
            int commit = rng.Next(8) == 0 ? rng.Next(0, n + 1) : n;
            bucket.Commit(commit);
            written += commit;
        }

        return written;
    }

    /// <summary>Checks the data and the tags of a chunk against the plan; <see langword="null"/> when they match.</summary>
    public static string? CheckChunk(Chunk<long> chunk)
    {
        ReadOnlySpan<long> span = chunk.Span;
        for (int j = 0; j < span.Length; j++)
        {
            if (span[j] != chunk.Cursor + j)
            {
                return Inv($"data mismatch at {chunk.Cursor + j}: {span[j]}");
            }
        }

        ReadOnlySpan<ITag> tags = chunk.Tags.Span;
        int t = 0;
        for (long o = chunk.Cursor; o < chunk.Cursor + chunk.Length; o++)
        {
            int k = StateAt(o);
            if (k >= 0)
            {
                if (t >= tags.Length || tags[t] is not StateTag s || s.Offset != (ulong)o || s.Key != StateKey(k) || s.Value != o)
                {
                    return Describe(chunk, t, o, "StateTag " + StateKey(k));
                }

                t++;
            }

            if (LabelAt(o))
            {
                if (t >= tags.Length || tags[t] is not LabelTag l || l.Offset != (ulong)o || l.Key != "label" || l.Text != "at " + o.ToString(CultureInfo.InvariantCulture))
                {
                    return Describe(chunk, t, o, "LabelTag");
                }

                t++;
            }

            if (RateAt(o))
            {
                if (t >= tags.Length || tags[t] is not SampleRateTag r || r.Offset != (ulong)o || r.Rate != RateValue(o))
                {
                    return Describe(chunk, t, o, "SampleRateTag");
                }

                t++;
            }
        }

        return t == tags.Length ? null : Inv($"chunk [{chunk.Cursor}, {chunk.Cursor + chunk.Length}) has {tags.Length} tags, the plan {t}");
    }

    /// <summary>Checks <see cref="RingReader{T}.ReadLastTagValues"/> of a reader at <paramref name="cursor"/>; <see langword="null"/> when it matches the plan.</summary>
    public static string? CheckState(IReadOnlyDictionary<string, ITag> state, long cursor)
    {
        int expectedCount = 0;
        for (int k = 0; k < StateKeys; k++)
        {
            long last = LastBefore(cursor, o => StateAt(o) == k);
            if (last < 0)
            {
                if (state.ContainsKey(StateKey(k)))
                {
                    return Inv($"state at {cursor}: {StateKey(k)} present, the plan has none");
                }

                continue;
            }

            expectedCount++;
            if (!state.TryGetValue(StateKey(k), out ITag? tag) || tag is not StateTag s || s.Value != last || s.Offset != (ulong)last)
            {
                return Inv($"state at {cursor}: {StateKey(k)} = {Show(state.GetValueOrDefault(StateKey(k)))}, the plan {last}");
            }
        }

        long rate = LastBefore(cursor, RateAt);
        if (rate >= 0)
        {
            expectedCount++;
            if (!state.TryGetValue(SampleRateTag.TagKey, out ITag? tag) || tag is not SampleRateTag r || r.Rate != RateValue(rate))
            {
                return Inv($"state at {cursor}: sample_rate = {Show(state.GetValueOrDefault(SampleRateTag.TagKey))}, the plan {RateValue(rate)}");
            }
        }

        return state.Count == expectedCount ? null : Inv($"state at {cursor} has {state.Count} keys, the plan {expectedCount}");
    }

    private static long LastBefore(long cursor, Func<long, bool> planned)
    {
        for (long o = cursor - 1; o >= 0; o--)
        {
            if (planned(o))
            {
                return o;
            }
        }

        return -1;
    }

    private static string Describe(Chunk<long> chunk, int index, long offset, string expected)
    {
        ReadOnlySpan<ITag> tags = chunk.Tags.Span;
        string actual = index < tags.Length ? Show(tags[index]) : "nothing";
        return Inv($"chunk [{chunk.Cursor}, {chunk.Cursor + chunk.Length}) tag #{index}: expected {expected} at {offset}, got {actual}");
    }

    private static string Show(ITag? tag) => tag switch
    {
        null => "null",
        StateTag s => Inv($"StateTag({s.Key}={s.Value}@{s.Offset})"),
        LabelTag l => Inv($"LabelTag({l.Text}@{l.Offset})"),
        SampleRateTag r => Inv($"SampleRateTag({r.Rate}@{r.Offset})"),
        _ => tag.ToString() ?? tag.GetType().Name,
    };

    private static ulong Mix(ulong x)
    {
        x += Seed;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EB;
        return x ^ (x >> 31);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
