using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WindowsDriver.Sdk;

/// <summary>
/// Dispatch mode for an IO queue — the managed equivalent of the
/// WdfIoQueueDispatch* settings.
/// </summary>
public enum QueueDispatchMode
{
    /// <summary>Requests are handed to the driver one at a time, in order.</summary>
    Sequential,

    /// <summary>Requests are handed to the driver concurrently (default).</summary>
    Parallel,

    /// <summary>Requests wait in the queue until the driver pulls them — for polling-style drivers.</summary>
    Manual,
}

/// <summary>Optional tuning knobs for <see cref="IoQueue"/>.</summary>
public sealed class IoQueueOptions
{
    public QueueDispatchMode Dispatch { get; init; } = QueueDispatchMode.Parallel;
    public string? Name { get; init; }

    public static IoQueueOptions Default => new();
}

/// <summary>
/// The set of request handlers a driver registers on a queue. Unregistered
/// kinds complete with <see cref="NtStatus.InvalidDeviceRequest"/> — the same
/// behavior as a WDF queue without a corresponding EvtIo* callback.
/// </summary>
public sealed class QueueHandlers
{
    public Func<IoRequest, CancellationToken, Task>? DeviceControl { get; set; }
    public Func<IoRequest, CancellationToken, Task>? Read { get; set; }
    public Func<IoRequest, CancellationToken, Task>? Write { get; set; }
}

/// <summary>
/// An IO queue owned by a device. Requests arrive via <see cref="Submit"/> and
/// are dispatched to the registered handlers according to the dispatch mode.
/// </summary>
public sealed class IoQueue
{
    private readonly QueueHandlers _handlers = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly List<Task> _outstanding = new();
    private readonly SemaphoreSlim _sequentialGate = new(1, 1);
    private readonly Channel<IoRequest> _manualChannel;
    private readonly IDriverLog _log;
    private int _running;

    public IoQueue(IDriverLog log, IoQueueOptions? options = null)
    {
        _log = log;
        Options = options ?? IoQueueOptions.Default;
        _manualChannel = Channel.CreateUnbounded<IoRequest>(new UnboundedChannelOptions { SingleReader = false });
    }

    public string Name => Options.Name ?? "queue";
    public IoQueueOptions Options { get; }
    public QueueDispatchMode Dispatch => Options.Dispatch;
    public QueueHandlers Handlers => _handlers;
    public bool IsStopped { get; private set; }

    // ----- Handler registration (fluent) -----

    public IoQueue OnDeviceControl(Func<IoRequest, CancellationToken, Task> handler) { _handlers.DeviceControl = handler; return this; }
    public IoQueue OnRead(Func<IoRequest, CancellationToken, Task> handler) { _handlers.Read = handler; return this; }
    public IoQueue OnWrite(Func<IoRequest, CancellationToken, Task> handler) { _handlers.Write = handler; return this; }

    /// <summary>Manual-mode readers pull requests from this channel.</summary>
    public ChannelReader<IoRequest> ManualRequests => _manualChannel.Reader;

    /// <summary>Submits a request. The host calls this; drivers do not.</summary>
    public void Submit(IoRequest request)
    {
        if (IsStopped)
        {
            request.Fail(NtStatus.Cancelled);
            return;
        }

        switch (Dispatch)
        {
            case QueueDispatchMode.Manual:
                _manualChannel.Writer.TryWrite(request);
                break;

            case QueueDispatchMode.Sequential:
                Track(DispatchSequentialAsync(request));
                break;

            default:
                _ = DispatchParallelAsync(request);
                break;
        }
    }

    private async Task DispatchSequentialAsync(IoRequest request)
    {
        try
        {
            await _sequentialGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            request.Fail(NtStatus.Cancelled);
            return;
        }
        try
        {
            // The gate is held for the whole handler duration, so requests run
            // strictly one at a time, in submission order.
            await InvokeHandlerAsync(request).ConfigureAwait(false);
        }
        finally
        {
            _sequentialGate.Release();
        }
    }

    private async Task DispatchParallelAsync(IoRequest request)
    {
        Track(InvokeHandlerAsync(request));
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    private void Track(Task handlerTask)
    {
        lock (_gate)
        {
            _outstanding.Add(handlerTask);
            _running++;
        }
        _ = handlerTask.ContinueWith(t =>
        {
            lock (_gate)
            {
                _outstanding.Remove(t);
                _running--;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task InvokeHandlerAsync(IoRequest request)
    {
        var handler = request.Kind switch
        {
            IoRequestKind.DeviceControl => _handlers.DeviceControl,
            IoRequestKind.Read => _handlers.Read,
            IoRequestKind.Write => _handlers.Write,
            _ => null,
        };

        try
        {
            if (handler is null)
            {
                request.Fail(NtStatus.InvalidDeviceRequest);
                return;
            }
            await handler(request, _stopping.Token).ConfigureAwait(false);
        }
        catch (DriverException ex)
        {
            _log.Error($"[{Name}] handler for {request.Kind} failed: {ex.Message}");
            request.Fail(ex.Status);
        }
        catch (OperationCanceledException)
        {
            request.Fail(NtStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _log.Error($"[{Name}] unhandled exception in {request.Kind} handler: {ex}");
            request.Fail(NtStatus.Unsuccessful);
        }
    }

    /// <summary>Waits until all in-flight handler invocations have finished.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_gate)
            {
                snapshot = _outstanding.ToArray();
            }
            if (snapshot.Length == 0)
            {
                return;
            }
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the queue: pending manual requests are cancelled, and new
    /// submissions complete immediately with <see cref="NtStatus.Cancelled"/>.
    /// </summary>
    public async Task StopAsync()
    {
        if (IsStopped)
        {
            return;
        }
        IsStopped = true;
        _stopping.Cancel();
        _manualChannel.Writer.TryComplete();
        while (_manualChannel.Reader.TryRead(out var pending))
        {
            pending.Fail(NtStatus.Cancelled);
        }
        await DrainAsync().ConfigureAwait(false);
    }
}
