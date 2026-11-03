using System.Text.Json;
using Microsoft.Data.Sqlite;
using WebPush;
namespace StockSentinel;

// VAPID private key and push subscriptions live in the existing persistent data folder.
public sealed class PushNotifications(IConfiguration config, ILogger<PushNotifications> logger)
{
    readonly object sync = new();
    string DbPath => Path.GetFullPath(config["Sentinel:DatabasePath"] ?? "data/sentinel.db");
    string KeysPath => Path.Combine(Path.GetDirectoryName(DbPath)!, "push-vapid.json");
    string ConnectionString => new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
    (string PublicKey,string PrivateKey) Keys()
    {
        lock (sync)
        {
            if (!File.Exists(KeysPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KeysPath)!);
                var generated = VapidHelper.GenerateVapidKeys();
                var json = JsonSerializer.Serialize(new KeyPair(generated.PublicKey, generated.PrivateKey));
                using (var stream = new FileStream(KeysPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) writer.Write(json);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeysPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var data = JsonSerializer.Deserialize<KeyPair>(File.ReadAllText(KeysPath)) ?? throw new InvalidOperationException("Invalid VAPID key file");
            return (data.PublicKey, data.PrivateKey);
        }
    }
    public string PublicKey => Keys().PublicKey;
    public void Initialize()
    {
        _ = Keys();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS push_subscriptions (endpoint TEXT PRIMARY KEY, p256dh TEXT NOT NULL, auth TEXT NOT NULL, created_utc TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }
    public void Subscribe(PushRegistration registration)
    {
        PushEndpointPolicy.Validate(registration.Endpoint);
        if(string.IsNullOrWhiteSpace(registration.Keys?.P256dh)||string.IsNullOrWhiteSpace(registration.Keys.Auth))throw new ArgumentException("Invalid push subscription");
        using var connection = new SqliteConnection(ConnectionString); connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO push_subscriptions(endpoint,p256dh,auth,created_utc) VALUES($e,$p,$a,$d) ON CONFLICT(endpoint) DO UPDATE SET p256dh=$p,auth=$a";
        cmd.Parameters.AddWithValue("$e",registration.Endpoint);cmd.Parameters.AddWithValue("$p",registration.Keys.P256dh);cmd.Parameters.AddWithValue("$a",registration.Keys.Auth);cmd.Parameters.AddWithValue("$d",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
    }
    public void Unsubscribe(string endpoint)
    {
        using var connection=new SqliteConnection(ConnectionString);connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="DELETE FROM push_subscriptions WHERE endpoint=$e";cmd.Parameters.AddWithValue("$e",endpoint);cmd.ExecuteNonQuery();
    }
    public int Count()
    {
        using var connection=new SqliteConnection(ConnectionString);connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM push_subscriptions";return Convert.ToInt32(cmd.ExecuteScalar());
    }
    public async Task<int> SendAsync(string title,string body,string url,CancellationToken ct)
    {
        List<PushRegistration> subscriptions = new();
        using(var connection=new SqliteConnection(ConnectionString))
        {
            connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT endpoint,p256dh,auth FROM push_subscriptions";using var reader=cmd.ExecuteReader();
            while(reader.Read())subscriptions.Add(new(reader.GetString(0),new(reader.GetString(1),reader.GetString(2))));
        }
        if(subscriptions.Count==0)throw new InvalidOperationException("No Web Push subscriptions. Install and subscribe from your iPhone first.");
        var keys=Keys();
        var subject = config["Push:VapidSubject"];
        if (string.IsNullOrWhiteSpace(subject) ||
            !(Uri.TryCreate(subject, UriKind.Absolute, out var contact) &&
              (contact.Scheme == "mailto" || contact.Scheme == "https")))
            throw new InvalidOperationException("Push:VapidSubject must be a configured mailto: or https: URI.");
        var payload=JsonSerializer.Serialize(new {title,body,url});
        var vapid=new VapidDetails(subject,keys.PublicKey,keys.PrivateKey);
        var successes=0;
        using var http=new HttpClient(NetworkSafety.CreateHandler());
        using var client=new WebPushClient(http);
        foreach(var sub in subscriptions)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                PushEndpointPolicy.Validate(sub.Endpoint);
                await client.SendNotificationAsync(new PushSubscription(sub.Endpoint,sub.Keys.P256dh,sub.Keys.Auth),payload,vapid,cancellationToken:ct);
                successes++;
            }
            catch(WebPushException ex) when(ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {Unsubscribe(sub.Endpoint); logger.LogWarning("Removed expired Web Push subscription");}
            catch(Exception ex) when(ex is not OperationCanceledException){logger.LogError(ex,"Web Push delivery request failed");}
        }
        if(successes==0)throw new InvalidOperationException("No push service accepted the notification; check server logs");
        return successes;
    }
    sealed record KeyPair(string PublicKey,string PrivateKey);
}
public sealed record PushKeys(string P256dh,string Auth);
public sealed record PushRegistration(string Endpoint,PushKeys Keys);
public sealed record PushUnsubscribe(string Endpoint);

public static class NotificationUrls
{
    public static string ApplicationBaseUrl(IConfiguration config)
    {
        var value=config["Application:BaseUrl"] ?? "https://stocksentinel.home/";
        if(!Uri.TryCreate(value,UriKind.Absolute,out var url)||url.Scheme!="https"||string.IsNullOrEmpty(url.Host)
            ||url.UserInfo.Length>0||url.Query.Length>0||url.Fragment.Length>0||url.AbsolutePath!="/")
            throw new InvalidOperationException("Application:BaseUrl must be an HTTPS origin without credentials, path, query or fragment");
        return url.AbsoluteUri;
    }

    public static string ForSource(Source source) => JsonStockDetector.PurchaseUrl(source) ?? source.Url;
}
