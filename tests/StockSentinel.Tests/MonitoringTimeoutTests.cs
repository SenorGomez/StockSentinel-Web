using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StockSentinel;
using Xunit;

namespace StockSentinel.Tests;

public sealed class MonitoringTimeoutTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"stocksentinel-timeout-"+Guid.NewGuid());
    readonly IConfiguration config;
    readonly Store store;
    readonly long product;
    public MonitoringTimeoutTests()
    {
        config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Sentinel:DatabasePath"]=Path.Combine(directory,"test.db"),
            ["Sentinel:HttpTimeoutSeconds"]="1"
        }).Build();
        store=new Store(config);store.Init();product=store.AddItem(new("Product",55));
    }
    long Add(string path,string adapter="html") => store.AddSource(new(product,path,"https://example.com/"+path,15,true,
        adapter,".yes",".no",".price",@"£(?<price>[0-9.]+)",JsonConfig:"""{"idPath":"$.product.id","expectedId":"sku","availablePath":"$.product.available"}"""));
    MonitorEngine Engine(HttpClient client,Func<Uri,CancellationToken,Task<bool>>? host=null) => new(store,new Factory(client),
        new MailNotifier(config),new PushNotifications(config,NullLogger<PushNotifications>.Instance),config,
        NullLogger<MonitorEngine>.Instance,host ?? ((_,_)=>Task.FromResult(true)));
    static HttpResponseMessage Healthy(string adapter) => new(HttpStatusCode.OK)
    {
        Content=new StringContent(adapter=="html"?"<span class='no'>Sold out</span>":"""{"product":{"id":"sku","available":false}}""",
            Encoding.UTF8,adapter=="html"?"text/html":"application/json")
    };
    static async Task Until(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while(!condition())await Task.Delay(10,timeout.Token);
    }

    [Theory]
    [InlineData("html","dns")]
    [InlineData("html","headers")]
    [InlineData("html","body")]
    [InlineData("rest-json","dns")]
    [InlineData("rest-json","headers")]
    [InlineData("rest-json","body")]
    public async Task Deadline_covers_each_network_stage_and_polling_continues(string adapter,string stage)
    {
        var first=Add("first",adapter);var second=Add("second",adapter);store.MarkNotified(first);
        var stream=new StalledStream();
        using var client=new HttpClient(new Handler(async(request,ct)=>
        {
            if(request.RequestUri!.AbsolutePath=="/second")return Healthy(adapter);
            if(stage=="headers")await Task.Delay(Timeout.Infinite,ct);
            var content=new StreamContent(stream);
            content.Headers.ContentType=new MediaTypeHeaderValue(adapter=="html"?"text/html":"application/json");
            return new HttpResponseMessage(HttpStatusCode.OK){Content=content};
        })){Timeout=Timeout.InfiniteTimeSpan};
        var engine=Engine(client,async(uri,ct)=>
        {
            if(stage=="dns"&&uri.AbsolutePath=="/first")await Task.Delay(Timeout.Infinite,ct);
            return true;
        });
        using var worker=new PollWorker(store,engine,NullLogger<PollWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try {await Until(()=>store.GetSource(second)!.LastCheckedUtc is not null);}
        finally {await worker.StopAsync(CancellationToken.None);}
        var failed=store.GetSource(first)!;
        Assert.Equal("Error",failed.Status);
        Assert.Equal("Monitoring request timed out",failed.LastDetail);
        Assert.Equal(1,failed.FailCount);
        Assert.InRange(DateTimeOffset.Parse(failed.NextCheckUtc!)-DateTimeOffset.UtcNow,TimeSpan.FromMinutes(14),TimeSpan.FromMinutes(16));
        Assert.Equal("Unavailable",store.GetSource(second)!.Status);
        Assert.True(store.HasNotified(first));
        Assert.Contains(store.Events(),e=>e.SourceId==first&&e.Level=="warning");
        Assert.Equal(2,JsonSerializer.SerializeToElement(store.Metrics(1)).GetProperty("totalChecks").GetInt32());
        if(stage=="body")Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task HttpClient_cancellation_is_recorded_and_uses_existing_backoff()
    {
        var id=Add("first");
        using var client=new HttpClient(new Handler((_,_)=>throw new TaskCanceledException("HTTP timeout")));
        var engine=Engine(client);
        Assert.Equal("Error",(await engine.Check(id,CancellationToken.None)).Status);
        Assert.Equal("Error",(await engine.Check(id,CancellationToken.None)).Status);
        Assert.Equal(2,store.GetSource(id)!.FailCount);
        Assert.InRange(DateTimeOffset.Parse(store.GetSource(id)!.NextCheckUtc!)-DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(29),TimeSpan.FromMinutes(31));
        Assert.Equal(2,store.Events().Count(e=>e.Level=="warning"));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_recording_failure_and_releases_gate()
    {
        var first=Add("first");var second=Add("second");var stream=new StalledStream();
        using var client=new HttpClient(new Handler((request,_)=>Task.FromResult(request.RequestUri!.AbsolutePath=="/second"
            ?Healthy("html"):new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream){Headers={ContentType=new("text/html")}}})));
        var engine=Engine(client);
        using var caller=new CancellationTokenSource();
        var check=engine.Check(first,caller.Token);
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(3));caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>check);
        Assert.Null(store.GetSource(first)!.LastCheckedUtc);
        Assert.Equal(0,store.GetSource(first)!.FailCount);
        Assert.Empty(store.Events());
        Assert.True(stream.Disposed);
        Assert.Equal("Unavailable",(await engine.Check(second,CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Shutdown_cancels_in_flight_body_without_timeout_logging()
    {
        var id=Add("first");var stream=new StalledStream();
        using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {Content=new StreamContent(stream){Headers={ContentType=new("text/html")}}})));
        using var worker=new PollWorker(store,Engine(client),NullLogger<PollWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try {await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        finally {await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Null(store.GetSource(id)!.LastCheckedUtc);
        Assert.Empty(store.Events());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Unexpected_source_failure_does_not_abort_remaining_sources()
    {
        var first=Add("first");var second=Add("second");
        using(var c=new SqliteConnection($"Data Source={Path.Combine(directory,"test.db")}"))
        {
            c.Open();using var cmd=c.CreateCommand();
            cmd.CommandText=$"CREATE TRIGGER fail_first BEFORE INSERT ON check_history WHEN NEW.source_id={first} BEGIN SELECT RAISE(FAIL,'simulated source write failure'); END";
            cmd.ExecuteNonQuery();
        }
        using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(Healthy("html"))));
        using var worker=new PollWorker(store,Engine(client),NullLogger<PollWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try {await Until(()=>store.GetSource(second)!.LastCheckedUtc is not null);}
        finally {await worker.StopAsync(CancellationToken.None);}
        Assert.Equal("Unavailable",store.GetSource(second)!.Status);
        Assert.Contains(store.Events(),e=>e.SourceId==first&&e.Level=="error"&&e.Message.Contains("simulated source write failure"));
    }

    sealed class Factory(HttpClient client) : IHttpClientFactory {public HttpClient CreateClient(string name)=>client;}
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);}
    sealed class StalledStream : Stream
    {
        public TaskCompletionSource Reading {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed {get;private set;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {Reading.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return 0;}
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>throw new NotSupportedException();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
}
