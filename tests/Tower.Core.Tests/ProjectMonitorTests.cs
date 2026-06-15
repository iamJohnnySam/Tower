using Tower.Core.Projects;
using Xunit;

namespace Tower.Core.Tests;

public class ProjectMonitorTests
{
    [Fact]
    public async Task Port_open_true_for_listening_port_false_for_closed()
    {
        // start a throwaway TCP listener on an ephemeral port
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        Assert.True(await ProjectMonitor.IsPortOpenAsync(port, 500));
        l.Stop();
        Assert.False(await ProjectMonitor.IsPortOpenAsync(port, 300));
    }
}
