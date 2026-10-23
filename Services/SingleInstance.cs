using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace TisViewer.Services;

/// <summary>
/// Keeps a single TIS window alive. A second launch forwards the requested
/// file to the running instance through a named pipe.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public SingleInstance()
    {
        _pipeName = $"TisViewer_{Environment.UserName}_{Process.GetCurrentProcess().SessionId}";
        _mutex = new Mutex(false, @"Local\TisViewer_SingleInstance_" + _pipeName);

        try
        {
            IsFirstInstance = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            IsFirstInstance = true;
        }
    }

    public bool IsFirstInstance { get; }

    public bool TrySend(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message.ReplaceLineEndings(" "));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartServer(Action<string> onMessage)
    {
        if (!IsFirstInstance)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        onMessage(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
                catch
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // ignored
        }

        _cancellation?.Dispose();

        try
        {
            if (IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // ignored
        }

        _mutex.Dispose();
    }
}
