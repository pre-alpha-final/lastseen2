using System.Net;
using System.Net.Http.Json;
using LastSeen2.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace LastSeen2.Services;

// Only fixed, same-origin API endpoints can receive the key. Never use default headers.
public sealed class BoardApiClient(HttpClient http)
{
    public async Task CheckKeyAsync(string key) =>
        (await SendAsync(HttpMethod.Post, ApiRoutes.CheckKey, key)).Dispose();

    public async Task<BoardSnapshot> GetBoardAsync(string key)
    {
        using var response = await SendAsync(HttpMethod.Get, ApiRoutes.Board, key);
        try
        {
            return await response.Content.ReadFromJsonAsync<BoardSnapshot>()
                ?? throw new ApiClientException("The server returned an empty board. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiClientException("Could not finish loading the board. Check your connection and try again.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ApiClientException("The server returned an unreadable board. Try again.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key)
    {
        using var request = new HttpRequestMessage(method, new Uri(http.BaseAddress!, path));
        request.Headers.Add(ApiRoutes.AccessKeyHeader, key);
        // A redirect must never forward the custom key header to another origin.
        request.SetBrowserRequestOption("redirect", "error");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiClientException("Could not reach the server. Check your connection and try again.");
        }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new ApiClientException(status switch
        {
            HttpStatusCode.Unauthorized => "The access key was not accepted. Please unlock again.",
            HttpStatusCode.TooManyRequests => "Too many unlock attempts. Wait a minute, then try again.",
            HttpStatusCode.ServiceUnavailable => "Bookmark storage is unavailable. Try again in a moment.",
            _ => "The server could not complete this request. Try again."
        }, status == HttpStatusCode.Unauthorized);
    }
}

public sealed class ApiClientException(string message, bool unauthorized = false) : Exception(message)
{
    public bool Unauthorized { get; } = unauthorized;
}
