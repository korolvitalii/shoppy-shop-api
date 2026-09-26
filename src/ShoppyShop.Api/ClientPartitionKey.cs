using System.Net;
using System.Net.Sockets;

namespace ShoppyShop.Api;

/// <summary>
/// The rate-limit partition for the caller's address, shared by every per-address policy.
/// </summary>
public static class ClientPartitionKey
{
    public static string For(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        // An IPv6 subscriber is normally handed a whole /64, so keying on the full address would let
        // one client step through 2^64 fresh budgets without changing networks. Key on the prefix.
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return $"{new IPAddress(bytes)}/64";
    }
}