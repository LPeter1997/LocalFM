using System.Collections.Concurrent;
using System.Net;
using LocalFM.Core;
using LocalFM.Vibe.Net;
using LocalFM.Vibe.Protocol;

namespace LocalFM.Vibe.Client;

/// <summary>
/// A listener's connection to a host: control (window, timeline, clock pings) and data (file ranges, as an
/// <see cref="IRangeSource"/>). Events are raised on background threads.
/// </summary>
public sealed class VibeClient : IRangeSource, IAsyncDisposable
{
    private const string Area = "Vibe";

    private readonly Wire _control, _data;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(int Status, ReadOnlyMemory<byte> Data)>> _pending = new();
    private uint _nextRequest;
    private volatile bool _dataGone;
    private int _ended;

    private VibeClient(Wire control, Wire data, Welcome welcome)
    {
        _control = control;
        _data = data;
        HostName = welcome.HostName;
        ListenerId = welcome.ListenerId;
        _ = Task.Run(ControlLoop);
        _ = Task.Run(DataLoop);
        _ = Task.Run(PingLoop);
    }

    public string HostName { get; }
    public string ListenerId { get; }
    public ClockSync Clock { get; } = new();
    public QueueWindow? Window { get; private set; }
    public Timeline? Timeline { get; private set; }

    public event Action? WindowChanged;
    public event Action? TimelineChanged;
    /// <summary>The session is over (host ended it, removed us, or the connection broke), with a reason to show.</summary>
    public event Action<string>? Ended;

    public static async Task<VibeClient> ConnectAsync(Invite invite, string name, CancellationToken ct = default)
    {
        var (controlStream, endpoint) = await TlsTransport.ConnectAsync(invite, null, ct).ConfigureAwait(false);
        var control = new Wire(controlStream);
        Wire? data = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await control.SendAsync(new Hello(Wire.Version, ConnectionRole.Control, Convert.ToBase64String(invite.Secret), name), timeout.Token).ConfigureAwait(false);
            var answer = await control.ReadMessageAsync(timeout.Token).ConfigureAwait(false);
            if (answer is Rejected r) throw new VibeRejectedException(r.Reason);
            if (answer is not Welcome welcome) throw new IOException("The host did not answer properly.");

            // The data connection goes to the address that just worked.
            var (dataStream, _) = await TlsTransport.ConnectAsync(invite, [endpoint], timeout.Token).ConfigureAwait(false);
            data = new Wire(dataStream);
            await data.SendAsync(new Hello(Wire.Version, ConnectionRole.Data, welcome.DataToken, name), timeout.Token).ConfigureAwait(false);
            return new VibeClient(control, data, welcome);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            if (data is not null) await data.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ control

    private async Task ControlLoop()
    {
        string reason = "Lost the connection to the host.";
        try
        {
            while (await _control.ReadMessageAsync(_cts.Token).ConfigureAwait(false) is { } m)
            {
                switch (m)
                {
                    case Pong p:
                        Clock.Add(p.T0, p.T1, VibeClock.NowUs);
                        break;
                    case QueueWindow w:
                        Window = w;
                        WindowChanged?.Invoke();
                        break;
                    case Timeline t:
                        Timeline = t;
                        TimelineChanged?.Invoke();
                        break;
                    case Bye bye:
                        reason = bye.Reason;
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        catch (Exception ex) { Log.Info(Area, $"Control connection: {ex.Message}"); }
        finally { End(reason); }
    }

    private async Task PingLoop()
    {
        try
        {
            for (int i = 0; !_cts.IsCancellationRequested; i++)
            {
                await _control.SendAsync(new Ping(VibeClock.NowUs), _cts.Token).ConfigureAwait(false);
                // Quick at first (a good estimate before the first track starts), then a slow trickle.
                await Task.Delay(i < 8 ? 250 : 2000, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug(Area, $"Ping: {ex.Message}"); }
    }

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _cts.Cancel();
        Ended?.Invoke(reason);
    }

    // ------------------------------------------------------------------ data

    public async ValueTask<int> ReadAsync(string mediaId, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (_dataGone) throw new IOException("Not connected to the host.");
        int length = Math.Min(buffer.Length, Wire.MaxRange);
        uint id = Interlocked.Increment(ref _nextRequest);
        var tcs = new TaskCompletionSource<(int, ReadOnlyMemory<byte>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _data.SendRangeRequestAsync(id, mediaId, offset, length, ct).ConfigureAwait(false);
            var (status, data) = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            if (status != 0) throw new IOException("The host could not send that file.");
            data.CopyTo(buffer);
            return data.Length;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task DataLoop()
    {
        try
        {
            while (await _data.ReadAsync(_cts.Token).ConfigureAwait(false) is { } f)
            {
                if (f.Type != Wire.FrameType.RangeReply) continue;
                var (id, status, data) = f.AsRangeReply();
                if (_pending.TryGetValue(id, out var tcs)) tcs.TrySetResult((status, data));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Info(Area, $"Data connection: {ex.Message}"); }
        finally
        {
            _dataGone = true;
            foreach (var t in _pending.Values) t.TrySetException(new IOException("Lost the connection to the host."));
            End("Lost the connection to the host.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            try { await _control.SendAsync(new Bye("left")).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { }
        }
        _cts.Cancel();
        await _control.DisposeAsync().ConfigureAwait(false);
        await _data.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class VibeRejectedException(string reason) : Exception(reason);
