using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class MetricsTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "stocksentinel-metrics-" + Guid.NewGuid());
    readonly Store store;
    readonly string path;

    public MetricsTests()
    {
        path = Path.Combine(directory, "test.db");
        store = new Store(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Sentinel:DatabasePath"] = path }).Build());
        store.Init();
        Execute("INSERT INTO items VALUES (1,'Nintendo',55,1),(2,'Apple',100,0),(3,'Empty',10,1)");
        Execute("INSERT INTO sources(id,item_id,name,url,interval_mins,enabled) VALUES (10,1,'Nintendo','https://example.com',15,1),(20,2,'Apple','https://example.com',15,0)");
        // Historical Available/Unknown/Available/Unavailable forms one 20-minute window.
        Event(10, -120, "info", "Nintendo: Available — old check");
        Event(10, -110, "warning", "Nintendo: Unknown — transient");
        Event(10, -100, "info", "Nintendo: Available — old check");
        Event(10, -90, "info", "Nintendo: Unavailable — ended");
        Check(10, -80, "Unavailable", 100);
        Check(10, -70, "Available", 200);
        Check(10, -60, "Error", 300);
        Check(10, -50, "Unknown", 400);
        Check(10, -40, "Available", 500);
        Check(10, -30, "Unavailable", 600);
        // Event logs after detailed checks begin must not be counted a second time.
        Event(10, -70, "info", "Nintendo: Available — duplicate");
        Event(10, -20, "info", "Nintendo: Available — ignored after detailed history");
        Event(10, -15, "info", "Not a stock observation");
        Event(10, -10, "alert", "Push accepted for 2 device(s)");
        Event(10, -9, "alert", "Reminder push accepted for 2 device(s)");
        Event(10, -8, "alert", "Backup email accepted");
        Check(20, -60, "Available", 1000);
        Check(20, -30, "Unavailable", 2000);
        Event(20, -10, "alert", "Push accepted for 1 device(s)");
        // Deleted-source history remains useful globally but cannot identify a product.
        Check(99, -60, "Available", 3000);
        Event(98, -60, "info", "Deleted: Available — legacy");
        Event(99, -10, "alert", "Push accepted for 1 device(s)");
        Event(null, -10, "alert", "Push accepted legacy notification");
        Event(null, -60, "info", "Unattributed: Available — no source");
        Event(null, -10, "alert", "Test Web Push accepted for 1 subscription(s)");
        Check(10, -8 * 24 * 60, "Available", 9000);
        Event(10, -8 * 24 * 60, "alert", "Push accepted outside period");
    }

    void Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    void Check(long source, int minutes, string status, int duration) => Execute(
        "INSERT INTO check_history(source_id,at_utc,status,duration_ms) VALUES($s,$at,$status,$ms)",
        ("$s", source), ("$at", At(minutes)), ("$status", status), ("$ms", duration));
    void Event(long? source, int minutes, string level, string message) => Execute(
        "INSERT INTO events(source_id,at_utc,level,message) VALUES($s,$at,$level,$message)",
        ("$s", source), ("$at", At(minutes)), ("$level", level), ("$message", message));
    static string At(int minutes) => DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("O");
    JsonElement Metrics(long? itemId = null, int days = 7) => JsonSerializer.SerializeToElement(store.Metrics(days, itemId));
    static int Count(JsonElement m, string key) => m.GetProperty(key).GetInt32();

    [Theory]
    [InlineData(1, 6, 4, 2, 4, 2, 2, 350)]
    [InlineData(2, 2, 2, 0, 0, 1, 1, 1500)]
    public void Product_filters_all_statistics_including_disabled_products(long id, int checks, int success,
        int failed, int historical, int restocks, int alerts, int average)
    {
        var m = Metrics(id);
        Assert.Equal(checks, Count(m, "totalChecks"));
        Assert.Equal(success, Count(m, "successfulChecks"));
        Assert.Equal(failed, Count(m, "failedChecks"));
        Assert.Equal(historical, Count(m, "historicalObservations"));
        Assert.Equal(restocks, Count(m, "restockCount"));
        Assert.Equal(alerts, Count(m, "pushAlerts"));
        Assert.Equal(average, Count(m, "averageResponseMs"));
        Assert.All(m.GetProperty("restocks").EnumerateArray(), r => Assert.Equal(id * 10, r.GetProperty("sourceId").GetInt64()));
        Assert.Equal(restocks, m.GetProperty("byHour").EnumerateArray().Sum(x => x.GetInt32()));
        Assert.Equal(restocks, m.GetProperty("byWeekday").EnumerateArray().Sum(x => x.GetInt32()));
    }

    [Fact]
    public void Historical_windows_and_unknown_error_transitions_are_preserved()
    {
        var windows = Metrics(1).GetProperty("restocks").EnumerateArray().ToArray();
        Assert.Equal(30, windows[0].GetProperty("confirmedMinutes").GetDouble());
        Assert.False(windows[0].GetProperty("historical").GetBoolean());
        Assert.Equal(20, windows[1].GetProperty("confirmedMinutes").GetDouble());
        Assert.True(windows[1].GetProperty("historical").GetBoolean());
    }

    [Fact]
    public void Global_metrics_include_orphans_and_unattributed_alerts_without_double_counting()
    {
        var m = Metrics();
        Assert.Equal(9, Count(m, "totalChecks"));
        Assert.Equal(7, Count(m, "successfulChecks"));
        Assert.Equal(2, Count(m, "failedChecks"));
        Assert.Equal(5, Count(m, "historicalObservations"));
        Assert.Equal(5, Count(m, "restockCount"));
        Assert.Equal(5, Count(m, "pushAlerts"));
        Assert.Equal(900, Count(m, "averageResponseMs"));
        Assert.Equal(5, m.GetProperty("byHour").EnumerateArray().Sum(x => x.GetInt32()));
        Assert.Equal(JsonSerializer.Serialize(store.Metrics(7)), JsonSerializer.Serialize(store.Metrics(7, null)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(999)]
    public void Empty_or_unknown_product_does_not_fall_back_to_global(long id)
    {
        var m = Metrics(id);
        foreach (var key in new[] { "totalChecks", "historicalObservations", "pushAlerts", "restockCount", "averageResponseMs" })
            Assert.Equal(0, Count(m, key));
        Assert.Empty(m.GetProperty("restocks").EnumerateArray());
    }

    [Fact]
    public void Product_combines_multiple_sources_and_history_without_detailed_checks()
    {
        Execute("INSERT INTO sources(id,item_id,name,url,interval_mins,enabled) VALUES (11,1,'Second shop','https://example.com',15,1)");
        Event(11, -60, "info", "Second shop: Available — legacy");
        Event(11, -40, "info", "Second shop: Available — legacy");
        Event(11, -30, "info", "Second shop: Unavailable — legacy");
        Event(11, -10, "alert", "Push accepted for 1 device(s)");
        var m = Metrics(1);
        Assert.Equal(6, Count(m, "totalChecks"));
        Assert.Equal(7, Count(m, "historicalObservations"));
        Assert.Equal(3, Count(m, "restockCount"));
        Assert.Equal(3, Count(m, "pushAlerts"));
        Assert.Contains(m.GetProperty("restocks").EnumerateArray(), r => r.GetProperty("sourceId").GetInt64() == 11);
    }

    [Fact]
    public void Deleting_source_keeps_global_history_but_removes_product_attribution()
    {
        var before = Metrics();
        store.DeleteSource(10);
        Assert.Equal(before.GetRawText(), Metrics().GetRawText());
        Assert.Equal(0, Count(Metrics(1), "totalChecks"));
        Assert.Equal(0, Count(Metrics(1), "historicalObservations"));
        Assert.Equal(0, Count(Metrics(1), "pushAlerts"));
        Assert.Equal(0, Count(Metrics(1), "restockCount"));
    }

    [Fact]
    public void Period_is_clamped_and_applies_to_checks_and_alerts()
    {
        Assert.Equal(1, Count(Metrics(1, 0), "days"));
        var m = Metrics(1, 100);
        Assert.Equal(90, Count(m, "days"));
        Assert.Equal(7, Count(m, "totalChecks"));
        Assert.Equal(3, Count(m, "pushAlerts"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
