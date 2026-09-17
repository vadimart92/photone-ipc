// Photone.Ipc sample: READER. Opens the ring buffer created by the Writer sample (by name, from another process),
// consumes the float stream and prints statistics once per second. Start as many instances as you like (up to 32):
// every reader has its own cursor and sees the whole stream (broadcast, zero-copy, true shared memory).
// It also follows the Writer's stream tags: the format in effect when it joins, format changes and per-second marks as it reads.
//
//   dotnet run --project samples/Photone.Ipc.Samples.Reader -c Release [-- --name sine]

using System.Diagnostics;
using System.Runtime.Versioning;
using Photone.Ipc;

[assembly: SupportedOSPlatform("windows")]

string name = "sine";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--name" && i + 1 < args.Length)
    {
        name = args[++i];
    }
}

// the same tag names as the Writer: this process's own StreamFormat / SecondMark types receive the Writer's tags
var tagSerializer = new JsonTagSerializer().Register<StreamFormat>("sine.format").Register<SecondMark>("sine.second");

// ---- open (retry until the writer exists) --------------------------------------------------------
RingBuffer<float> buffer;
while (true)
{
    try
    {
        buffer = RingBuffer<float>.Open(name, new RingBufferOptions { TagSerializer = tagSerializer });
        break;
    }
    catch (RingBufferNotFoundException)
    {
        Console.WriteLine($"waiting for a writer to create \"{name}\" ...");
        await Task.Delay(500);
    }
}

using (buffer)
{
    Console.WriteLine($"reader  : {buffer.Name}  capacity={buffer.Capacity:N0} floats  base=0x{buffer.BaseAddress:X}  creatorBase=0x{buffer.CreatorBaseAddress:X}  sameAddress={buffer.IsMappedAtCreatorAddress}  pid={Environment.ProcessId}");

    // ---- the API sketch, reader side -------------------------------------------------------------
    using var reader = buffer.CreateReader();                   // independent cursor; starts at the current head of the stream
    Console.WriteLine($"          slot={reader.Slot}  cursor={reader.ReadCursor:N0}  (Ctrl+C stops)");
    if (CurrentFormat(reader) is StreamFormat joined)            // written long before this reader existed
    {
        Console.WriteLine($"          format in effect: {joined.Frequency} Hz tone at {joined.SampleRate:N0} samples/s (set at sample {joined.Offset:N0})");
    }

    if (await reader.Wait(100))                                 // async wait until >= 100 elements are readable
    {
        reader.TryRead(100, out var chunk);                     // chunk.Data.Span : ReadOnlySpan<float>, contiguous across the ring's end
        Console.WriteLine($"first chunk: {chunk.Length} samples at cursor {chunk.Cursor}, sample[0]={chunk.Data.Span[0]:F4}");
        reader.Advance(90);                                     // consume 90 (may advance less than read: the last 10 are read again below)
    }

    // ---- consume and print stats -----------------------------------------------------------------
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    const int BlockSize = 1024;
    long consumed = 90;
    long lastConsumed = consumed;
    long marks = 0;
    double peak = 0;
    double sumSquares = 0;
    long sumCount = 0;
    var clock = Stopwatch.StartNew();
    double lastReport = 0;

    try
    {
        while (await reader.Wait(BlockSize, cts.Token))        // false => the writer closed or died and fewer than BlockSize will ever arrive
        {
            reader.TryRead(BlockSize, out var chunk);
            ReadOnlySpan<float> span = chunk.Data.Span;
            for (int i = 0; i < span.Length; i++)
            {
                float v = span[i];
                double a = Math.Abs(v);
                if (a > peak)
                {
                    peak = a;
                }

                sumSquares += (double)v * v;
            }

            sumCount += span.Length;
            foreach (ITag tag in chunk.Tags.Span)               // the tags attached to these 1024 samples, in offset order
            {
                switch (tag)
                {
                    case StreamFormat f:
                        Console.WriteLine($"          format change at sample {f.Offset:N0}: {f.Frequency} Hz tone");
                        break;
                    case SecondMark:
                        marks++;
                        break;
                }
            }

            reader.Advance(BlockSize);
            consumed += BlockSize;

            double now = clock.Elapsed.TotalSeconds;
            if (now - lastReport >= 1.0)
            {
                double perSec = (consumed - lastConsumed) / (now - lastReport);
                double rms = Math.Sqrt(sumSquares / Math.Max(1, sumCount));
                double tone = CurrentFormat(reader)?.Frequency ?? double.NaN;
                Console.WriteLine($"[{now,7:F1} s] consumed={consumed:N0}  {perSec:N0} samples/s  rms={rms:F4} peak={peak:F4}  tone={tone} Hz  seconds marked={marks}  lag={buffer.WriteCursor - reader.ReadCursor:N0}  status={reader.Status}");
                lastReport = now;
                lastConsumed = consumed;
                peak = 0;
                sumSquares = 0;
                sumCount = 0;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("cancelled");
    }

    // drain whatever is left (fewer than BlockSize elements) after the writer went away
    if (reader.Status is ReaderStatus.WriterClosed or ReaderStatus.WriterTerminated)
    {
        int left = (int)reader.Available;
        if (left > 0 && reader.TryRead(left, out var tail))
        {
            consumed += tail.Length;
            reader.Advance(left);
        }
    }

    Console.WriteLine($"done: consumed={consumed:N0}  status={reader.Status}  completed={reader.IsCompleted}");
}

// the format in effect at the reader's position: the last persistent "format" tag before it (ReadLastTagValues is a span over the reader's own array)
static StreamFormat? CurrentFormat(RingReader<float> reader)
{
    foreach (ITag tag in reader.ReadLastTagValues())
    {
        if (tag is StreamFormat format)
        {
            return format;
        }
    }

    return null;
}

/// <summary>This process's view of the Writer's "sine.format" tag (persistent).</summary>
public sealed class StreamFormat : ITag
{
    public static bool IsPersistent => true;

    public ulong Offset { get; set; }

    public string Key => "format";

    public double SampleRate { get; set; }

    public double Frequency { get; set; }
}

/// <summary>This process's view of the Writer's "sine.second" tag.</summary>
public sealed class SecondMark : ITag
{
    public ulong Offset { get; set; }

    public string Key => "second";

    public long Second { get; set; }
}
