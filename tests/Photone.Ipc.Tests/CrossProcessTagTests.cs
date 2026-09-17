using System.Globalization;
using Photone.Ipc.TestChild;

namespace Photone.Ipc.Tests;

/// <summary>Tags across processes (DESIGN §16): JSON records written in one process, checked against <see cref="TagPlan"/> in another.</summary>
[Collection("ipc")]
public sealed class CrossProcessTagTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    private static string Arg(long value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void ChildReader_JoinsWithThePersistentState_AndSeesEveryLaterTag()
    {
        string name = TestNames.UniqueSection();
        var options = new RingBufferOptions { Tags = TagMode.CrossProcess, TagSerializer = TagPlan.CreateSerializer() };
        using var buffer = RingBuffer<long>.Create(1 << 16, name, options);
        var rng = new Random(3);
        long before = TagPlan.Write(buffer, 50_000, 512, rng);           // no reader yet: the ring has long been recycled, only the table remembers

        using ChildProcess child = ChildProcess.Start("tag-reader", name, Arg(200_000));
        string ready = child.ReadLine(s_timeout);
        Assert.StartsWith("ready cursor=", ready, StringComparison.Ordinal);
        Assert.Contains($"cursor={before}", ready, StringComparison.Ordinal);
        Assert.Contains($"stateKeys={TagPlan.StateKeys + 1}", ready, StringComparison.Ordinal);   // k0..k2 and sample_rate

        TagPlan.Write(buffer, 200_000, 512, rng);
        string done = child.ReadLine(s_timeout);
        Assert.StartsWith("done ok", done, StringComparison.Ordinal);
        Assert.Equal(0, child.WaitForExit(s_timeout));
    }

    [Fact]
    public void ParentReader_SeesTheChildWritersTags_WhileTheirMemoryGrows()
    {
        string name = TestNames.UniqueSection();
        const long Count = 200_000;
        const int Padding = 1000;                                        // ~1 KiB labels on every 13th element: the lagging reader makes the rings grow
        using ChildProcess child = ChildProcess.Start("tag-writer", name, Arg(Count), "--label-padding", Arg(Padding));
        Assert.StartsWith("ready", child.ReadLine(s_timeout), StringComparison.Ordinal);

        using var buffer = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        using RingReader<long> reader = buffer.CreateReader();
        Assert.Equal(TagMode.CrossProcess, buffer.Tags);
        Assert.Equal(0, reader.ReadLastTagValues().Length);
        child.WriteLine("go");
        Assert.True(reader.WaitSync(60_000, s_timeout));                  // let the writer run ahead by most of the ring before reading

        var rng = new Random(8);
        long tags = 0;
        while (reader.ReadCursor < Count)
        {
            Assert.True(reader.WaitSync(1, s_timeout), $"status {reader.Status}");
            int n = (int)Math.Min(reader.Available, rng.Next(1, 2000));
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            Assert.Null(TagPlan.CheckChunk(chunk, Padding));
            tags += chunk.Tags.Length;
            reader.Advance(n);
            Assert.Null(TagPlan.CheckState(reader.ReadLastTagValues(), reader.ReadCursor));
        }

        string committed = child.ReadLine(s_timeout);
        Assert.StartsWith($"committed written={Count}", committed, StringComparison.Ordinal);
        Assert.DoesNotContain("ringSwitches=0 ", committed, StringComparison.Ordinal);
        Assert.Equal("closed", child.ReadLine(s_timeout));
        Assert.Equal(0, child.WaitForExit(s_timeout));
        Assert.True(tags > Count / 5, $"only {tags} tags");
        Assert.True(reader.IsCompleted);
    }

    [Fact]
    public void ParentReader_DrainsEveryTag_AfterTheChildWriterHasExited()
    {
        string name = TestNames.UniqueSection();
        const long Count = 60_000;                                       // below the ring's capacity: the writer never waits for this reader
        const int Padding = 3000;
        using ChildProcess child = ChildProcess.Start("tag-writer", name, Arg(Count), "--label-padding", Arg(Padding));
        Assert.StartsWith("ready", child.ReadLine(s_timeout), StringComparison.Ordinal);

        using var buffer = RingBuffer<long>.Open(name, new RingBufferOptions { TagSerializer = TagPlan.CreateSerializer() });
        using RingReader<long> reader = buffer.CreateReader();
        child.WriteLine("go");
        string committed = child.ReadLine(s_timeout);
        Assert.StartsWith($"committed written={Count}", committed, StringComparison.Ordinal);
        Assert.DoesNotContain("ringSwitches=0 ", committed, StringComparison.Ordinal);   // the tags outgrew the first rings while nobody read
        Assert.Equal("closed", child.ReadLine(s_timeout));
        Assert.Equal(0, child.WaitForExit(s_timeout));

        // the writer is gone, and with it its views: the tag memory lives in the buffer's own section, which this process holds
        long tags = 0;
        while (reader.ReadCursor < Count)
        {
            int n = (int)Math.Min(reader.Available, 1000);
            Assert.True(n > 0);
            Assert.True(reader.TryRead(n, out Chunk<long> chunk));
            Assert.Null(TagPlan.CheckChunk(chunk, Padding));
            tags += chunk.Tags.Length;
            reader.Advance(n);
            Assert.Null(TagPlan.CheckState(reader.ReadLastTagValues(), reader.ReadCursor));
        }

        Assert.True(tags > Count / 5, $"only {tags} tags");
        Assert.True(reader.IsCompleted);
    }
}
