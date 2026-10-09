using LastSeen2.Contracts;
using LastSeen2.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOptions<MongoDbOptions>()
    .Bind(builder.Configuration.GetSection(MongoDbOptions.SectionName))
    .Configure(options => options.ConnectionString =
        builder.Configuration.GetConnectionString("LastSeen2") ?? string.Empty);
builder.Services.Configure<AccessKeyOptions>(builder.Configuration.GetSection(AccessKeyOptions.SectionName));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFoundationApi();
// Never return the SPA shell for an unknown API route (including paths with dots).
app.MapFallback(ApiRoutes.Prefix + "/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound, title: "API endpoint not found",
    extensions: new Dictionary<string, object?> { ["code"] = "not_found" }));
app.MapFallbackToFile("index.html");
app.Run();

// Exposes the entry point to WebApplicationFactory without coupling to the client Program.
public partial class Program { }
