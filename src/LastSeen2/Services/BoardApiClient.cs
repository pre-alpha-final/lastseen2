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

    public Task<BoardSnapshot> GetBoardAsync(string key) => ReadAsync<BoardSnapshot>(HttpMethod.Get, ApiRoutes.Board, key);

    public Task<BoardSnapshot> SaveVideoAsync(string key, Guid? id, SaveVideoRequest value) =>
        ReadAsync<BoardSnapshot>(id is null ? HttpMethod.Post : HttpMethod.Put, ApiRoutes.Videos + (id is null ? "" : $"/{id:D}"), key, value);

    public Task<BoardSnapshot> SaveChannelAsync(string key, Guid? id, SaveChannelRequest value) =>
        ReadAsync<BoardSnapshot>(id is null ? HttpMethod.Post : HttpMethod.Put, ApiRoutes.Channels + (id is null ? "" : $"/{id:D}"), key, value);

    public Task<BoardSnapshot> DeleteAsync(string key, bool video, Guid id, long revision) =>
        ReadAsync<BoardSnapshot>(HttpMethod.Delete, (video ? ApiRoutes.Videos : ApiRoutes.Channels) + $"/{id:D}?expectedRevision={revision}", key);

    public Task<MetadataResult> MetadataAsync(string key, string url) =>
        ReadAsync<MetadataResult>(HttpMethod.Post, ApiRoutes.Metadata, key, new MetadataRequest(url));

    public Task<BoardSnapshot> ReorderVideosAsync(string key, ReorderVideosRequest value) =>
        ReadAsync<BoardSnapshot>(HttpMethod.Put, ApiRoutes.Videos + "/reorder", key, value);

    public Task<BoardSnapshot> MoveChannelAsync(string key, Guid id, MoveChannelRequest value) =>
        ReadAsync<BoardSnapshot>(HttpMethod.Put, ApiRoutes.Channels + $"/{id:D}/move", key, value);

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, string key, object? body = null)
    {
        using var response = await SendAsync(method, path, key, body);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new ApiClientException("The server returned an empty response. Reload the board before retrying.", review: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new ApiClientException("Could not read the server response. Reload the board before retrying.", review: true);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(http.BaseAddress!, path));
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add(ApiRoutes.AccessKeyHeader, key);
        // A redirect must never forward the custom key header to another origin.
        request.SetBrowserRequestOption("redirect", "error");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiClientException("Could not reach the server. Check your connection and try again.", review: true);
        }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new ApiClientException(status switch
        {
            HttpStatusCode.Unauthorized => "The access key was not accepted. Please unlock again.",
            HttpStatusCode.TooManyRequests => "Too many unlock attempts. Wait a minute, then try again.",
            HttpStatusCode.Conflict => "The board changed in another session. Reload and review before saving again.",
            HttpStatusCode.NotFound => "This bookmark no longer exists. Reload and review the board.",
            HttpStatusCode.BadRequest => "Check the title/name and HTTP(S) URLs. URLs must not contain credentials.",
            HttpStatusCode.RequestEntityTooLarge => "The board is full. Remove bookmarks or shorten their values before retrying.",
            HttpStatusCode.ServiceUnavailable => "Bookmark storage is unavailable. Try again in a moment.",
            _ => "The server could not complete this request. Try again."
        }, status == HttpStatusCode.Unauthorized, status == HttpStatusCode.Conflict || status == HttpStatusCode.NotFound || (int)status >= 500);
    }
}

public sealed class ApiClientException(string message, bool unauthorized = false, bool review = false) : Exception(message)
{
    public bool RequiresReview { get; } = review;
    public bool Unauthorized { get; } = unauthorized;
}
