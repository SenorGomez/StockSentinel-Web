using StockSentinel;
using System.Security.Cryptography;
using System.Text;
var builder=WebApplication.CreateBuilder(args);
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options=>AuthenticationSecurity.ConfigureProxy(options,builder.Configuration));
builder.Services.Configure<SentinelOptions>(builder.Configuration.GetSection("Sentinel"));
builder.Services.AddSingleton<PushNotifications>();builder.Services.AddSingleton<Store>();builder.Services.AddSingleton<MailNotifier>();builder.Services.AddSingleton<MonitorEngine>();builder.Services.AddHostedService<PollWorker>();builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddHttpClient("stock",client=>{client.Timeout=TimeSpan.FromSeconds(builder.Configuration.GetValue("Sentinel:HttpTimeoutSeconds",20));client.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["Sentinel:DefaultUserAgent"]??"StockSentinel/1.0");}).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false});
var app=builder.Build();
app.UseForwardedHeaders();
app.Services.GetRequiredService<Store>().Init();
app.Services.GetRequiredService<PushNotifications>().Initialize();
app.Use(async (ctx,next)=>{
    // Protect all UI and API endpoints with a configured key. /health is for localhost probes.
    if(ctx.Request.Path=="/health" && System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress??System.Net.IPAddress.None)){await next();return;}
    // The login UI requires these public static assets before a session exists.
    // All API endpoints remain protected by the authentication middleware.
    if(ctx.Request.Path=="/app.js" || ctx.Request.Path=="/style.css" || ctx.Request.Path=="/favicon.ico" || ctx.Request.Path=="/sw.js" || ctx.Request.Path=="/manifest.webmanifest" || ctx.Request.Path=="/icon.svg"){await next();return;}
    var expected=Environment.GetEnvironmentVariable("STOCKSENTINEL_API_KEY");
    if(string.IsNullOrWhiteSpace(expected)||expected.Length<24){ctx.Response.StatusCode=503;await ctx.Response.WriteAsync("Set STOCKSENTINEL_API_KEY (24+ chars) before accessing UI");return;}
    var supplied=ctx.Request.Headers["X-StockSentinel-Key"].ToString();
    if(string.IsNullOrWhiteSpace(supplied)) supplied=ctx.Request.Cookies["sentinel_auth"]??"";
    var a=Encoding.UTF8.GetBytes(expected);var b=Encoding.UTF8.GetBytes(supplied);
    if(a.Length!=b.Length || !CryptographicOperations.FixedTimeEquals(a,b)){
        if(ctx.Request.Path=="/"||ctx.Request.Path=="/login"){await next();return;}
        ctx.Response.StatusCode=401;return;
    }
    await next();
});
app.MapGet("/health",()=>Results.Ok(new{status="running",utc=DateTimeOffset.UtcNow}));
app.MapPost("/login",(HttpContext ctx,LoginBody body)=>{
    var expected=Environment.GetEnvironmentVariable("STOCKSENTINEL_API_KEY")??"";
    var a=Encoding.UTF8.GetBytes(expected);var b=Encoding.UTF8.GetBytes(body.Key??"");
    if(a.Length!=b.Length||!CryptographicOperations.FixedTimeEquals(a,b))return Results.Unauthorized();
    ctx.Response.Cookies.Append("sentinel_auth",body.Key!,AuthenticationSecurity.CookieOptions(ctx.Request,app.Environment));return Results.Ok(new{ok=true});
});
app.MapPost("/api/logout",(HttpContext ctx)=>{ctx.Response.Cookies.Delete("sentinel_auth");return Results.Ok();});
app.MapGet("/api/items",(Store s)=>s.Items());
app.MapPost("/api/items",(NewItem i,Store s)=>{
    if(string.IsNullOrWhiteSpace(i.Name)||i.MaxPriceGbp<=0||i.MaxPriceGbp>100000)return Results.BadRequest("Invalid name/price");
    return Results.Ok(new{id=s.AddItem(i)});
});
app.MapPut("/api/items/{id:long}",(long id,NewItem i,Store s)=>{
    if(string.IsNullOrWhiteSpace(i.Name)||i.MaxPriceGbp<=0||i.MaxPriceGbp>100000)return Results.BadRequest();return s.UpdateItem(id,i)?Results.Ok():Results.NotFound();
});
app.MapGet("/api/sources",(Store s)=>s.Sources());
string? ValidateSource(NewSource x,Store s,IConfiguration config){
    if(s.Items().All(i=>i.Id!=x.ItemId))return "Product not found";
    return SourceTemplates.ValidateSettings(x,config);
}
app.MapPost("/api/sources",(NewSource x,Store s,IConfiguration c)=>{var error=ValidateSource(x,s,c);return error is not null?Results.BadRequest(error):Results.Ok(new{id=s.AddSource(x)});});
app.MapPut("/api/sources/{id:long}",(long id,NewSource x,Store s,IConfiguration c)=>{var error=ValidateSource(x,s,c);return error is not null?Results.BadRequest(error):s.UpdateSource(id,x)?Results.Ok():Results.NotFound();});
app.MapGet("/api/sources/{id:long}/template",(long id,Store s,IConfiguration config)=>{
    var source=s.GetSource(id);if(source is null)return Results.NotFound();
    try{return Results.File(SourceTemplates.Bytes(SourceTemplates.Export(source,config)),"application/json","stocksentinel-source-template.json");}
    catch(ArgumentException ex){return Results.BadRequest(ex.Message);}
});
app.MapPost("/api/source-templates/preview",(System.Text.Json.JsonElement document,Store s,IConfiguration config)=>{
    try{
        var template=SourceTemplates.Read(document,config);
        var source=SourceTemplates.ToSource(template,0);
        return Results.Ok(new{source,duplicates=s.Sources().Where(x=>SourceTemplates.IsDuplicate(x,source)).Select(x=>x.Name)});
    }catch(ArgumentException ex){return Results.BadRequest(ex.Message);}
});
app.MapPost("/api/source-templates/import",(TemplateImport body,Store s,IConfiguration config)=>{
    try{
        var source=SourceTemplates.ToSource(SourceTemplates.Read(body.Template,config),body.ItemId);
        var error=ValidateSource(source,s,config);
        return error is not null?Results.BadRequest(error):Results.Ok(new{id=s.AddSource(source)});
    }catch(ArgumentException ex){return Results.BadRequest(ex.Message);}
});
app.MapDelete("/api/sources/{id:long}",(long id,Store s)=>{s.DeleteSource(id);return Results.Ok();});
app.MapGet("/api/metrics",(Store s,int? days,long? itemId)=>s.Metrics(days??7,itemId));
app.MapGet("/api/events",(Store s,int? limit)=>s.Events(limit??150));
app.MapPost("/api/sources/{id:long}/check",async (long id,Store s,MonitorEngine m,CancellationToken ct)=>s.GetSource(id) is null?Results.NotFound():Results.Ok(await m.Check(id,ct)));
app.MapGet("/api/push/key",(PushNotifications push)=>Results.Ok(new {publicKey=push.PublicKey,subscriptions=push.Count()}));
app.MapPost("/api/push/subscribe",(PushRegistration sub,PushNotifications push)=>{try{push.Subscribe(sub);return Results.Ok(new {ok=true});}catch(ArgumentException ex){return Results.BadRequest(ex.Message);}});
app.MapPost("/api/push/unsubscribe",(PushUnsubscribe sub,PushNotifications push)=>{push.Unsubscribe(sub.Endpoint);return Results.Ok(new{ok=true});});
app.MapPost("/api/push/test",async (PushNotifications push,Store store,CancellationToken ct)=>{try{var count=await push.SendAsync("StockSentinel test","Web Push is connected and working.","https://stocksentinel.home/",ct);store.Log("alert",$"Test Web Push accepted for {count} subscription(s)");return Results.Ok(new{ok=true,accepted=count});}catch(Exception ex){store.Log("error",$"Push test failed: {ex.Message}");return Results.Problem(ex.Message,statusCode:502);}});
app.MapGet("/api/settings",(IConfiguration cfg)=>{
    var smtp=cfg.GetSection("Smtp").Get<SmtpOptions>()??new();return Results.Ok(new{smtp.Enabled,smtp.Host,smtp.Port,smtp.Security,smtp.Username,smtp.FromAddress,smtp.FromName,smtp.ToAddress,passwordConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STOCKSENTINEL_SMTP_PASSWORD"))});
});
app.MapPost("/api/notifications/test",async (MailNotifier mail,Store store,CancellationToken ct)=>{
    try{await mail.Send("StockSentinel test notification","Your StockSentinel email notifications are working. This is a test.",ct);store.Log("alert","Test email sent");return Results.Ok(new{ok=true});}
    catch(Exception ex){store.Log("error",$"Test email failed: {ex.Message}");return Results.Problem("Test email failed. Inspect recent activity for details.",statusCode:502);}
});
app.UseDefaultFiles();app.UseStaticFiles();app.Run();
public sealed record LoginBody(string? Key);
