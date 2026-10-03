using System.Diagnostics;

namespace Sancho.Console.Agents;

/// <summary>Owns a CLI process until it exits, including cancellation and iterator disposal.</summary>
internal sealed class OwnedAgentProcess : IAsyncDisposable
{
    private readonly CancellationTokenRegistration _registration;
    public Process Process { get; }

    private OwnedAgentProcess(Process process, CancellationToken token)
    {
        Process = process;
        _registration = token.Register(Kill);
    }

    public static OwnedAgentProcess Start(ProcessStartInfo info, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new OwnedAgentProcess(Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start {info.FileName}."), token);
    }

    private void Kill()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // Already exited.
    }

    public async Task StopAsync()
    {
        Kill();
        await Process.WaitForExitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _registration.DisposeAsync();
        await StopAsync();
        Process.Dispose();
    }
}
