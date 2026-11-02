using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class SourceIdentityTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"stocksentinel-identity-"+Guid.NewGuid());
    readonly IConfiguration config;
    readonly Store store;
    readonly string path;
    public SourceIdentityTests()
    {
        path=Path.Combine(directory,"test.db");
        config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {["Sentinel:DatabasePath"]=path}).Build();
        store=new Store(config);store.Init();
    }
    NewSource Source(long product,string name="Shop") => new(product,name,"https://example.com/product",15,false);
    void Execute(string sql)
    {
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        c.Open();using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
    }
    JsonElement Metrics(long? product=null) => JsonSerializer.SerializeToElement(store.Metrics(7,product));

    [Fact]
    public void Delete_and_recreate_never_assigns_old_history_to_another_product()
    {
        var firstProduct=store.AddItem(new("Nintendo",55));var secondProduct=store.AddItem(new("Apple",100));
        var old=store.AddSource(Source(firstProduct));
        store.RecordCheck(old,new("Available",49,"In stock"),100);
        store.Log("alert","Push accepted for 1 device(s)",old);store.MarkNotified(old);
        var global=Metrics().GetRawText();
        store.DeleteSource(old);
        var next=store.AddSource(Source(secondProduct,"Other shop"));
        Assert.True(next>old);
        Assert.Null(store.GetSource(old));
        Assert.Equal(global,Metrics().GetRawText());
        Assert.Single(store.Events());
        Assert.False(store.HasNotified(old));Assert.False(store.HasNotified(next));
        foreach(var product in new[]{firstProduct,secondProduct})
        {
            var m=Metrics(product);
            Assert.Equal(0,m.GetProperty("totalChecks").GetInt32());
            Assert.Equal(0,m.GetProperty("pushAlerts").GetInt32());
            Assert.Equal(0,m.GetProperty("restockCount").GetInt32());
        }
        store.RecordCheck(next,new("Unavailable",null,"Sold out"),50);
        Assert.Equal(1,Metrics(secondProduct).GetProperty("totalChecks").GetInt32());
        Assert.Equal(0,Metrics(secondProduct).GetProperty("restockCount").GetInt32());
        Assert.Equal(2,Metrics().GetProperty("totalChecks").GetInt32());
    }

    [Fact]
    public void Counter_survives_deletion_of_all_sources_history_cleanup_and_restart()
    {
        var product=store.AddItem(new("Product",55));
        long previous=0;
        for(var i=0;i<4;i++)
        {
            var current=store.AddSource(Source(product));Assert.True(current>previous);
            store.DeleteSource(current);
            Execute("DELETE FROM events; DELETE FROM check_history;");
            new Store(config).Init();previous=current;
        }
        Assert.Empty(store.Sources());
        Assert.True(new Store(config).AddSource(Source(product))>previous);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("events")]
    [InlineData("check_history")]
    [InlineData("notifications")]
    [InlineData("alert_reminders")]
    public void Legacy_database_initializes_above_all_retained_source_ids(string highestTable)
    {
        var product=store.AddItem(new("Existing product",55));
        var live=store.AddSource(Source(product));
        store.Record(live,new("Unavailable",49,"Existing status"),2,DateTimeOffset.UtcNow.AddMinutes(15));
        store.RecordCheck(live,new("Unavailable",49,"Existing check"),100);
        store.Log("info","Shop: Unavailable — existing event",live);store.MarkNotified(live);
        // An existing installation has all these tables but no allocation counter.
        Execute("DROP TABLE source_id_sequence; CREATE TABLE installation_secret(value TEXT); INSERT INTO installation_secret VALUES('keep-me');");
        const long high=500;
        var timestamp=DateTimeOffset.UtcNow.ToString("O");
        var seed=highestTable switch
        {
            "sources"=>$"INSERT INTO sources(id,item_id,name,url,interval_mins,enabled,json_config) VALUES({high},{product},'Legacy shop','https://example.com',15,0,'{{\"idPath\":\"$.id\"}}')",
            "events"=>$"INSERT INTO events(source_id,at_utc,level,message) VALUES({high},'{timestamp}','info','Deleted: Available — legacy')",
            "check_history"=>$"INSERT INTO check_history(source_id,at_utc,status,duration_ms) VALUES({high},'{timestamp}','Available',100)",
            "notifications"=>$"INSERT INTO notifications(source_id,sent_at,status) VALUES({high},'{timestamp}','sent')",
            _=>$"INSERT INTO alert_reminders(source_id,first_sent_utc,count) VALUES({high},'{timestamp}',2)"
        };
        Execute(seed);
        var before=store.Sources();var events=store.Events();var metrics=Metrics().GetRawText();
        store.Init();store.Init();
        Assert.Equal(before,store.Sources());Assert.Equal(events,store.Events());
        Assert.Equal(metrics,Metrics().GetRawText());Assert.True(store.HasNotified(live));
        var id=store.AddSource(Source(product,"New source"));
        Assert.True(id>high);
        // The migration never rewrites existing sources or unrelated installation tables.
        using var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT value FROM installation_secret";Assert.Equal("keep-me",q.ExecuteScalar());
    }

    [Fact]
    public async Task Concurrent_source_creation_allocates_unique_ids()
    {
        var product=store.AddItem(new("Product",55));
        var ids=await Task.WhenAll(Enumerable.Range(0,12).Select(i=>Task.Run(()=>new Store(config).AddSource(Source(product,$"Shop {i}")))));
        Assert.Equal(12,ids.Distinct().Count());Assert.Equal(12,store.Sources().Count);
        foreach(var id in ids)store.DeleteSource(id);
        Assert.True(store.AddSource(Source(product))>ids.Max());
    }

    [Fact]
    public void Failed_insert_rolls_back_counter_and_exhaustion_fails_without_reusing_ids()
    {
        var product=store.AddItem(new("Product",55));
        var first=store.AddSource(Source(product));
        Assert.Throws<SqliteException>(()=>store.AddSource(Source(product) with {Name=null!}));
        Assert.Equal(first+1,store.AddSource(Source(product)));
        Execute("UPDATE source_id_sequence SET last_id=9223372036854775807");
        Assert.Throws<InvalidOperationException>(()=>store.AddSource(Source(product)));
        Assert.Equal(2,store.Sources().Count);
    }
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
}
