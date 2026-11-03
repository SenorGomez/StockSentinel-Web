using System.Net;
using System.Net.Sockets;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class NetworkSafetyTests
{
    [Theory]
    [InlineData("127.0.0.1")][InlineData("10.0.0.1")][InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")][InlineData("192.168.1.1")][InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")][InlineData("224.0.0.1")][InlineData("240.0.0.1")]
    [InlineData("192.0.0.1")][InlineData("192.0.2.1")][InlineData("198.18.0.1")][InlineData("203.0.113.1")]
    [InlineData("::1")][InlineData("::")][InlineData("fc00::1")][InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")][InlineData("64:ff9b::a00:1")][InlineData("2002:0a00:0001::1")]
    [InlineData("2001:db8::1")][InlineData("2001::1")][InlineData("3fff::1")]
    public void Nonpublic_and_transition_destinations_are_blocked(string ip) => Assert.False(NetworkSafety.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")][InlineData("1.1.1.1")][InlineData("2606:4700:4700::1111")][InlineData("2001:4860:4860::8888")]
    public void Public_destinations_are_supported(string ip) => Assert.True(NetworkSafety.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task Connection_revalidates_dns_and_never_connects_to_a_rebound_or_mixed_target()
    {
        foreach(var addresses in new[]{new[]{IPAddress.Loopback},new[]{IPAddress.Parse("8.8.8.8"),IPAddress.Parse("10.0.0.1")}})
        {
            var connected=false;
            await Assert.ThrowsAsync<HttpRequestException>(async()=>await NetworkSafety.ConnectPublicAsync(new("example.com",443),default,
                (_,_)=>Task.FromResult(addresses),(_,_)=>{connected=true;return ValueTask.FromResult<Stream>(new MemoryStream());}));
            Assert.False(connected);
        }
    }
    [Fact]
    public async Task Connection_pins_the_resolved_address_without_a_second_dns_lookup()
    {
        var resolutions=0;IPEndPoint? connected=null;
        using var stream=await NetworkSafety.ConnectPublicAsync(new("example.com",443),default,
            (_,_)=>Task.FromResult(++resolutions==1?new[]{IPAddress.Parse("8.8.8.8")}:new[]{IPAddress.Loopback}),
            (endpoint,_)=>{connected=endpoint;return ValueTask.FromResult<Stream>(new MemoryStream());});
        Assert.Equal(1,resolutions);Assert.Equal(IPAddress.Parse("8.8.8.8"),connected!.Address);Assert.Equal(443,connected.Port);
    }
    [Fact]
    public async Task Connection_falls_back_only_among_validated_public_addresses()
    {
        var attempts=new List<IPAddress>();
        using var stream=await NetworkSafety.ConnectPublicAsync(new("example.com",443),default,
            (_,_)=>Task.FromResult(new[]{IPAddress.Parse("8.8.8.8"),IPAddress.Parse("1.1.1.1")}),
            (endpoint,_)=>{attempts.Add(endpoint.Address);if(attempts.Count==1)throw new SocketException();return ValueTask.FromResult<Stream>(new MemoryStream());});
        Assert.Equal(2,attempts.Count);
    }
    [Fact]
    public async Task Cancellation_is_preserved_and_redirects_proxies_and_cookies_are_disabled()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await NetworkSafety.ConnectPublicAsync(new("example.com",443),cancelled.Token,
            (_,ct)=>Task.FromCanceled<IPAddress[]>(ct)));
        using var handler=NetworkSafety.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);Assert.False(handler.UseProxy);Assert.False(handler.UseCookies);Assert.NotNull(handler.ConnectCallback);
    }
}
