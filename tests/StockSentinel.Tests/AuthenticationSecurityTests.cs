using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class AuthenticationSecurityTests
{
    static ForwardedHeadersOptions ProxyOptions(string? address=null)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {["ReverseProxy:KnownProxy"]=address}).Build();
        var options=new ForwardedHeadersOptions();AuthenticationSecurity.ConfigureProxy(options,config);return options;
    }
    static DefaultHttpContext Context(string remote,string forwardedProto)
    {
        var context=new DefaultHttpContext();context.Request.Scheme="http";context.Request.Host=new("stocksentinel.home");
        context.Connection.RemoteIpAddress=IPAddress.Parse(remote);
        context.Request.Headers["X-Forwarded-Proto"]=forwardedProto;
        context.Request.Headers["X-Forwarded-For"]="198.51.100.20";
        context.Request.Headers["X-Forwarded-Host"]="untrusted.example";
        return context;
    }
    static ForwardedHeadersMiddleware Middleware(RequestDelegate next,ForwardedHeadersOptions options) =>
        new(next,NullLoggerFactory.Instance,Options.Create(options));

    [Theory]
    [InlineData("127.0.0.1","https","https")]
    [InlineData("::ffff:127.0.0.1","https","https")]
    [InlineData("203.0.113.5","https","http")]
    [InlineData("127.0.0.2","https","http")]
    [InlineData("::1","https","http")]
    [InlineData("127.0.0.1","https, http","http")]
    public async Task Only_configured_proxy_and_one_scheme_hop_are_trusted(string remote,string header,string expectedScheme)
    {
        var context=Context(remote,header);
        await Middleware(_=>Task.CompletedTask,ProxyOptions()).Invoke(context);
        Assert.Equal(expectedScheme,context.Request.Scheme);
        Assert.Equal(IPAddress.Parse(remote),context.Connection.RemoteIpAddress);
        Assert.Equal("stocksentinel.home",context.Request.Host.Host);
    }

    [Fact]
    public async Task Explicit_ipv6_loopback_proxy_is_supported_without_trusting_ipv4()
    {
        var options=ProxyOptions("::1");
        var trusted=Context("::1","https");
        await Middleware(_=>Task.CompletedTask,options).Invoke(trusted);
        Assert.True(trusted.Request.IsHttps);
        var untrusted=Context("127.0.0.1","https");
        await Middleware(_=>Task.CompletedTask,options).Invoke(untrusted);
        Assert.False(untrusted.Request.IsHttps);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Single(options.KnownProxies);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.1")]
    [InlineData("localhost")]
    public void Non_loopback_or_invalid_proxy_configuration_is_rejected(string address) =>
        Assert.Throws<InvalidOperationException>(()=>ProxyOptions(address));

    [Theory]
    [InlineData("Production","http",true)]
    [InlineData("Production","https",true)]
    [InlineData("Staging","http",true)]
    [InlineData("Development","http",false)]
    [InlineData("Development","https",true)]
    public void Cookie_security_requires_https_outside_development(string environment,string scheme,bool secure)
    {
        var context=new DefaultHttpContext();context.Request.Scheme=scheme;
        var cookie=AuthenticationSecurity.CookieOptions(context.Request,new EnvironmentStub(environment));
        Assert.Equal(secure,cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict,cookie.SameSite);
        Assert.Equal(TimeSpan.FromHours(12),cookie.MaxAge);
    }

    [Fact]
    public async Task Proxied_production_login_cookie_is_secure_with_existing_security_flags()
    {
        var context=Context("127.0.0.1","https");
        await Middleware(ctx=>
        {
            Assert.True(ctx.Request.IsHttps);
            ctx.Response.Cookies.Append("sentinel_auth","test-key",AuthenticationSecurity.CookieOptions(ctx.Request,new EnvironmentStub("Production")));
            return Task.CompletedTask;
        },ProxyOptions()).Invoke(context);
        var header=context.Response.Headers.SetCookie.ToString();
        Assert.Contains("; secure",header,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly",header,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict",header,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=43200",header,StringComparison.OrdinalIgnoreCase);
    }

    sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName {get;set;}=name;
        public string ApplicationName {get;set;}="StockSentinel.Tests";
        public string ContentRootPath {get;set;}="";
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    }
}
