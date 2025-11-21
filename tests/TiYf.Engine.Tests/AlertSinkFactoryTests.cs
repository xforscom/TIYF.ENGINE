using System;
using System.Net.Http;
using TiYf.Engine.Host.Alerts;
using Xunit;

namespace TiYf.Engine.Tests;

public class AlertSinkFactoryTests
{
    private sealed class DummyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    [Fact]
    public void Factory_ReturnsNoop_WhenUnset()
    {
        Environment.SetEnvironmentVariable("ALERT_SINK_TYPE", null);
        var sink = AlertSinkFactory.Create(new DummyFactory(), "demo");
        Assert.IsType<NoopAlertSink>(sink);
    }

    [Fact]
    public void Factory_ReturnsFile_WhenConfigured()
    {
        Environment.SetEnvironmentVariable("ALERT_SINK_TYPE", "file");
        Environment.SetEnvironmentVariable("ALERT_FILE_PATH", "/tmp/alert-proof/test.log");
        var sink = AlertSinkFactory.Create(new DummyFactory(), "demo");
        Assert.IsType<FileAlertSink>(sink);
    }
}
