using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// With a polling file watcher (mounted ConfigMap), an invalid reload throws inside the options
/// monitor's callback. This proves the process survives it.
/// </summary>
public sealed class PollingFileWatcherConfigurationTests : IDisposable
{
    private const string Valid = """{ "ApiPipeline": { "SecurityHeaders": { "XFrameOptionsValue": "DENY" } } }""";
    private const string Invalid = """{ "ApiPipeline": { "SecurityHeaders": { "XFrameOptionsValue": "ALLOW-FROM x" } } }""";

    private readonly string _directory = Directory.CreateTempSubdirectory("apipipeline-reload-").FullName;

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// An invalid edit picked up by the polling watcher does not crash the process, and requests
    /// are still served with the value read at startup.
    /// </summary>
    [Fact]
    public async Task InvalidEdit_FromAPolledFile_DoesNotCrash_AndKeepsTheStartupValue()
    {
        var path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(path, Valid, TestContext.Current.CancellationToken);

        var unhandled = new List<object>();
        UnhandledExceptionEventHandler onUnhandled = (_, e) => { lock (unhandled) { unhandled.Add(e.ExceptionObject); } };
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            // What DOTNET_USE_POLLING_FILE_WATCHER=1 switches on, set here rather than through the
            // process-wide variable so other tests are unaffected.
            using var files = new PhysicalFileProvider(_directory) { UsePollingFileWatcher = true, UseActivePolling = true };
            builder.Configuration.AddJsonFile(files, "appsettings.json", optional: false, reloadOnChange: true);
            builder.Services.AddApiPipeline(builder.Configuration);

            await using var app = builder.Build();
            app.UseApiPipeline(p => p.WithSecurityHeaders());
            app.MapGet("/ok", () => "ok");
            await app.StartAsync(TestContext.Current.CancellationToken);
            var client = app.GetTestClient();

            // The framework's monitor, subscribed to the file's change token: the component whose
            // change callback throws on an invalid reload.
            var monitor = app.Services.GetRequiredService<IOptionsMonitor<ApiPipelineSecurityHeadersOptions>>();
            monitor.CurrentValue.XFrameOptionsValue.Should().Be("DENY");

            await File.WriteAllTextAsync(path, Invalid, TestContext.Current.CancellationToken);

            // The polling watcher checks every 4 s; allow a few cycles.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!ReloadDelivered(monitor) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
            }

            ReloadDelivered(monitor).Should().BeTrue("the polling watcher must have delivered the invalid edit");

            // Give any exception thrown on the watcher's thread time to surface.
            await Task.Delay(1000, TestContext.Current.CancellationToken);

            // The reload failed validation, yet nothing escaped the watcher thread.
            monitor.Invoking(m => m.CurrentValue).Should().Throw<OptionsValidationException>();

            var response = await client.GetAsync("/ok", TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");

            lock (unhandled)
            {
                unhandled.Should().BeEmpty("an exception escaping the watcher callback would crash the process");
            }
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
        }
    }

    // The framework monitor fails validation, and throws on every read, once the edit has arrived.
    private static bool ReloadDelivered(IOptionsMonitor<ApiPipelineSecurityHeadersOptions> monitor)
    {
        try
        {
            _ = monitor.CurrentValue;
            return false;
        }
        catch (OptionsValidationException)
        {
            return true;
        }
    }
}
