using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace StockSentinel.Tests;

public sealed class PublishExclusionTests
{
    [Theory]
    [InlineData("publish/")]
    [InlineData("alternate-output/")]
    public async Task Evaluated_items_exclude_tests_nested_builds_and_previous_publish_outputs(string publishDir)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockSentinel.Web.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var temp = Path.Combine(Path.GetTempPath(), "stocksentinel-items-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            File.Copy(Path.Combine(root.FullName, "StockSentinel.Web.csproj"), Path.Combine(temp, "StockSentinel.Web.csproj"));
            var excluded = new[] { "tests/StockSentinel.Tests/bin/Release/net10.0/tests/old.json",
                "tests/StockSentinel.Tests/obj/project.assets.json", "tests/Example.cs",
                "publish/publish/old.runtimeconfig.json", "publish/old.cs", "publish/wwwroot/index.html",
                "data/push-vapid.json", "data/push-subscriptions.json", "data/sentinel.db", "data/sentinel.db-wal",
                "data/nested/secrets.config", "data/nested/private.key", "data/ignored.cs", "data/resource.resx",
                "nested/bin/old.json", "nested/obj/old.json", publishDir + "previous.json" };
            var included = new[] { "wwwroot/index.html", "wwwroot/app.js", "wwwroot/icon.png", "wwwroot/sw.js",
                "wwwroot/manifest.webmanifest", "appsettings.json", "Program.cs" };
            foreach (var file in excluded.Concat(included))
            {
                var path = Path.Combine(temp, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
            }
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = temp,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "msbuild", "StockSentinel.Web.csproj", "-nologo",
                "-getItem:Content,None,Compile,EmbeddedResource", "-p:RuntimeIdentifier=linux-arm", "-p:PublishDir=" + publishDir })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errors);
            using var json = JsonDocument.Parse(await output);
            var items = json.RootElement.GetProperty("Items").EnumerateObject()
                .SelectMany(group => group.Value.EnumerateArray())
                .Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/')).ToHashSet();
            foreach (var file in excluded) Assert.DoesNotContain(file, items);
            foreach (var file in included) Assert.Contains(file, items);
        }
        finally { Directory.Delete(temp, true); }
    }
}
