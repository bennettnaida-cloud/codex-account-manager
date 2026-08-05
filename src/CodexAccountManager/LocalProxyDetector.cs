using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CodexAccountManager;

internal sealed record LocalProxyDetectionResult(
    string Address,
    int Port,
    string Scheme,
    string Description)
{
    public string UriText => $"{Scheme}://{Address}:{Port}";
}

internal static class LocalProxyDetector
{
    private const int ProbeHeaderLimit = 4096;
    private const int ProbeTimeoutMilliseconds = 1200;
    private static readonly byte[] ProbePayload = Encoding.ASCII.GetBytes("CAM-PROXY-PROBE");

    private static readonly int[] CommonPorts =
    [
        10808, 10809, 7890, 7891, 8080, 8118, 1080, 8888, 3128, 20171
    ];

    public static async Task<LocalProxyDetectionResult?> DetectAsync(
        int? preferredPort = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = BuildCandidatePorts(preferredPort);
        foreach (var batch in candidates.Chunk(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(batch.Select(async port =>
                (Port: port, Result: await ProbeHttpProxyAsync(port, cancellationToken))));
            var detected = results
                .OrderBy(result => Array.IndexOf(batch, result.Port))
                .Select(result => result.Result)
                .FirstOrDefault(result => result != null);
            if (detected != null)
            {
                return detected;
            }
        }

        return null;
    }

    internal static Task<LocalProxyDetectionResult?> DetectPortAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (port is <= 0 or > 65535 || port == LocalPatGateway.Port)
        {
            return Task.FromResult<LocalProxyDetectionResult?>(null);
        }

        return ProbeHttpProxyAsync(port, cancellationToken);
    }

    internal static IReadOnlyList<int> BuildCandidatePorts(int? preferredPort = null)
    {
        var ports = new HashSet<int>();
        if (preferredPort is > 0 and <= 65535 && preferredPort != LocalPatGateway.Port)
        {
            ports.Add(preferredPort.Value);
        }

        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == LocalPatGateway.Port ||
                    endpoint.Port <= 0 ||
                    endpoint.Port > 65535 ||
                    !IsLoopback(endpoint.Address))
                {
                    continue;
                }

                ports.Add(endpoint.Port);
            }
        }
        catch
        {
            // The common-port fallback below is sufficient when listener enumeration is
            // unavailable due to a restricted Windows networking provider.
        }

        foreach (var port in CommonPorts)
        {
            if (port != LocalPatGateway.Port)
            {
                ports.Add(port);
            }
        }

        return ports
            .OrderBy(port => preferredPort.HasValue && port == preferredPort.Value ? 0 : 1)
            .ThenBy(port => Array.IndexOf(CommonPorts, port) < 0
                ? int.MaxValue
                : Array.IndexOf(CommonPorts, port))
            .ThenBy(port => port)
            .Take(48)
            .ToArray();
    }

    private static async Task<LocalProxyDetectionResult?> ProbeHttpProxyAsync(
        int port,
        CancellationToken cancellationToken)
    {
        // A local service can return a superficially valid CONNECT status without
        // actually being an HTTP tunnel (QQ's private port does exactly this). Keep
        // the probe local, but verify a real CONNECT plus a byte-for-byte round trip
        // through a short-lived loopback echo listener.
        using var targetListener = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        Task<TcpClient>? acceptTask = null;
        try
        {
            targetListener.Start();
            var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
            acceptTask = targetListener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                cancellationToken);
            using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{targetPort}\r\n" +
                "Proxy-Connection: keep-alive\r\n\r\n");
            await stream.WriteAsync(request, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var response = await ReadHttpHeadersAsync(stream, cancellationToken);
            if (response == null)
            {
                return null;
            }

            var statusLine = response.Split("\r\n", 2, StringSplitOptions.None)[0].Trim();
            var statusParts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (statusParts.Length < 2 ||
                !statusParts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(statusParts[1], out var statusCode) ||
                statusCode != 200)
            {
                return null;
            }

            var targetClient = await acceptTask.WaitAsync(
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                cancellationToken);
            using (targetClient)
            {
                using var targetStream = targetClient.GetStream();
                await stream.WriteAsync(ProbePayload, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                var received = new byte[ProbePayload.Length];
                if (!await ReadExactlyAsync(targetStream, received, cancellationToken) ||
                    !received.AsSpan().SequenceEqual(ProbePayload))
                {
                    return null;
                }

                await targetStream.WriteAsync(received, cancellationToken);
                await targetStream.FlushAsync(cancellationToken);
                var echoed = new byte[ProbePayload.Length];
                if (!await ReadExactlyAsync(stream, echoed, cancellationToken) ||
                    !echoed.AsSpan().SequenceEqual(ProbePayload))
                {
                    return null;
                }
            }

            return new LocalProxyDetectionResult(
                "127.0.0.1",
                port,
                "http",
                $"本地 HTTP CONNECT 代理：{statusLine}，已验证数据隧道");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            TimeoutException or
            ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            targetListener.Stop();
            if (acceptTask is { IsCompleted: false })
            {
                try
                {
                    await acceptTask;
                }
                catch
                {
                    // Stopping the listener cancels a pending local accept.
                }
            }
        }
    }

    private static async Task<string?> ReadHttpHeadersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < ProbeHeaderLimit)
        {
            var read = await stream.ReadAsync(one.AsMemory(), cancellationToken).AsTask().WaitAsync(
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                cancellationToken);
            if (read == 0)
            {
                return null;
            }

            bytes.Add(one[0]);
            if (bytes.Count >= 4 &&
                bytes[^4] == (byte)'\r' &&
                bytes[^3] == (byte)'\n' &&
                bytes[^2] == (byte)'\r' &&
                bytes[^1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }

        return null;
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).AsTask().WaitAsync(
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    internal static bool IsLoopbackHost(string? host)
    {
        host = host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        host = host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var zoneSeparator = host.IndexOf('%');
        if (zoneSeparator >= 0)
        {
            host = host[..zoneSeparator];
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        return IPAddress.IsLoopback(address) ||
               address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
}
