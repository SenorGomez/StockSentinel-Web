using System.Text;
using System.Text.Json;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public class JsonStockDetectorTests
{
    private static CheckResult Detect(string json, string rules) =>
        JsonStockDetector.Detect(Encoding.UTF8.GetBytes(json), rules);

    private const string AppleRules = """
    {"idPath":"$.body.stores[0].partsAvailability[\"MK2D4QN/A\"].partNumber","expectedId":"MK2D4QN/A","availablePath":"$.body.stores[0].partsAvailability[\"MK2D4QN/A\"].pickupDisplay","availableValue":"available"}
    """;

    private const string NintendoRules = """
    {"idPath":"$.data.products[0].id","expectedId":"000000000010004546","availablePath":"$.data.products[0].inventory.orderable","confirmationPath":"$.data.products[0].c_availabilityModel.orderable","preorderPath":"$.data.products[0].inventory.preorderable","pricePath":"$.data.products[0].price.value","currencyPath":"$.data.products[0].price.currencyCode","currency":"GBP"}
    """;

    private const string CollectionRules = """
    {"itemsPath":"$.body.stores[*]","labelPath":"$.storeName","matchMode":"any","idPath":"$.partsAvailability[\"MK2D4QN/A\"].partNumber","expectedId":"MK2D4QN/A","availablePath":"$.partsAvailability[\"MK2D4QN/A\"].pickupDisplay","availableValue":"available"}
    """;

    private static string Apple(string? state, string sku = "MK2D4QN/A") =>
        JsonSerializer.Serialize(new { body = new { stores = new[] {
            new { storeName = "Regent Street", partsAvailability = new Dictionary<string, object> {
                ["MK2D4QN/A"] = new { partNumber = sku, pickupDisplay = state }
            }}
        }}});
    private static string Nintendo(bool available, bool confirm, bool preorder = false, string currency = "GBP") =>
        JsonSerializer.Serialize(new { data = new { products = new[] {
            new { id = "000000000010004546", inventory = new { orderable = available, preorderable = preorder },
                c_availabilityModel = new { orderable = confirm }, price = new { value = 49.99m, currencyCode = currency }}
        }}});
    private static string Stores(params (string name, string? state)[] stores) =>
        JsonSerializer.Serialize(new { body = new { stores = stores.Select(s =>
            new { storeName = s.name, partsAvailability = new Dictionary<string, object> {
                ["MK2D4QN/A"] = new { partNumber = "MK2D4QN/A", pickupDisplay = s.state }
            }}).ToArray() }});

    [Theory]
    [InlineData("available", "Available")]
    [InlineData("unavailable", "Unavailable")]
    [InlineData(null, "Unknown")]
    public void Apple_string_availability(string? state, string expected) =>
        Assert.Equal(expected, Detect(Apple(state), AppleRules).Status);

    [Fact]
    public void Apple_wrong_sku_is_unknown() =>
        Assert.Equal("Unknown", Detect(Apple("available", "OTHER"), AppleRules).Status);

    [Fact]
    public void Apple_missing_store_is_unknown() =>
        Assert.Equal("Unknown", Detect("""{"body":{"stores":[]}}""", AppleRules).Status);

    [Theory]
    [InlineData(true, true, false, "GBP", "Available")]
    [InlineData(false, false, false, "GBP", "Unavailable")]
    [InlineData(true, false, false, "GBP", "Unknown")]
    [InlineData(true, true, false, "USD", "Unknown")]
    public void Nintendo_regression(bool available, bool confirm, bool preorder, string currency, string expected)
    {
        var result = Detect(Nintendo(available, confirm, preorder, currency), NintendoRules);
        Assert.Equal(expected, result.Status);
        if (expected == "Available") Assert.Equal(49.99m, result.Price);
    }

    [Theory]
    [InlineData("""{"availablePath":"$.foo"}""")]
    [InlineData("""{"idPath":"$.id","expectedId":"abc","availablePath":"$.stock","pricePath":"$.price"}""")]
    [InlineData("""{"idPath":"$.items[*].id","expectedId":"abc","availablePath":"$.stock"}""")]
    public void Invalid_rules_are_rejected(string rules) =>
        Assert.Throws<ArgumentException>(() => JsonStockDetector.Validate(rules));

    [Fact]
    public void Collection_any_available_reports_matching_store()
    {
        var result = Detect(Stores(("Regent Street", "unavailable"), ("Covent Garden", "available")), CollectionRules);
        Assert.Equal("Available", result.Status);
        Assert.Contains("Covent Garden", result.Detail);
    }

    [Fact]
    public void Collection_all_unavailable() =>
        Assert.Equal("Unavailable", Detect(Stores(("Regent Street", "unavailable"), ("Covent Garden", "unavailable")), CollectionRules).Status);

    [Fact]
    public void Collection_empty_is_unknown() =>
        Assert.Equal("Unknown", Detect(Stores(), CollectionRules).Status);

    [Fact]
    public void Collection_unknown_is_not_unavailable() =>
        Assert.Equal("Unknown", Detect(Stores(("Regent Street", null), ("Covent Garden", "unavailable")), CollectionRules).Status);

    [Fact]
    public void Collection_positive_overrides_unknown_in_any_mode() =>
        Assert.Equal("Available", Detect(Stores(("Regent Street", null), ("Covent Garden", "available")), CollectionRules).Status);

    [Theory]
    [InlineData("available", "available", "Available")]
    [InlineData("available", "unavailable", "Unavailable")]
    public void Collection_all_mode(string first, string second, string expected) =>
        Assert.Equal(expected, Detect(Stores(("Regent Street", first), ("Covent Garden", second)),
            CollectionRules.Replace("\"any\"", "\"all\"")).Status);

    [Fact]
    public void Collection_unavailable_lists_every_checked_item()
    {
        var result = Detect(Stores(("Lakeside", "unavailable"), ("Regent Street", "unavailable"),
            ("Battersea", "unavailable")), CollectionRules);
        Assert.Equal("Unavailable", result.Status);
        Assert.Contains("Checked 3 items: Lakeside, Regent Street, Battersea", result.Detail);
    }

    [Fact]
    public void Collection_available_lists_matching_and_checked_items()
    {
        var result = Detect(Stores(("Lakeside", "available"), ("Regent Street", "unavailable")), CollectionRules);
        Assert.Equal("Available", result.Status);
        Assert.Contains("Available: Lakeside", result.Detail);
        Assert.Contains("Checked 2 items: Lakeside, Regent Street", result.Detail);
    }

    [Fact]
    public void Collection_unknown_lists_checked_and_unverified_items()
    {
        var result = Detect(Stores(("Lakeside", null), ("Regent Street", "unavailable")), CollectionRules);
        Assert.Equal("Unknown", result.Status);
        Assert.Contains("Unverified collection items: Lakeside", result.Detail);
        Assert.Contains("Checked 2 items: Lakeside, Regent Street", result.Detail);
    }

    [Fact]
    public void Collection_all_partial_lists_checked_items()
    {
        var rules = CollectionRules.Replace("\"any\"", "\"all\"");
        var result = Detect(Stores(("Lakeside", "available"), ("Regent Street", "unavailable")), rules);
        Assert.Equal("Unavailable", result.Status);
        Assert.Contains("Checked 2 items: Lakeside, Regent Street", result.Detail);
    }

    [Fact]
    public void Single_item_result_remains_unchanged()
    {
        var result = Detect(Apple("unavailable"), AppleRules);
        Assert.Equal("Unavailable", result.Status);
        Assert.Equal("API confirms not orderable or preorderable", result.Detail);
    }

    [Fact]
    public void Collection_requires_label() =>
        Assert.Throws<ArgumentException>(() => JsonStockDetector.Validate(CollectionRules.Replace("\"labelPath\":\"$.storeName\",", "")));

    [Fact]
    public void Collection_rejects_invalid_mode() =>
        Assert.Throws<ArgumentException>(() => JsonStockDetector.Validate(CollectionRules.Replace("\"any\"", "\"first\"")));
}
