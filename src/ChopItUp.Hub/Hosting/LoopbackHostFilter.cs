using System.Net;
using Microsoft.AspNetCore.Http;

namespace ChopItUp.Hub.Hosting;

/// <summary>Row 29 finding (ticket 04): Windows PowerShell 5.1's <c>Invoke-WebRequest</c> (on .NET
/// Framework's <c>HttpWebRequest</c>) sends the loopback IPv6 address as the fully expanded,
/// non-compressed literal - <c>[0000:0000:0000:0000:0000:0000:0000:0001]</c> - never the RFC 5952
/// canonical <c>[::1]</c>. That is the same address spelled two ways, so a Host-header gate that
/// accepts one and not the other is refusing a legitimate loopback caller, not enforcing a boundary.
/// This predicate answers "is this Host header loopback" by parsing the address rather than by string
/// list, so every spelling of 127.0.0.1 and ::1 (and the literal word <c>localhost</c>) is accepted,
/// and anything that is not loopback - by address or by name - is not. <see cref="Hosting.HubHost"/>
/// wires this in as the hub's only Host-header gate (its own <c>AllowedHosts</c> config entry is left
/// at <c>*</c>, which disables the built-in list-based filtering).</summary>
internal static class LoopbackHostFilter
{
    /// <summary>True when <paramref name="hostHeaderValue"/> (a Host header's raw value, with or
    /// without a port) names loopback: the literal word <c>localhost</c>, or any textual form of
    /// <c>127.0.0.1</c> / <c>::1</c>, bracketed or not. False for anything else, including a value
    /// that fails to parse as a host at all.</summary>
    public static bool IsLoopback(string? hostHeaderValue)
    {
        if (string.IsNullOrEmpty(hostHeaderValue)) return false;
        // HostString.Host strips the port (and keeps an IPv6 literal's brackets, since the bracket is
        // what tells GetParts where the address ends and the port separator begins).
        var host = new HostString(hostHeaderValue).Host;
        if (host.Length == 0) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var literal = host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        return IPAddress.TryParse(literal, out var address) && IPAddress.IsLoopback(address);
    }
}
