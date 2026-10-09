using System.Net;
using System.Net.Http.Json;
using LastSeen2.Contracts;
using LastSeen2.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LastSeen2.Server.Tests;

public sealed class AppFactory : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The solution resides under src, while the tests live at the repository root.
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/LastSeen2.Server")));
        builder.UseEnvironment("Development");
    }
}

public class HostingTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/some-client-route")]
    public async Task HostServesClientShell(string path)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"app\">", html);
        Assert.Contains("blazor.webassembly", html);
        Assert.DoesNotContain("#[", html);
    }

    [Theory]
    [InlineData("/api/missing")]
    [InlineData("/api/missing.json")]
    [InlineData("/api")]
    public async Task UnknownApiRoutesReturnProblemDetailsInsteadOfHtml(string path)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("not_found", error!.Code);
    }

    [Theory]
    [InlineData("POST", "/api/auth/check")]
    [InlineData("GET", "/api/board")]
    [InlineData("POST", "/api/videos")]
    [InlineData("PUT", "/api/videos/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/videos/11111111-1111-1111-1111-111111111111?expectedRevision=0")]
    [InlineData("PUT", "/api/videos/reorder")]
    [InlineData("POST", "/api/channels")]
    [InlineData("PUT", "/api/channels/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/channels/11111111-1111-1111-1111-111111111111?expectedRevision=0")]
    [InlineData("PUT", "/api/channels/11111111-1111-1111-1111-111111111111/move")]
    [InlineData("POST", "/api/metadata")]
    public async Task FoundationDoesNotClaimToAuthenticateOrPersist(string method, string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("not_implemented", error!.Code);
        Assert.Equal(501, error.Status);
    }

    [Theory]
    [InlineData("/_framework/blazor.webassembly.js")]
    [InlineData("/_framework/dotnet.js")]
    [InlineData("/_framework/dotnet.native.wasm")]
    [InlineData("/css/app.css")]
    public async Task ClientBootAssetsAreServed(string path)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ServerConfigurationIsNotAPublicAsset()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/appsettings.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ServerOptionsSupportConfigurationOverrides()
    {
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:LastSeen2"] = "mongodb://localhost:27017/test_database",
                    ["MongoDb:Collection"] = "test_override",
                    ["Authentication:AccessKey"] = "test-only-not-a-real-key"
                })));
        Assert.Equal("mongodb://localhost:27017/test_database", configured.Services.GetRequiredService<IOptions<MongoDbOptions>>().Value.ConnectionString);
        Assert.Equal("test_override", configured.Services.GetRequiredService<IOptions<MongoDbOptions>>().Value.Collection);
        Assert.Equal("test-only-not-a-real-key", configured.Services.GetRequiredService<IOptions<AccessKeyOptions>>().Value.AccessKey);
    }
}
