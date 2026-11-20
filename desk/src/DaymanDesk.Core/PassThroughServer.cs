using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DaymanDesk.Core;

/// <summary>Writes bytes untouched (datatype RAW) to a Windows print queue (winspool in the App; a fake in tests).</summary>
public interface IRawQueue
{
    void Write(string queue, byte[] data, string docName);
}

/// <param name="Seq">Unique across routes: a QR slip prints only while it is still its route's last job.</param>
/// <param name="PrinterUnreachable">The direct TCP/IP printer did not answer: the job went to the spooler queue instead.</param>
/// <param name="PrevSeq">The route's sequence before this job (WO-91: Release puts it back for a job that printed nothing).</param>
public sealed record ForwardedJob(PrinterRoute Route, byte[] Data, long ForwardMs, long Seq, bool PrinterUnreachable, long PrevSeq = -1);

/// <summary>
/// The listeners, one per route (WO-89). A Windows route listens on 127.0.0.1:port where its POS printer queue now
/// points: each connection is one print job, read to the end, forwarded at once (straight to a TCP/IP printer, else
/// to the raw queue on the real port), then handed to analysis. The network-mode route listens on the LAN and proxies
/// both ways to the printer while it captures the job. Forwarding never waits for analysis or the network.
/// </summary>
public sealed class PassThroughServer
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    const int MaxCapture = 8 << 20;
    /// <summary>Bytes before a LAN connection counts as a print job (status queries such as DLE EOT are shorter).</summary>
    const int JobThreshold = 16;

    sealed class Lane
    {
        public PrinterRoute Route = null!;
        public TcpListener Listener = null!;
        public CancellationTokenSource Cts = null!;
        public readonly object Lock = new();
        public long LastSeq = -1;
    }

    readonly IRawQueue _raw;
    readonly RollingLog _log;
    readonly Action<PrinterRoute, byte[]> _onForwardFailed;
    readonly IPAddress _lanAddress;
    readonly ConcurrentDictionary<string, Lane> _lanes = new();
    long _seq;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public event Action<ForwardedJob>? JobForwarded;
    /// <summary>Network mode: the printer did not answer, so the POS connection was refused as it would be without us.</summary>
    public event Action<PrinterRoute, string>? PrinterUnreachable;

    /// <param name="onForwardFailed">The raw queue refused a job: the caller restores the port and prints it directly.</param>
    /// <param name="lanAddress">The network-mode bind address (all interfaces; tests use loopback).</param>
    public PassThroughServer(IRawQueue raw, RollingLog log, Action<PrinterRoute, byte[]> onForwardFailed, IPAddress? lanAddress = null)
    {
        _raw = raw;
        _log = log;
        _onForwardFailed = onForwardFailed;
        _lanAddress = lanAddress ?? IPAddress.Any;
    }

    public bool Running => !_lanes.IsEmpty;
    public bool IsRunning(string routeId) => _lanes.ContainsKey(routeId);

    public bool Start(PrinterRoute route)
    {
        if (_lanes.ContainsKey(route.Id)) return true;
        TcpListener l;
        try
        {
            l = new TcpListener(route.Lan ? _lanAddress : IPAddress.Loopback, route.ListenPort);
            if (OperatingSystem.IsWindows()) l.Server.ExclusiveAddressUse = true;
            l.Start();
        }
        catch (SocketException ex)
        {
            _log.Error($"listen {route.ListenPort} failed: " + ex.Message);
            return false;
        }
        var lane = new Lane { Route = route, Listener = l, Cts = new CancellationTokenSource() };
        if (!_lanes.TryAdd(route.Id, lane)) { l.Stop(); return true; }
        _ = AcceptLoop(lane);
        _log.Info(route.Lan ? $"network mode listening on port {route.ListenPort}" : $"listening on 127.0.0.1:{route.ListenPort}");
        return true;
    }

    public void Stop(string routeId)
    {
        if (!_lanes.TryRemove(routeId, out var lane)) return;
        lane.Cts.Cancel();
        try { lane.Listener.Stop(); } catch { }
        _log.Info($"listener {lane.Route.ListenPort} stopped");
    }

    public void StopAll()
    {
        foreach (var id in _lanes.Keys.ToList()) Stop(id);
    }

    /// <summary>
    /// Prints the QR slip on the printer its receipt came from, only if no other job reached that printer since
    /// (checked and printed under the route's lock, the same lock the forwarder holds). False when it did not print.
    /// </summary>
    public bool PrintIfCurrent(long seq, byte[] slip)
    {
        foreach (var lane in _lanes.Values)
        {
            lock (lane.Lock)
            {
                if (lane.LastSeq != seq) continue;
                var r = lane.Route;
                if (r.DirectHost != null) SendDirect(r.DirectHost, r.DirectPort, slip);
                else _raw.Write(r.RawQueueName ?? throw new InvalidOperationException("no raw queue"), slip, "Dayman QR");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// WO-91: the job <paramref name="seq"/> turned out to print nothing (a printer command, not a receipt): if no
    /// other job came after it, the receipt before it is current again and its pending QR may still print.
    /// </summary>
    public void Release(long seq, long prevSeq)
    {
        foreach (var lane in _lanes.Values)
            lock (lane.Lock)
                if (lane.LastSeq == seq) { lane.LastSeq = prevSeq; return; }
    }

    async Task AcceptLoop(Lane lane)
    {
        var ct = lane.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await lane.Listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { _log.Warn("accept: " + ex.Message); continue; }
            _ = Task.Run(() => lane.Route.Lan ? Proxy(lane, c, ct) : Receive(lane, c, ct));
        }
    }

    async Task Receive(Lane lane, TcpClient client, CancellationToken ct)
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
                        if (buf.Length > 0) _log.Warn("job idle without close: forwarding what arrived");
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
            Forward(lane, buf.ToArray());
        }
    }

    void Forward(Lane lane, byte[] data)
    {
        var route = lane.Route;
        var sw = Stopwatch.StartNew();
        long seq, prev;
        bool unreachable = false;
        // WO-91: a job under 16 bytes (a drawer kick, an init) is not a receipt: forwarded, not analysed, and it does
        // not cancel the previous receipt's QR (no paper came out between them).
        bool tiny = data.Length < JobThreshold;
        try
        {
            lock (lane.Lock)
            {
                // Bumped under the same lock as the QR check: a pending QR can never print after a newer job.
                prev = lane.LastSeq;
                if (tiny) seq = lane.LastSeq;
                else
                {
                    seq = Interlocked.Increment(ref _seq);
                    lane.LastSeq = seq;
                }
                if (route.DirectHost != null)
                {
                    try { SendDirect(route.DirectHost, route.DirectPort, data); }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                    {
                        // The spooler queue on the same TCP/IP port keeps it and retries, as the POS queue would.
                        unreachable = true;
                        _log.Warn($"printer {route.DirectHost}:{route.DirectPort} unreachable ({ex.GetType().Name}): job sent to the spooler queue");
                    }
                }
                if (route.DirectHost == null || unreachable)
                    _raw.Write(route.RawQueueName ?? throw new InvalidOperationException("no raw queue configured"), data, "Dayman Desk");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"FORWARD FAILED ({data.Length} B): {ex.Message}");
            _onForwardFailed(route, data);
            return;
        }
        long ms = sw.ElapsedMilliseconds;
        if (ms > 300) _log.Warn($"forward took {ms} ms ({data.Length} B)");
        if (tiny) return;
        var handler = JobForwarded;
        if (handler != null) _ = Task.Run(() => handler(new ForwardedJob(route, data, ms, seq, unreachable, prev)));
    }

    /// <summary>
    /// Network mode: connect to the printer first (refused → the POS connection is reset, as without us), then pipe
    /// POS → printer (captured for analysis) and printer → POS (status answers) until either side closes or idles.
    /// </summary>
    async Task Proxy(Lane lane, TcpClient client, CancellationToken ct)
    {
        var route = lane.Route;
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote == null || !IsPrivate(remote))
            {
                _log.Warn("network mode: refused a connection from outside the local network");
                try { client.LingerState = new LingerOption(true, 0); } catch { }
                return;
            }
            var printer = new TcpClient { NoDelay = true, SendTimeout = 10_000 };
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(ConnectTimeout);
                await printer.ConnectAsync(route.DirectHost!, route.DirectPort, connect.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                printer.Dispose();
                if (ct.IsCancellationRequested) return;
                _log.Warn($"network printer {route.DirectHost}:{route.DirectPort} unreachable: POS connection refused");
                try { client.LingerState = new LingerOption(true, 0); } catch { }
                PrinterUnreachable?.Invoke(route, ex.GetType().Name);
                return;
            }
            using (printer)
            {
                var cs = client.GetStream();
                var ps = printer.GetStream();
                var back = Task.Run(async () =>
                {
                    try { await ps.CopyToAsync(cs, ct); } catch { } // status bytes back to the POS
                });
                var capture = new MemoryStream();
                var chunk = new byte[64 * 1024];
                long seq = 0, prev = -1, maxMs = 0, total = 0;
                bool counted = false, failed = false;
                while (true)
                {
                    int n;
                    try
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idle.CancelAfter(IdleTimeout);
                        n = await cs.ReadAsync(chunk, idle.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { break; }
                    if (n == 0) break;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        if (!counted && total + n >= JobThreshold)
                        {
                            lock (lane.Lock)
                            {
                                prev = lane.LastSeq;
                                seq = Interlocked.Increment(ref _seq);
                                lane.LastSeq = seq;
                                counted = true;
                                ps.Write(chunk, 0, n);
                            }
                        }
                        else ps.Write(chunk, 0, n);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        _log.Warn($"network printer {route.DirectHost} dropped the connection: {ex.Message}");
                        failed = true;
                        break;
                    }
                    maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds);
                    total += n;
                    if (capture.Length + n <= MaxCapture) capture.Write(chunk, 0, n);
                }
                try { printer.Client.Shutdown(SocketShutdown.Send); } catch { }
                await Task.WhenAny(back, Task.Delay(2000));
                if (failed) { PrinterUnreachable?.Invoke(route, "dropped"); return; }
                if (!counted) return;
                var handler = JobForwarded;
                if (handler != null) _ = Task.Run(() => handler(new ForwardedJob(route, capture.ToArray(), maxMs, seq, false, prev)));
            }
        }
    }

    /// <summary>A LAN or loopback IPv4 address (10/8, 172.16/12, 192.168/16, 169.254/16, 127/8), also IPv4-mapped IPv6.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Sends a whole job to a RAW TCP printer (Standard TCP/IP port 9100), as the Windows port monitor does.</summary>
    public static void SendDirect(string host, int port, byte[] data)
    {
        using var c = new TcpClient { NoDelay = true, SendTimeout = 10_000 };
        using (var cts = new CancellationTokenSource(ConnectTimeout))
            c.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
        c.GetStream().Write(data, 0, data.Length);
        c.LingerState = new LingerOption(true, 5); // close waits until the printer has the bytes
        c.Client.Shutdown(SocketShutdown.Send);
    }

    /// <summary>This PC's IPv4 addresses on the LAN (shown for «وضع الشبكة»).</summary>
    public static IReadOnlyList<string> LocalIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Checks the «وضع الشبكة» settings; null when valid, else the Arabic reason.</summary>
    public static string? ValidateNetwork(string? host, int listenPort, int printerPort, IEnumerable<PrinterRoute> routes, IEnumerable<string> localIps)
    {
        if (!IPAddress.TryParse(host?.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || host!.Trim().Count(c => c == '.') != 3)
            return "اكتب عنوان IP الطابعة الحقيقي، مثل 192.168.1.50.";
        if (IPAddress.IsLoopback(ip) || localIps.Contains(ip.ToString()))
            return "هذا عنوان هذا الجهاز. اكتب عنوان طابعة الفواتير نفسها.";
        if (listenPort < 1024 || listenPort > 65535 || printerPort < 1 || printerPort > 65535)
            return "رقم المنفذ غير صحيح. المنفذ المعتاد للطابعات 9100.";
        var used = routes.Select(r => r.ListenPort).ToList();
        if (used.Contains(listenPort))
            return $"المنفذ {listenPort} مستخدم لطابعة على هذا الجهاز. اختر منفذاً آخر مثل {PrinterSetup.FreePort(used)}.";
        return null;
    }
}
