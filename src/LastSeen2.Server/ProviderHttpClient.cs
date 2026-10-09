using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LastSeen2.Server;

// No proxy, cookies, automatic redirects, credentials, or app API headers.
public sealed class ProviderHttpClient(HttpClient client) : IDisposable
{
    public const int MaxBytes = 512 * 1024;
    public static bool IsAllowed(Uri uri) => uri.Scheme == "https" && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo) && uri.Host is "www.youtube.com" or "www.twitch.tv" or "clips.twitch.tv";

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(3), MaxResponseHeadersLength = 32,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = (context, ct) => PublicNetwork.ConnectAsync(context.DnsEndPoint, ct)
    };

    public async Task<string> GetAsync(Uri uri, string mediaType, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (!IsAllowed(uri)) throw new HttpRequestException("Unsupported metadata destination");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(mediaType);
            request.Headers.UserAgent.ParseAdd("LastSeen2/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is not { } location)
                    throw new HttpRequestException("Invalid metadata redirect");
                var next = new Uri(uri, location);
                // Do not cross providers, even between otherwise allowed hosts.
                if (!IsAllowed(next) || (uri.Host == "www.youtube.com") != (next.Host == "www.youtube.com"))
                    throw new HttpRequestException("Unsafe metadata redirect");
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaxBytes)
                throw new HttpRequestException("Invalid metadata response");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > MaxBytes) throw new HttpRequestException("Metadata response too large");
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    public void Dispose() => client.Dispose();
}

public static class PublicNetwork
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 88 && b[2] == 99)))
                && !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Only global unicast; reject transition, documentation and special-purpose ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)))
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }

    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, Task<Stream>>? connect = null)
    {
        if (endpoint.Port != 443 || endpoint.Host is not ("www.youtube.com" or "www.twitch.tv" or "clips.twitch.tv"))
            throw new HttpRequestException("Unsupported metadata destination");
        var addresses = await (resolve ?? Dns.GetHostAddressesAsync)(endpoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
            throw new HttpRequestException("Nonpublic metadata destination");
        // Connect to the checked IP, never resolve the hostname a second time (DNS rebinding).
        foreach (var address in addresses)
        {
            ct.ThrowIfCancellationRequested();
            try { return await (connect ?? ConnectSocket)(address, endpoint.Port, ct); }
            catch (SocketException) { }
        }
        throw new HttpRequestException("Metadata connection failed");
    }

    private static async Task<Stream> ConnectSocket(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
