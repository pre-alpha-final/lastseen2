using LastSeen2.Contracts;
using LastSeen2.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOptions<MongoDbOptions>()
    .Bind(builder.Configuration.GetSection(MongoDbOptions.SectionName))
    .Configure(options => options.ConnectionString =
        builder.Configuration.GetConnectionString("LastSeen2") ?? string.Empty)
    .Validate(MongoDbOptions.IsValid, "ConnectionStrings:LastSeen2 must be a valid MongoDB URI with a database name, and MongoDb:Collection must be valid.")
    .ValidateOnStart();
builder.Services.AddOptions<AccessKeyOptions>().Bind(builder.Configuration.GetSection(AccessKeyOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.AccessKey) && options.AccessKey.Length <= 4096,
        "Authentication:AccessKey is required and must not exceed 4096 characters.").ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AccessKeyGuard>();
builder.Services.AddSingleton<IBoardStore, MongoBoardStore>();
builder.Services.AddSingleton<BoardService>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<ApiBoundaryMiddleware>();
app.MapBookmarkApi();
// Never return the SPA shell for an unknown API route (including paths with dots).
app.MapFallback(ApiRoutes.Prefix + "/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound, title: "API endpoint not found",
    extensions: new Dictionary<string, object?> { ["code"] = "not_found" }));
app.MapFallbackToFile("index.html");
app.Run();

// Exposes the entry point to WebApplicationFactory without coupling to the client Program.
public partial class Program { }
