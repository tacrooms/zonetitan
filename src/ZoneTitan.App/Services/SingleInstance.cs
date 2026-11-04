using System.IO;
using System.IO.Pipes;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.App.Services;

/// <summary>
/// Enforces one running ZoneTitan and forwards command-line arguments to it.
/// <para>
/// This is what makes every external trigger free (spec §11): AutoHotkey, a Stream Deck
/// button, a desktop shortcut or a scheduled task can all just run
/// <c>ZoneTitan.exe --zone game</c>, and the already-running instance does the work.
/// </para>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\ZoneTitan.SingleInstance";
    private const string PipeName = "ZoneTitan.Command";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public bool IsFirstInstance { get; }

    /// <summary>Raised when another invocation forwards arguments to this instance.</summary>
    public event Action<string[]>? CommandReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>Starts accepting forwarded commands. First instance only.</summary>
    public void StartServer()
    {
        if (!IsFirstInstance)
            return;

        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoop(_cancellation.Token));
    }

    private async Task ServerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var line = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var args = line.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.Trim())
                    .ToArray();

                Log.Info($"Received forwarded command: {string.Join(' ', args)}");
                CommandReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Pipe server error: {ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends arguments to the running instance. Returns false if nobody answered.</summary>
    public static bool TryForward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(string.Join('\n', args));

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not forward command to the running instance: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();

        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned */ }
        }

        _mutex.Dispose();
    }
}
