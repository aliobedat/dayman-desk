using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>
/// Listens on 127.0.0.1:9100 where the POS printer queue now points. Each connection is one print job:
/// read it to the end, forward it at once to the raw queue on the real port, then hand it to analysis.
/// Forwarding never waits for analysis or the network.
/// </summary>
sealed class PassThroughServer
{
    public static readonly object PrintLock = new();
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    readonly Func<string?> _rawQueue;
    readonly RollingLog _log;
    readonly Action<byte[]> _onForwardFailed;
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    long _seq;

    /// <summary>bytes, forward milliseconds, forward sequence number after this job.</summary>
    public event Action<byte[], long, long>? JobForwarded;

    public PassThroughServer(Func<string?> rawQueue, RollingLog log, Action<byte[]> onForwardFailed)
    {
        _rawQueue = rawQueue;
        _log = log;
        _onForwardFailed = onForwardFailed;
    }

    public bool Running => _listener != null;
    public long Sequence => Interlocked.Read(ref _seq);

    public bool Start()
    {
        if (_listener != null) return true;
        try
        {
            var l = new TcpListener(IPAddress.Loopback, DeskConfig.ListenPort);
            l.Server.ExclusiveAddressUse = true;
            l.Start();
            _listener = l;
        }
        catch (SocketException ex)
        {
            _log.Error("listen 9100 failed: " + ex.Message);
            return false;
        }
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
        _log.Info("listening on 127.0.0.1:9100");
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _log.Info("listener stopped");
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { _log.Warn("accept: " + ex.Message); continue; }
            _ = Task.Run(() => Handle(c, ct));
        }
    }

    async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var buf = new MemoryStream();
            try
            {
                var s = client.GetStream();
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    int n;
                    try { n = await s.ReadAsync(chunk, idle.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (buf.Length > 0) _log.Warn("job idle 10 s without close: forwarding what arrived");
                        break;
                    }
                    if (n == 0) break;
                    buf.Write(chunk, 0, n);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                _log.Warn("receive: " + ex.Message + (buf.Length > 0 ? " (forwarding what arrived)" : ""));
            }
            if (buf.Length == 0) return;
            var data = buf.ToArray();
            Forward(data);
        }
    }

    void Forward(byte[] data)
    {
        var sw = Stopwatch.StartNew();
        long seq;
        try
        {
            var queue = _rawQueue() ?? throw new InvalidOperationException("no raw queue configured");
            lock (PrintLock)
            {
                // Bumped under the same lock as the QR check: a pending QR can never print after a newer job.
                seq = Interlocked.Increment(ref _seq);
                RawPrinter.Write(queue, data, "Dayman Desk");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"FORWARD FAILED ({data.Length} B): {ex.Message}");
            _onForwardFailed(data);
            return;
        }
        long ms = sw.ElapsedMilliseconds;
        if (ms > 300) _log.Warn($"forward took {ms} ms ({data.Length} B)");
        var handler = JobForwarded;
        if (handler != null) _ = Task.Run(() => handler(data, ms, seq));
    }
}
