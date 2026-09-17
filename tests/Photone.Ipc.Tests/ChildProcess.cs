using System.Diagnostics;

namespace Photone.Ipc.Tests;

/// <summary>Spawns <c>Photone.Ipc.TestChild.exe</c> and talks to it over stdin/stdout lines (readiness by line, never by sleeping).</summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly List<string> _transcript = [];

    private ChildProcess(Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public IReadOnlyList<string> Transcript => _transcript;

    public static ChildProcess Start(params string[] args)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Photone.Ipc.TestChild.exe");
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (File.Exists(exe))
        {
            psi.FileName = exe;
        }
        else
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Photone.Ipc.TestChild.dll"));
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the test child.");
        return new ChildProcess(p);
    }

    /// <summary>Reads the next stdout line, failing the test if the child prints nothing within <paramref name="timeout"/>.</summary>
    public string ReadLine(TimeSpan timeout)
    {
        Task<string?> t = _process.StandardOutput.ReadLineAsync();
        if (!t.Wait(timeout))
        {
            throw new TimeoutException($"Child printed nothing within {timeout}. Transcript: {string.Join(" | ", _transcript)}; stderr: {TryReadStdErr()}");
        }

        string line = t.Result ?? throw new InvalidOperationException($"Child closed stdout (exit code {(HasExited ? _process.ExitCode : "?")}). Transcript: {string.Join(" | ", _transcript)}; stderr: {TryReadStdErr()}");
        _transcript.Add(line);
        return line;
    }

    public void WriteLine(string line)
    {
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    /// <summary>Kills the child immediately (simulates a crash without giving it a chance to clean up).</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public int WaitForExit(TimeSpan timeout)
    {
        if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            throw new TimeoutException($"Child did not exit within {timeout}. Transcript: {string.Join(" | ", _transcript)}");
        }

        return _process.ExitCode;
    }

    private string TryReadStdErr()
    {
        try
        {
            if (_process.HasExited)
            {
                return _process.StandardError.ReadToEnd();
            }
        }
        catch (InvalidOperationException)
        {
        }

        return string.Empty;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}

/// <summary>Cross-process tests never run in parallel with each other.</summary>
[CollectionDefinition("ipc", DisableParallelization = true)]
public sealed class IpcCollection
{
}
