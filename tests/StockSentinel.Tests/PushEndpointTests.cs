using Microsoft.Extensions.Configuration;
using StockSentinel;
using Xunit;
namespace StockSentinel.Tests;
public sealed class PushEndpointTests
{
    [Theory]
    [InlineData("https://web.push.apple.com/Qtoken")]
    [InlineData("https://other.push.apple.com/opaque/token")]
    [InlineData("https://fcm.googleapis.com/fcm/send/token")]
    [InlineData("https://fcm.googleapis.com/wp/token")]
    [InlineData("https://android.googleapis.com/gcm/send/token")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/token")]
    [InlineData("https://push.services.mozilla.com/wpush/v1/token")]
    [InlineData("https://wns2-am3p.notify.windows.com/w/?token=opaque%2Ftoken")]
    public void Supported_browser_subscriptions_remain_opaque(string endpoint) => Assert.Equal(endpoint,PushEndpointPolicy.Validate(endpoint).OriginalString);
    [Theory]
    [InlineData("https://localhost/push")][InlineData("https://127.0.0.1/push")]
    [InlineData("https://fcm.googleapis.com.attacker.example/push")][InlineData("https://evilpush.apple.com/push")]
    [InlineData("https://notify.windows.com.attacker.example/push")][InlineData("https://example.com/push")]
    [InlineData("http://fcm.googleapis.com/push")][InlineData("https://fcm.googleapis.com:8443/push")]
    [InlineData("https://user:pass@web.push.apple.com/token")][InlineData("https://web.push.apple.com/token#fragment")]
    [InlineData("https://web.push.apple.com/")]
    public void Unsupported_or_ambiguous_endpoints_are_rejected(string endpoint) => Assert.Throws<ArgumentException>(()=>PushEndpointPolicy.Validate(endpoint));
}

public sealed class PushSubscriptionPolicyTests
{
    [Fact]
    public async Task Registration_and_legacy_delivery_both_validate_without_deleting_subscriptions()
    {
        var directory=Path.Combine(Path.GetTempPath(),"stocksentinel-push-policy-"+Guid.NewGuid());
        try
        {
            var path=Path.Combine(directory,"test.db");
            var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                {["Sentinel:DatabasePath"]=path,["Push:VapidSubject"]="mailto:test@example.com"}).Build();
            var store=new Store(config);store.Init();var logger=new CapturingLogger();var push=new PushNotifications(config,logger);push.Initialize();
            Assert.Throws<ArgumentException>(()=>push.Subscribe(new("https://example.com/push",new("key","auth"))));
            Assert.Equal(0,push.Count());
            push.Subscribe(new("https://web.push.apple.com/opaque",new("key","auth")));
            Assert.Equal(1,push.Count());
            push.Unsubscribe("https://web.push.apple.com/opaque");
            using(var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();using var command=connection.CreateCommand();
                command.CommandText="INSERT INTO push_subscriptions VALUES('https://localhost/private','key','auth','legacy')";command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(()=>push.SendAsync("test","test","/",default));
            Assert.Contains(logger.Exceptions,e=>e is ArgumentException && e.Message.StartsWith("Unsupported Web Push service"));
            Assert.Equal(1,push.Count());
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PushNotifications>
    {
        public List<Exception> Exceptions {get;}=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level)=>true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,Microsoft.Extensions.Logging.EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {if(exception is not null)Exceptions.Add(exception);}
    }
}
