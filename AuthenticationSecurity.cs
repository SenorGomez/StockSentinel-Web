using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace StockSentinel;

public static class AuthenticationSecurity
{
    public static void ConfigureProxy(ForwardedHeadersOptions options, IConfiguration config)
    {
        var address=config["ReverseProxy:KnownProxy"] ?? "127.0.0.1";
        if(!IPAddress.TryParse(address,out var proxy)||!IPAddress.IsLoopback(proxy))
            throw new InvalidOperationException("ReverseProxy:KnownProxy must be a loopback IP address");
        // Only the scheme is needed for TLS termination. Do not trust remote
        // client/host headers or the framework's broader default loopback networks.
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
