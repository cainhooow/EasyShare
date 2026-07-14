using System.IO.Pipes;
using System.Text;

namespace EasyShare.Services;

/// <summary>
/// Carries a privacy-safe activation pulse from a duplicate process to the
/// already-running instance. The pipe is restricted to the current Windows user.
/// </summary>
public sealed class SingleInstanceActivationService : IDisposable
{
    private const string PipeName = "EasyShare.Activation.v1";
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    public event Action? ActivationRequested;

    public void Start()
    {
        _listener ??= Task.Run(ListenAsync);
    }

    public static bool TrySignalExistingInstance(TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            client.Connect(checked((int)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds));
            var pulse = Encoding.UTF8.GetBytes("activate\n");
            client.Write(pulse);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                var buffer = new byte[16];
                var read = await server.ReadAsync(buffer, _cancellation.Token).ConfigureAwait(false);
                if (read > 0)
                {
                    ActivationRequested?.Invoke();
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StartupDiagnostics.Write("Single-instance activation listener recovered from an error.", ex);
            }
        }
    }
}
