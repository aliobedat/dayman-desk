// WO-95: the few .NET Core APIs this code uses, for netstandard2.0 (the program runs on .NET Framework 4.8).
// Built for netstandard2.0 and, linked, for the net48 program; the Linux test build (net8) uses the real ones.
#if NETSTANDARD2_0 || NETFRAMEWORK
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System
{
    internal readonly struct Index
    {
        readonly int _value;
        public Index(int value, bool fromEnd = false) => _value = fromEnd ? ~value : value;
        public static implicit operator Index(int value) => new(value);
        public bool IsFromEnd => _value < 0;
        public int Value => _value < 0 ? ~_value : _value;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static Index Start => new(0);
        public static Index End => new(0, true);
    }

    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int s = Start.GetOffset(length), e = End.GetOffset(length);
            return (s, e - s);
        }
    }
}

namespace DaymanDesk.Core
{
    internal static class OperatingSystem
    {
        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    internal static class Compat
    {
        public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
        public static bool Contains(this string s, string v, StringComparison cmp) => s.IndexOf(v, cmp) >= 0;
        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
        public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
        public static string[] Split(this string s, char c, StringSplitOptions o) => s.Split(new[] { c }, o);

        public static bool TryAdd<TK, TV>(this Dictionary<TK, TV> d, TK k, TV v) where TK : notnull
        {
            if (d.ContainsKey(k)) return false;
            d.Add(k, v);
            return true;
        }

        public static TV? GetValueOrDefault<TK, TV>(this IReadOnlyDictionary<TK, TV> d, TK k) =>
            d.TryGetValue(k, out var v) ? v : default;

        public static Task<string> ReadAsStringAsync(this HttpContent c, CancellationToken _) => c.ReadAsStringAsync();

        public static Task CopyToAsync(this Stream s, Stream dest, CancellationToken ct) => s.CopyToAsync(dest, 81920, ct);

        /// <summary>A network read that really ends on cancel (.NET Framework ignores the token once the read is pending).</summary>
        public static async Task<int> ReadAsync(this Stream s, byte[] buf, CancellationToken ct)
        {
            using (ct.Register(() => { try { s.Dispose(); } catch { } }))
            {
                try { return await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }

        public static async Task<TcpClient> AcceptTcpClientAsync(this TcpListener l, CancellationToken ct)
        {
            using (ct.Register(() => { try { l.Stop(); } catch { } }))
            {
                try { return await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }

        public static async ValueTask ConnectAsync(this TcpClient c, string host, int port, CancellationToken ct)
        {
            using (ct.Register(() => { try { c.Close(); } catch { } }))
            {
                try { await c.ConnectAsync(host, port).ConfigureAwait(false); }
                catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }
    }
}
#endif

namespace DaymanDesk.Core
{
    internal static class FileUtil
    {
        /// <summary>File.Move(src, dst, overwrite: true) for every target framework.</summary>
        public static void MoveOverwrite(string src, string dst)
        {
            if (File.Exists(dst)) File.Replace(src, dst, null);
            else File.Move(src, dst);
        }
    }
}
