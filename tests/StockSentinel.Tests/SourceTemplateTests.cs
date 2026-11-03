using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class SourceTemplateTests
{
    static readonly IConfiguration Config = new ConfigurationBuilder().Build();
    const string Rules = """{"idPath":"$.product.id","expectedId":"SKU/A","availablePath":"$.product.available","pricePath":"$.product.price","currencyPath":"$.product.currency","purchaseUrl":"https://example.com/buy?id=SKU%2FA"}""";
    static Source Example(string adapter = "html", string url = "https://example.com/product?q=console&limit=6", string? rules = Rules) =>
        new(42,7,"Retailer",url,15,true,adapter,".available",".unavailable",".price",@"£\s*(?<price>[0-9.]+)","Buy","Sold out",
            "Available","private runtime diagnostic",DateTimeOffset.UtcNow.ToString("O"),"next check",3,rules);
    static JsonElement Document(SourceTemplate t) => JsonSerializer.Deserialize<JsonElement>(SourceTemplates.Bytes(t));
    static SourceTemplate Read(string json, IConfiguration? config = null) => SourceTemplates.Read(JsonSerializer.Deserialize<JsonElement>(json),config ?? Config);
    static string File(Source source) => Encoding.UTF8.GetString(SourceTemplates.Bytes(SourceTemplates.Export(source,Config)));

    [Fact]
    public void Apple_pickup_example_can_be_exported_and_imported()
    {
        const string url="https://www.apple.com/uk/shop/retail/pickup-message?pl=true&parts.0=MK2E4QN%2FA&location=RM1%204HB";
        var template=SourceTemplates.Export(Example("rest-json",url),Config);
        var imported=SourceTemplates.ToSource(SourceTemplates.Read(Document(template),Config),99);
        Assert.Equal(url,imported.Url);
        Assert.False(imported.Enabled);
    }

    [Theory]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=false&parts.0=MK2E4QN%2FA&parts.1=MTJV3B%2FA&location=RM1+4HB")]
    [InlineData("https://www.apple.com/shop/retail/pickup-message?pl=true&parts.12=MK2E4QN%2FA&location=90210")]
    [InlineData("https://www.apple.com/fr/shop/retail/pickup-message?parts.01=MK2E4QN%2FA")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=true")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.0=MK2E4QN%2FA")]
    [InlineData("https://example.com/pickup?location=RM1%204HB&parts=MK2E4QN%2FA")]
    public void Apple_pickup_parameters_round_trip_without_changing_other_retailers(string url)
    {
        var exported=SourceTemplates.Export(Example("rest-json",url),Config);
        Assert.Equal(url,exported.Source.Url);
        // Check import independently rather than relying on export sanitising the URL.
        var template=SourceTemplates.Export(Example("rest-json"),Config);
        var imported=SourceTemplates.Read(Document(template with {Source=template.Source with {Url=url}}),Config);
        Assert.Equal(url,imported.Source.Url);
    }

    [Theory]
    [InlineData("https://example.com/uk/shop/retail/pickup-message?pl=true")]
    [InlineData("https://example.com/uk/shop/retail/pickup-message?parts.0=MK2E4QN%2FA")]
    [InlineData("https://www.apple.com.evil.example/uk/shop/retail/pickup-message?pl=true")]
    [InlineData("https://www.apple.com@evil.example/uk/shop/retail/pickup-message?parts.0=MK2E4QN%2FA")]
    [InlineData("http://www.apple.com/uk/shop/retail/pickup-message?pl=true")]
    [InlineData("https://www.apple.com:8443/uk/shop/retail/pickup-message?parts.0=MK2E4QN%2FA")]
    [InlineData("https://www.apple.com/uk/shop/product?pl=true")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message/extra?parts.0=MK2E4QN%2FA")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.=sku")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.-1=sku")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.1e2=sku")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.1.token=private")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.0%0A=sku")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.١=sku")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=1")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=true&token=private")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.0=sku&%73ignature=private")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=true&unknown=private")]
    [InlineData("https://user:password@www.apple.com/uk/shop/retail/pickup-message?pl=true")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?pl=true#token=private")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.0=Bearer+private")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.0=api_key%3Dprivate")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?parts.0=signature%3Dprivate")]
    [InlineData("https://www.apple.com/uk/shop/retail/pickup-message?location=password%3Dprivate")]
    public void Apple_exceptions_do_not_allow_unsafe_urls_or_parameters(string url)
    {
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example("rest-json",url),Config));
        var template=SourceTemplates.Export(Example("rest-json"),Config);
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Read(Document(template with {Source=template.Source with {Url=url}}),Config));
    }

    [Fact]
    public void Apple_pickup_validation_also_applies_to_json_purchase_urls()
    {
        const string url="https://www.apple.com/uk/shop/retail/pickup-message?pl=true&parts.0=MK2E4QN%2FA&location=RM1%204HB";
        var rules=Rules.Replace("https://example.com/buy?id=SKU%2FA",url);
        var template=SourceTemplates.Export(Example("rest-json",rules:rules),Config);
        Assert.Equal(url,JsonStockDetector.Validate(SourceTemplates.Read(Document(template),Config).Source.JsonConfig!.Value.GetRawText()).PurchaseUrl);
        var unsafeRules=rules.Replace("pl=true","pl=true&token=private");
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example("rest-json",rules:unsafeRules),Config));
        var changed=template with {Source=template.Source with {JsonConfig=JsonSerializer.Deserialize<JsonElement>(unsafeRules)}};
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Read(Document(changed),Config));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("rest-json")]
    public void Export_import_round_trip_preserves_reusable_settings(string adapter)
    {
        var original = Example(adapter);
        var exported = SourceTemplates.Export(original,Config);
        var imported = SourceTemplates.ToSource(SourceTemplates.Read(Document(exported),Config),99);
        Assert.Equal(99,imported.ItemId);
        Assert.False(imported.Enabled);
        Assert.Equal(original.Name,imported.Name);
        Assert.Equal(original.Url,imported.Url);
        Assert.Equal(original.IntervalMinutes,imported.IntervalMinutes);
        Assert.Equal(adapter,imported.Adapter);
        if(adapter=="html")
        {
            Assert.Equal(original.AvailableSelector,imported.AvailableSelector);
            Assert.Equal(original.UnavailableSelector,imported.UnavailableSelector);
            Assert.Equal(original.PriceSelector,imported.PriceSelector);
            Assert.Equal(original.PricePattern,imported.PricePattern);
            Assert.Equal(original.AvailabilityText,imported.AvailabilityText);
            Assert.Equal(original.UnavailabilityText,imported.UnavailabilityText);
            Assert.Null(imported.JsonConfig);
        }
        else
        {
            Assert.Equal(JsonStockDetector.Validate(Rules).PurchaseUrl,JsonStockDetector.Validate(imported.JsonConfig!).PurchaseUrl);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(Rules),System.Text.Json.Nodes.JsonNode.Parse(imported.JsonConfig!)));
            Assert.Null(imported.AvailableSelector);
        }
    }

    [Fact]
    public void Export_uses_allowlist_without_database_runtime_or_unused_secret_fields()
    {
        var json = File(Example(rules:"{\"password\":\"DO-NOT-EXPORT\"}"));
        foreach(var value in new[] { "itemId", "sourceId", "enabled", "status", "lastDetail", "lastCheckedUtc", "nextCheckUtc", "failCount", "private runtime diagnostic", "DO-NOT-EXPORT", "jsonConfig" })
            Assert.DoesNotContain(value,json);
        Assert.Equal(new[] { "format", "version", "source" },Document(SourceTemplates.Export(Example(),Config)).EnumerateObject().Select(p=>p.Name));
    }

    [Theory]
    [InlineData("https://user:password@example.com/product")]
    [InlineData("https://example.com/product?api_key=private")]
    [InlineData("https://example.com/product?%74oken=private")]
    [InlineData("https://example.com/product?signature=private")]
    [InlineData("https://example.com/product?unknown=private")]
    [InlineData("https://example.com/product#access_token=private")]
    [InlineData("https://example.com/product?q=Bearer%20private")]
    public void Sensitive_or_unapproved_urls_cannot_be_exported_or_imported(string url)
    {
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example(url:url),Config));
        var template=SourceTemplates.Export(Example(),Config);
        template=template with { Source=template.Source with {Url=url} };
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Read(Document(template),Config));
    }

    [Fact]
    public void Purchase_urls_and_unknown_json_rule_fields_are_checked()
    {
        foreach(var rules in new[] { Rules.Replace("https://example.com/buy?id=SKU%2FA","https://user:pass@example.com/buy"),
            Rules.Replace("https://example.com/buy?id=SKU%2FA","https://example.com/buy?token=private"),
            Rules.Replace("\"idPath\"","\"password\":\"private\",\"idPath\"") })
            Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example("rest-json",rules:rules),Config));
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example() with {Name="Authorization: Bearer private"},Config));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"stocksentinel-source\",\"version\":2,\"source\":{}}")]
    [InlineData("{\"format\":\"stocksentinel-source\",\"version\":1,\"source\":null}")]
    public void Invalid_envelopes_are_rejected(string json) => Assert.Throws<ArgumentException>(()=>Read(json));

    [Theory]
    [InlineData("\"adapter\": \"html\"","\"adapter\": \"script\"")]
    [InlineData("\"intervalMinutes\": 15","\"intervalMinutes\": 0")]
    [InlineData("\"intervalMinutes\": 15","\"intervalMinutes\": 1441")]
    [InlineData("\"name\": \"Retailer\"","\"name\": null")]
    [InlineData("\"name\": \"Retailer\"","\"name\": \"Retailer\", \"enabled\": true")]
    [InlineData("\"version\": 1","\"version\": 1, \"id\": 42")]
    [InlineData("\"version\": 1","\"version\": 1, \"version\": 1")]
    public void Unsupported_settings_and_duplicate_properties_are_rejected(string before,string after) =>
        Assert.Throws<ArgumentException>(()=>Read(File(Example()).Replace(before,after)));

    [Fact]
    public void Invalid_detection_rules_and_oversized_templates_are_rejected()
    {
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example("rest-json",rules:"{}"),Config));
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example() with {PricePattern="["},Config));
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Export(Example() with {PricePattern="[0-9]+"},Config));
        Assert.Throws<ArgumentException>(()=>SourceTemplates.Read(default,Config));
        Assert.Throws<ArgumentException>(()=>Read(File(Example()).Replace("Retailer",new string('x',SourceTemplates.MaxBytes))));
        var strict=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Sentinel:MinimumPollMinutes"]="30"}).Build();
        Assert.Throws<ArgumentException>(()=>Read(File(Example()),strict));
    }

    [Fact]
    public void Incomplete_html_stays_disabled_and_existing_enable_validation_is_preserved()
    {
        var template=SourceTemplates.Export(Example() with {AvailableSelector=null,UnavailableSelector=null},Config);
        var source=SourceTemplates.ToSource(template,1);
        Assert.False(source.Enabled);
        Assert.Null(SourceTemplates.ValidateSettings(source,Config));
        Assert.NotNull(SourceTemplates.ValidateSettings(source with {Enabled=true},Config));
    }

    [Fact]
    public void Import_creates_separate_disabled_source_without_modifying_existing_data()
    {
        var directory=Path.Combine(Path.GetTempPath(),"stocksentinel-template-"+Guid.NewGuid());
        try
        {
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                {["Sentinel:DatabasePath"]=Path.Combine(directory,"test.db")}).Build();
            var store=new Store(config);store.Init();
            var product=store.AddItem(new NewItem("Local product",55));
            var source=SourceTemplates.ToSource(SourceTemplates.Export(Example(),Config),product);
            var originalId=store.AddSource(source with {Enabled=true});
            var original=store.GetSource(originalId);
            store.Log("info","Existing history",originalId);
            store.MarkNotified(originalId);
            var imported=SourceTemplates.ToSource(SourceTemplates.Read(Document(SourceTemplates.Export(original!,Config)),Config),product);
            var importedId=store.AddSource(imported);
            Assert.NotEqual(originalId,importedId);
            Assert.Equal(original,store.GetSource(originalId));
            Assert.Equal(2,store.Sources().Count);
            Assert.False(store.GetSource(importedId)!.Enabled);
            Assert.Equal(product,store.GetSource(importedId)!.ItemId);
            Assert.True(store.HasNotified(originalId));
            Assert.False(store.HasNotified(importedId));
            Assert.Single(store.Events());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
    }

    [Fact]
    public void Collection_rules_and_existing_case_insensitive_rules_are_preserved()
    {
        var rules=Rules.Replace("\"idPath\"","\"IdPath\"").Replace("{","{\"itemsPath\":\"$.stores[*]\",\"labelPath\":\"$.name\",\"matchMode\":\"all\",",StringComparison.Ordinal);
        var imported=SourceTemplates.ToSource(SourceTemplates.Export(Example("rest-json",rules:rules),Config),1);
        var parsed=JsonStockDetector.Validate(imported.JsonConfig!);
        Assert.Equal("$.stores[*]",parsed.ItemsPath);
        Assert.Equal("$.name",parsed.LabelPath);
        Assert.Equal("all",parsed.MatchMode);
        Assert.Equal("$.product.id",parsed.IdPath);
    }

    [Fact]
    public void Duplicate_detection_warns_for_matching_name_or_url()
    {
        var existing=Example();
        var source=SourceTemplates.ToSource(SourceTemplates.Export(existing,Config),99);
        Assert.True(SourceTemplates.IsDuplicate(existing,source with {Name="Different"}));
        Assert.True(SourceTemplates.IsDuplicate(existing,source with {Name="RETAILER",Url="https://other.example.com"}));
        Assert.False(SourceTemplates.IsDuplicate(existing,source with {Name="Different",Url="https://other.example.com"}));
    }
}
