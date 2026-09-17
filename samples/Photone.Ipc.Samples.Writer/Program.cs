// Photone.Ipc sample: WRITER. Creates the shared ring buffer "sine" and streams a 440 Hz sine wave of floats into it.
// Run the Reader sample (any number of instances, in other processes) while this is running.
//
//   dotnet run --project samples/Photone.Ipc.Samples.Writer -c Release [-- --rate 48000] [--name sine]
//
// --rate N   samples per second (default 48000 = audio rate; 0 = unpaced, as fast as the slowest reader takes it)
// --name X   buffer name (default "sine"; becomes the section "Local\photone.sine")

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Photone.Ipc;

[assembly: SupportedOSPlatform("windows")]

string name = "sine";
double rate = 48_000;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--rate" && i + 1 < args.Length)
    {
        rate = double.Parse(args[++i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--name" && i + 1 < args.Length)
    {
        name = args[++i];
    }
}

// ---- the API sketch, writer side ----------------------------------------------------------------
using RingBuffer<float> buffer = RingBuffer<float>.Create(1 << 20, name);   // 2^20 floats = 4 MiB, section "Local\photone.<name>"

Console.WriteLine($"writer  : {buffer.Name}  capacity={buffer.Capacity:N0} floats ({buffer.DataBytes / 1024 / 1024} MiB)  base=0x{buffer.BaseAddress:X}  pid={Environment.ProcessId}");
Console.WriteLine($"          rate={rate:N0} samples/s ({(rate == 0 ? "unpaced" : "paced")}); start readers now; Ctrl+C stops");

using (var bucket = buffer.GetBucket(1024))      // blocks if there is no space for 1024 elements (never with zero readers)
{
    bucket.Span.Fill(0);                          // Span<float> of length 1024, contiguous even across the ring's end (double mapping)
    bucket.Commit(1000);                          // publish the first 1000 elements only (a bucket may commit less than requested)
}

// ---- stream --------------------------------------------------------------------------------------
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

const int BucketSize = 1024;
const double Frequency = 440.0;
double phaseStep = 2 * Math.PI * Frequency / (rate > 0 ? rate : 48_000);
double phase = 0;
long written = 1000;
var clock = Stopwatch.StartNew();
double lastReport = 0;
long lastWritten = written;

while (!cts.IsCancellationRequested)
{
    using (var bucket = buffer.GetBucket(BucketSize))                 // back-pressure: waits (spin, then kernel) for the slowest reader
    {
        Span<float> span = bucket.Span;
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (float)Math.Sin(phase);
            phase += phaseStep;
        }

        if (phase > 2 * Math.PI)
        {
            phase -= 2 * Math.PI;
        }

        bucket.Commit(BucketSize);
    }

    written += BucketSize;

    if (rate > 0)
    {
        // pace to the requested sample rate: sleep for the coarse part, spin for the rest
        double due = written / rate;
        double ahead = due - clock.Elapsed.TotalSeconds;
        if (ahead > 0.002)
        {
            Thread.Sleep((int)(ahead * 1000) - 1);
        }

        while (clock.Elapsed.TotalSeconds < due && !cts.IsCancellationRequested)
        {
            Thread.SpinWait(50);
        }
    }

    double now = clock.Elapsed.TotalSeconds;
    if (now - lastReport >= 1.0)
    {
        double perSec = (written - lastWritten) / (now - lastReport);
        Console.WriteLine($"[{now,7:F1} s] written={written:N0}  {perSec:N0} samples/s ({perSec * sizeof(float) / 1e6:F1} MB/s)  readers={buffer.ActiveReaderCount}  free={buffer.FreeSpace:N0}  evicted={buffer.EvictedReaders}");
        lastReport = now;
        lastWritten = written;
    }
}

Console.WriteLine($"closing after {written:N0} samples; readers drain what is left and then see Status == WriterClosed");
