using System.Net;

using Microsoft.AspNetCore.Http;

using ShoppyShop.Api;

namespace ShoppyShop.IntegrationTests;

public sealed class ClientPartitionKeyTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("203.0.113.10", "203.0.113.10")]
    [InlineData("::ffff:203.0.113.10", "203.0.113.10")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    public void KeysOnTheAddressAndOnTheWholeIpv6Prefix(string? address, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);

        Assert.Equal(expected, ClientPartitionKey.For(context));
    }
}