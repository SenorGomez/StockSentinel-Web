using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
namespace StockSentinel;
public sealed class MailNotifier(IConfiguration cfg)
{
    public async Task Send(string subject,string body,CancellationToken ct)
    {
        var o=cfg.GetSection("Smtp").Get<SmtpOptions>() ?? new();
        if(!o.Enabled || string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.ToAddress) || string.IsNullOrWhiteSpace(o.FromAddress)) throw new InvalidOperationException("SMTP isn't configured/enabled. See README.");
        using var msg=new MimeMessage(); msg.From.Add(new MailboxAddress(o.FromName,o.FromAddress));msg.To.Add(MailboxAddress.Parse(o.ToAddress));msg.Subject=subject;msg.Body=new TextPart("plain"){Text=body};
        using var client=new SmtpClient();
        var security=o.Security.Equals("SslOnConnect",StringComparison.OrdinalIgnoreCase)?SecureSocketOptions.SslOnConnect:o.Security.Equals("None",StringComparison.OrdinalIgnoreCase)?SecureSocketOptions.None:SecureSocketOptions.StartTls;
        await client.ConnectAsync(o.Host,o.Port,security,ct);
        var password=Environment.GetEnvironmentVariable("STOCKSENTINEL_SMTP_PASSWORD");
        if(!string.IsNullOrEmpty(o.Username)){
            if(string.IsNullOrEmpty(password))throw new InvalidOperationException("Missing STOCKSENTINEL_SMTP_PASSWORD environment variable");
            await client.AuthenticateAsync(o.Username,password,ct);
        }
        await client.SendAsync(msg,ct);await client.DisconnectAsync(true,ct);
    }
}
public sealed class MonitorEngine(Store store,IHttpClientFactory factory,MailNotifier mail,PushNotifications push,IConfiguration cfg,ILogger<MonitorEngine> logger,
    Func<Uri,CancellationToken,Task<bool>>? publicHostCheck = null)
{
    readonly SemaphoreSlim gate=new(1,1);
    public async Task<CheckResult> Check(long sourceId,CancellationToken ct)
    {
        var entered=await gate.WaitAsync(0,ct);if(!entered)return new("Busy",null,"Another check is in progress");
        try{
            var s=store.GetSource(sourceId);if(s is null)return new("Error",null,"Source not found");
            var item=store.Items().FirstOrDefault(x=>x.Id==s.ItemId);if(item is null)return new("Error",null,"Product not found");
            CheckResult result;
            var watch=System.Diagnostics.Stopwatch.StartNew();
            try {
                // HttpClient.Timeout ends at headers with ResponseHeadersRead. Keep
                // the same deadline through DNS, headers, and all response body reads.
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(cfg.GetValue("Sentinel:HttpTimeoutSeconds",20)));
                result=await Fetch(s,deadline.Token);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested){result=new("Error",null,"Monitoring request timed out");}
            catch(Exception ex) when(ex is not OperationCanceledException){result=new("Error",null,$"{ex.GetType().Name}: {ex.Message}");}
            ct.ThrowIfCancellationRequested();
            watch.Stop();store.RecordCheck(s.Id,result,watch.ElapsedMilliseconds);
            int fails=result.Status is "Error" or "Unknown"?Math.Min(s.FailCount+1,8):0;
            int minutes=Math.Max(cfg.GetValue("Sentinel:MinimumPollMinutes",3),s.IntervalMinutes);
            if(fails>=2) minutes=Math.Min(360,minutes*(int)Math.Pow(2,Math.Min(fails-1,5)));
            var next=DateTimeOffset.UtcNow.AddMinutes(minutes);
            // An unknown/error is not evidence of unavailability, and never resets the alert latch.
            store.Record(s.Id,result,fails,next);
            store.Log(result.Status is "Error" or "Unknown"?"warning":"info",$"{s.Name}: {result.Status} — {result.Detail}",s.Id);
            if(result.Status=="Unavailable")store.ClearNotification(s.Id);
            var priceLabel=result.Price is decimal amount?$"£{amount:0.00}":"price not supplied";
            if(result.Status=="Available" && (result.Price is null || result.Price<=item.MaxPriceGbp) && !store.HasNotified(s.Id))
            {
                bool delivered=false;
                var purchaseUrl=JsonStockDetector.PurchaseUrl(s) ?? s.Url;
                try
                {
                    var count=await push.SendAsync($"🚨 {item.Name} IN STOCK!",$"{s.Name} — {priceLabel} — {result.Detail}",purchaseUrl,ct);
                    store.Log("alert",$"Push accepted for {count} device(s): {item.Name} at {s.Name}",s.Id);
                    delivered=true;
                }
                catch(Exception ex){store.Log("warning",$"Push notification failed: {ex.Message}",s.Id);logger.LogWarning(ex,"Push notification failed");}
                // Optional existing SMTP backup. Never require SMTP for Web Push to work.
                if(cfg.GetValue<bool>("Smtp:Enabled"))
                {
                    try{await mail.Send($"🚨 STOCK ALERT: {item.Name} — {priceLabel}",$"{item.Name} available at {s.Name}\n{result.Detail}\nPrice: {priceLabel}\nBuy: {purchaseUrl}",ct);delivered=true;store.Log("alert","Backup email accepted",s.Id);}
                    catch(Exception ex){store.Log("error",$"SMTP notification failed: {ex.Message}",s.Id);}
                }
                if(delivered)store.MarkNotified(s.Id);

            }
            return result;
        } finally {gate.Release();}
    }
    async Task<CheckResult> FetchJson(Source s,CancellationToken ct)
    {
        if(!Uri.TryCreate(s.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https")return new("Error",null,"Only HTTPS URLs supported");
        if(!await (publicHostCheck ?? NetworkSafety.IsPublicHost)(uri,ct))return new("Error",null,"Host must resolve to a public IP");
        var client=factory.CreateClient("stock");
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if((int)response.StatusCode is 403 or 429)return new("Error",null,$"Blocked/rate limited: HTTP {(int)response.StatusCode}");
        if(!response.IsSuccessStatusCode)return new("Error",null,$"HTTP {(int)response.StatusCode}");
        if(response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json") &&
            !(response.Content.Headers.ContentType?.MediaType?.EndsWith("+json",StringComparison.OrdinalIgnoreCase)??false))return new("Unknown",null,"API did not return JSON");
        var cap=cfg.GetValue("Sentinel:MaxResponseBytes",750000);
        if(response.Content.Headers.ContentLength>cap)return new("Unknown",null,"JSON exceeds size cap");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        using var buffer=new MemoryStream();var chunk=new byte[8192];int n;
        while((n=await stream.ReadAsync(chunk,ct))>0){if(buffer.Length+n>cap)return new("Unknown",null,"JSON exceeds size cap");buffer.Write(chunk,0,n);}
        try{return JsonStockDetector.Detect(buffer.ToArray(),s.JsonConfig);}
        catch(System.Text.Json.JsonException){return new("Unknown",null,"Malformed API JSON");}
    }
    async Task<CheckResult> Fetch(Source s,CancellationToken ct)
    {
        if(s.Adapter=="rest-json")return await FetchJson(s,ct);
        if(s.Adapter!="html")return new("Error",null,"Unsupported adapter");
        if(!Uri.TryCreate(s.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https")return new("Error",null,"Only HTTPS URLs are supported");
        if(string.IsNullOrWhiteSpace(s.AvailableSelector)||string.IsNullOrWhiteSpace(s.UnavailableSelector)||string.IsNullOrWhiteSpace(s.PriceSelector)||string.IsNullOrWhiteSpace(s.PricePattern))return new("Unknown",null,"Detection selectors and price pattern must all be configured and tested");
        // Avoid private hosts / SSRF from browser-editable URLs; validate DNS-resolved addresses as well.
        if(!await (publicHostCheck ?? NetworkSafety.IsPublicHost)(uri,ct))return new("Error",null,"Target must resolve to a public internet IP address");
        var client=factory.CreateClient("stock");using var request=new HttpRequestMessage(HttpMethod.Get,uri);using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if((int)response.StatusCode is 403 or 429)return new("Error",null,$"Retailer blocked/rate-limited request: HTTP {(int)response.StatusCode}");
        if(!response.IsSuccessStatusCode)return new("Error",null,$"HTTP {(int)response.StatusCode}");
        if(response.Content.Headers.ContentType?.MediaType is not "text/html" and not "application/xhtml+xml")return new("Unknown",null,"Unexpected content type");
        var cap=cfg.GetValue("Sentinel:MaxResponseBytes",750000);
        if(response.Content.Headers.ContentLength>cap)return new("Unknown",null,"Response exceeds size limit");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var memory=new MemoryStream();var buffer=new byte[8192];int n;while((n=await stream.ReadAsync(buffer,ct))>0){if(memory.Length+n>cap)return new("Unknown",null,"Response exceeds size limit");memory.Write(buffer,0,n);}
        var html=System.Text.Encoding.UTF8.GetString(memory.ToArray());
        var document=await new HtmlParser().ParseDocumentAsync(html,ct);
        var available=document.QuerySelector(s.AvailableSelector);
        var unavailable=document.QuerySelector(s.UnavailableSelector);
        bool positive=available is not null && (string.IsNullOrWhiteSpace(s.AvailabilityText)||available.TextContent.Contains(s.AvailabilityText,StringComparison.OrdinalIgnoreCase));
        bool negative=unavailable is not null && (string.IsNullOrWhiteSpace(s.UnavailabilityText)||unavailable.TextContent.Contains(s.UnavailabilityText,StringComparison.OrdinalIgnoreCase));
        if(positive==negative)return new("Unknown",null,"Availability selectors ambiguous or unmatched");
        if(negative)return new("Unavailable",null,"Explicit out-of-stock element matched");
        var priceText=document.QuerySelector(s.PriceSelector)?.TextContent;
        if(string.IsNullOrEmpty(priceText))return new("Unknown",null,"Price element missing");
        var match=Regex.Match(priceText,s.PricePattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(200));
        if(!match.Success||!match.Groups["price"].Success)return new("Unknown",null,"No trusted GBP price extracted");
        var normalized=match.Groups["price"].Value.Replace(",","");
        if(!decimal.TryParse(normalized,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var price)||price<=0)return new("Unknown",null,"Invalid price");
        return new("Available",price,"Availability and GBP price matched on retailer HTML");
    }
}
public static class NetworkSafety
{
    public static async Task<bool> IsPublicHost(Uri u,CancellationToken ct)
    {
        if(u.HostNameType!=UriHostNameType.Dns||u.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase))return false;
        var ips=await System.Net.Dns.GetHostAddressesAsync(u.DnsSafeHost,ct);
        if(ips.Length==0)return false;
        return ips.All(ip=>{
            if(System.Net.IPAddress.IsLoopback(ip))return false;
            var b=ip.GetAddressBytes();
            if(ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)return b[0] is not (0 or 10 or 127) && !(b[0]==169&&b[1]==254) && !(b[0]==172&&b[1]>=16&&b[1]<=31) && !(b[0]==192&&b[1]==168) && !(b[0]>=224) && !(b[0]==100&&b[1]>=64&&b[1]<=127);
            return !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast && !ip.Equals(System.Net.IPAddress.IPv6Any) && !(b[0]==0xfc||b[0]==0xfd);
        });
    }
}
public sealed class PollWorker(Store store,MonitorEngine engine,ILogger<PollWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{
                var now=DateTimeOffset.UtcNow;
                foreach(var s in store.Sources().Where(x=>x.Enabled))
                {
                    if(!store.Items().Any(i=>i.Id==s.ItemId&&i.Enabled))continue;
                    if(s.NextCheckUtc is not null&&DateTimeOffset.TryParse(s.NextCheckUtc,out var next)&&next>now)continue;
                    try {await engine.Check(s.Id,stoppingToken);}
                    catch(Exception ex) when(!stoppingToken.IsCancellationRequested){
                        logger.LogError(ex,"Source {SourceId} check failed",s.Id);
                        store.Log("error",$"Source check failed: {ex.Message}",s.Id);
                    }
                }
            }catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Polling loop failed");store.Log("error",ex.Message);}
            try{await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}

// Reminder pushes use cached confirmed availability and never fetch the retailer.
public sealed class ReminderWorker(Store store,PushNotifications push,ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(20));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach(var (id,count) in store.DueReminders(DateTimeOffset.UtcNow))
                {
                    var source=store.GetSource(id);
                    if(source is null || !store.ClaimReminder(id,count))continue;
                    var item=store.Items().FirstOrDefault(i=>i.Id==source.ItemId);
                    if(item is null)continue;
                    try
                    {
                        var accepted=await push.SendAsync($"🔔 Still in stock: {item.Name}",$"Reminder {count}/2 — {source.Name}",JsonStockDetector.PurchaseUrl(source)??source.Url,stoppingToken);
                        store.Log("alert",$"Reminder push accepted for {accepted} device(s): {item.Name} at {source.Name}",id);
                    }
                    catch(Exception ex) when(ex is not OperationCanceledException){store.Log("warning",$"Reminder push failed: {ex.Message}",id);logger.LogWarning(ex,"Reminder push failed");}
                }
            }
            catch(Exception ex) when(ex is not OperationCanceledException){logger.LogError(ex,"Reminder worker failed");}
        }
    }
}
