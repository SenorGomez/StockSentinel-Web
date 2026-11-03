using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace StockSentinel;

public static class AuthenticationSecurity
{
    public static void ConfigureProxy(ForwardedHeadersOptions options, IConfiguration config)
    {
        var address=config["ReverseProxy:KnownProxy"] ?? "127.0.0.1";
        if(!IPAddress.TryParse(address,out var proxy)||proxy.Equals(IPAddress.Any)||proxy.Equals(IPAddress.IPv6Any)
            ||(proxy.IsIPv4MappedToIPv6&&proxy.MapToIPv4().Equals(IPAddress.Any)))
            throw new InvalidOperationException("ReverseProxy:KnownProxy must be a specific proxy IP address");
        // Only the scheme is needed for TLS termination. Do not trust remote
        // client/host headers or any network beyond the one explicitly trusted IP.
        options.ForwardedHeaders=ForwardedHeaders.XForwardedProto;
        options.ForwardLimit=1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(proxy);
    }

    public static CookieOptions CookieOptions(HttpRequest request, IHostEnvironment environment) => new()
    {
        HttpOnly=true,
        SameSite=SameSiteMode.Strict,
        Secure=!environment.IsDevelopment()||request.IsHttps,
        MaxAge=TimeSpan.FromHours(12)
    };
}
