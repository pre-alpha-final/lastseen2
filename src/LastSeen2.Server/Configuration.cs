using MongoDB.Driver;

namespace LastSeen2.Server;

public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";
    public string ConnectionString { get; set; } = string.Empty;
    public string Collection { get; set; } = "lastseen2";

    public static bool IsValid(MongoDbOptions options)
    {
        try
        {
            var url = new MongoUrl(options.ConnectionString);
            return !string.IsNullOrWhiteSpace(url.DatabaseName)
                && !url.DatabaseName.Any(c => "/\\. \"$*<>:|?".Contains(c) || char.IsControl(c))
                && !string.IsNullOrWhiteSpace(options.Collection)
                && !options.Collection.Contains('$') && !options.Collection.Contains('\0')
                && !options.Collection.StartsWith("system.", StringComparison.Ordinal);
        }
        catch { return false; } // Never include driver parsing errors: they can contain credentials.
    }
}

public sealed class AccessKeyOptions
{
    public const string SectionName = "Authentication";
    public string AccessKey { get; set; } = string.Empty;
}
