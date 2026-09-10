using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChopItUp.Hub.Security;

/// <summary>Row 29: which local process owns the client end of a loopback TCP connection. Reads the
/// owner-PID table for the peer's address family (the same tables <c>netstat -ano</c> prints) and
/// matches the row whose LOCAL address and port are the peer Kestrel reports, in the ESTABLISHED
/// state — the port alone is not enough, a TIME_WAIT row can share it. The hub listens on both
/// <c>127.0.0.1</c> and <c>[::1]</c> (HubHost), and <c>localhost</c> resolves to <c>::1</c> first on
/// Windows, so the browser's connections are IPv6 — both tables are read. Measured 2026-09-10 for
/// IPv4 in-process: server-seen remote port == client local port, owning PID == the client.</summary>
[SupportedOSPlatform("windows")]
public static partial class PeerProcess
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint MibTcpStateEstablished = 5;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    /// <summary>MIB_TCP6ROW_OWNER_PID: 16 address bytes as two ulongs (same in-memory order as
    /// <see cref="IPAddress.GetAddressBytes"/>), scope id, port, the remote triple, then state and
    /// pid — note State comes LAST here, unlike the IPv4 row.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        public ulong LocalAddr0, LocalAddr1;
        public uint LocalScopeId, LocalPort;
        public ulong RemoteAddr0, RemoteAddr1;
        public uint RemoteScopeId, RemotePort, State, OwningPid;
    }

    /// <summary>Internal (not <c>private</c>) and test-callable via <c>InternalsVisibleTo</c>: a test
    /// can fall through to the real syscall for some calls of a fake <see cref="TcpTableFn"/> while
    /// forcing others to fail, to prove <see cref="Scan"/>'s retry loop without touching the
    /// production call path (<see cref="OwningPid(Endpoints)"/> never passes a non-null one).</summary>
    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    internal static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);

    /// <summary>Row 29 finding: the shape of the one Win32 call <see cref="Scan"/> makes twice per
    /// attempt, so a test can substitute it. Matches <see cref="GetExtendedTcpTable"/>'s signature
    /// exactly (marshalling attributes are only meaningful at the P/Invoke boundary itself, already
    /// applied by the generated implementation, so the delegate type omits them).</summary>
    internal delegate uint TcpTableFn(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    private const int MaxScanAttempts = 5;

    /// <summary>Headroom added on top of the measured size so a small growth in the table between the
    /// size query and the read that follows does not by itself force a retry.</summary>
    private const int ScanHeadroomBytes = 4096;

    private static int HostPort(uint networkOrderPort) => (int)(((networkOrderPort & 0xFF) << 8) | ((networkOrderPort >> 8) & 0xFF));

    /// <summary>The two ends of one connection as Kestrel reports them: the peer (client) end and the
    /// hub's own listening end. Both are matched — the peer's address:port alone is not unique when a
    /// second ESTABLISHED row shares it (a listener on that port, SO_REUSEADDR).</summary>
    public readonly record struct Endpoints(IPAddress RemoteAddress, int RemotePort, IPAddress LocalAddress, int LocalPort);

    /// <summary>Null when the peer is neither IPv4 (or IPv4-mapped) nor IPv6, the table cannot be
    /// read, or no ESTABLISHED row joins that peer address:port to that hub address:port.</summary>
    public static int? OwningPid(Endpoints e) => OwningPid(e, tableCall: null);

    /// <summary>Test-only seam (row 29 finding): <paramref name="tableCall"/> is null on every
    /// production call site and <see cref="Scan"/> then calls the real
    /// <see cref="GetExtendedTcpTable"/> exactly as before; a test can pass a fake to force the
    /// retry path without a real table ever needing to grow mid-scan.</summary>
    internal static int? OwningPid(Endpoints e, TcpTableFn? tableCall)
    {
        var peer = e.RemoteAddress.IsIPv4MappedToIPv6 ? e.RemoteAddress.MapToIPv4() : e.RemoteAddress;
        var hub = e.LocalAddress.IsIPv4MappedToIPv6 ? e.LocalAddress.MapToIPv4() : e.LocalAddress;
        if (peer.AddressFamily != hub.AddressFamily) return null;
        var want = new Wanted(peer.GetAddressBytes(), e.RemotePort, hub.GetAddressBytes(), e.LocalPort);
        return peer.AddressFamily switch
        {
            AddressFamily.InterNetwork => Scan(AfInet, Marshal.SizeOf<MibTcpRowOwnerPid>(), want, MatchV4, tableCall),
            AddressFamily.InterNetworkV6 => Scan(AfInet6, Marshal.SizeOf<MibTcp6RowOwnerPid>(), want, MatchV6, tableCall),
            _ => null,
        };
    }

    /// <summary>In table terms the PEER is the row's LOCAL end (the row belongs to the client's
    /// socket) and the HUB is the row's REMOTE end.</summary>
    private readonly record struct Wanted(byte[] PeerAddr, int PeerPort, byte[] HubAddr, int HubPort);

    private static int? MatchV4(nint row, Wanted w)
    {
        var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(row);
        if (r.State != MibTcpStateEstablished) return null;
        if (HostPort(r.LocalPort) != w.PeerPort || HostPort(r.RemotePort) != w.HubPort) return null;
        if (r.LocalAddr != BitConverter.ToUInt32(w.PeerAddr, 0) || r.RemoteAddr != BitConverter.ToUInt32(w.HubAddr, 0)) return null;
        return (int)r.OwningPid;
    }

    private static int? MatchV6(nint row, Wanted w)
    {
        var r = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(row);
        if (r.State != MibTcpStateEstablished) return null;
        if (HostPort(r.LocalPort) != w.PeerPort || HostPort(r.RemotePort) != w.HubPort) return null;
        if (r.LocalAddr0 != BitConverter.ToUInt64(w.PeerAddr, 0) || r.LocalAddr1 != BitConverter.ToUInt64(w.PeerAddr, 8)) return null;
        if (r.RemoteAddr0 != BitConverter.ToUInt64(w.HubAddr, 0) || r.RemoteAddr1 != BitConverter.ToUInt64(w.HubAddr, 8)) return null;
        return (int)r.OwningPid;
    }

    /// <summary>Row 29 finding: the table can grow between the size query and the read that follows,
    /// which used to make the read return ERROR_INSUFFICIENT_BUFFER and this whole method null — the
    /// middleware reads that as an unresolvable peer and locks the owner out on ordinary network
    /// churn, not an attack. Sizes fresh on every attempt (never reuses a stale size), pads the
    /// allocation with <see cref="ScanHeadroomBytes"/> so a small growth in the gap does not by
    /// itself force a retry, and gives up only after <see cref="MaxScanAttempts"/> attempts or a
    /// failure that is not ERROR_INSUFFICIENT_BUFFER. Frees the buffer on every path, including the
    /// retry path, via the <c>finally</c>.</summary>
    private static int? Scan(int family, int rowSize, Wanted want, Func<nint, Wanted, int?> match, TcpTableFn? tableCall = null)
    {
        var call = tableCall ?? GetExtendedTcpTable;
        for (var attempt = 0; attempt < MaxScanAttempts; attempt++)
        {
            int size = 0;
            var sizing = call(0, ref size, false, family, TcpTableOwnerPidAll, 0);
            if (sizing != ErrorInsufficientBuffer && sizing != NoError) return null;
            var bufferSize = size + ScanHeadroomBytes;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var read = call(buffer, ref bufferSize, false, family, TcpTableOwnerPidAll, 0);
                if (read == NoError)
                {
                    int count = Marshal.ReadInt32(buffer);
                    var rows = buffer + 4;
                    for (int i = 0; i < count; i++)
                    {
                        var pid = match(rows + i * rowSize, want);
                        if (pid is not null) return pid;
                    }
                    return null;
                }
                if (read != ErrorInsufficientBuffer) return null;
                // the table outgrew even the headroom in the gap between the size query and this
                // read: loop around and measure it again fresh rather than trusting the old number.
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null; // retries exhausted: the table would not hold still long enough to read
    }
}
