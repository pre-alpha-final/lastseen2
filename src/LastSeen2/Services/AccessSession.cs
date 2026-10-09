using Microsoft.JSInterop;

namespace LastSeen2.Services;

public sealed class AccessSession(IJSRuntime js)
{
    private const string StorageKey = "lastseen2.accessKey";
    public string? Key { get; private set; }
    public async Task RestoreAsync() => Key = await js.InvokeAsync<string?>("sessionStorage.getItem", StorageKey);
    public async Task SaveAsync(string key)
    {
        await js.InvokeVoidAsync("sessionStorage.setItem", StorageKey, key);
        Key = key;
    }
    public async Task ClearAsync()
    {
        Key = null;
        await js.InvokeVoidAsync("sessionStorage.removeItem", StorageKey);
    }
}
