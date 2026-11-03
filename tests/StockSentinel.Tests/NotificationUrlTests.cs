using Microsoft.Extensions.Configuration;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class NotificationUrlTests
{
    static IConfiguration Config(string? value=null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string,string?> {["Application:BaseUrl"]=value}).Build();

    [Fact]
    public void Existing_native_configuration_keeps_pi_test_url() =>
        Assert.Equal("https://stocksentinel.home/",NotificationUrls.ApplicationBaseUrl(Config()));

    [Theory]
    [InlineData("https://stock.example.com", "https://stock.example.com/")]
    [InlineData("https://stock.example.com/", "https://stock.example.com/")]
    [InlineData("https://stock.example.com:8443", "https://stock.example.com:8443/")]
    public void Configured_https_origin_is_used_for_test_notifications(string value,string expected) =>
        Assert.Equal(expected,NotificationUrls.ApplicationBaseUrl(Config(value)));

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("http://stock.example.com")]
    [InlineData("https://user:secret@stock.example.com")]
    [InlineData("https://stock.example.com/app/")]
    [InlineData("https://stock.example.com/?token=secret")]
    [InlineData("https://stock.example.com/#fragment")]
    public void Unsafe_or_unsupported_application_urls_are_rejected(string value) =>
        Assert.Throws<InvalidOperationException>(()=>NotificationUrls.ApplicationBaseUrl(Config(value)));

    static Source Source(string adapter,string? rules=null) => new(1,1,"Retailer","https://retailer.example/api/product",15,true,
        adapter,null,null,null,null,null,null,"Unknown",null,null,null,0,rules);

    [Fact]
    public void Stock_and_reminder_urls_keep_the_retailer_purchase_url()
    {
        var source=Source("rest-json","""{"idPath":"$.id","expectedId":"sku","availablePath":"$.available","purchaseUrl":"https://retailer.example/buy?sku=example"}""");
        Assert.Equal("https://retailer.example/buy?sku=example",NotificationUrls.ForSource(source));
    }

    [Theory]
    [InlineData("html",null)]
    [InlineData("rest-json","""{"idPath":"$.id","expectedId":"sku","availablePath":"$.available"}""")]
    [InlineData("rest-json","invalid legacy rules")]
    public void Existing_sources_without_purchase_url_keep_their_request_url(string adapter,string? rules)
    {
        var source=Source(adapter,rules);
        Assert.Equal(source.Url,NotificationUrls.ForSource(source));
    }
}
