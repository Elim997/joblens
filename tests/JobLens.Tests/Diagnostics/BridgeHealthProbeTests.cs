using System.Net;
using System.Net.Sockets;
using JobLens.Core.Diagnostics;

namespace JobLens.Tests.Diagnostics;

public class BridgeHealthProbeTests
{
    [Fact]
    public async Task IsHealthyAsync_ListeningLocalPort_ReturnsTrue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new TcpBridgeHealthProbe();

        var healthy = await probe.IsHealthyAsync(port, CancellationToken.None);

        Assert.True(healthy);
    }

    [Fact]
    public async Task IsHealthyAsync_ClosedLocalPort_ReturnsFalse()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var probe = new TcpBridgeHealthProbe();

        var healthy = await probe.IsHealthyAsync(port, CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task IsHealthyAsync_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new TcpBridgeHealthProbe();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.IsHealthyAsync(8080, cancellation.Token));
    }
}
