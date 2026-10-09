namespace LastSeen2.Server;

// Server-only options. Required-value/startup validation is implemented in Phase 2.
public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";
    public string ConnectionString { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
}

public sealed class AccessKeyOptions
{
    public const string SectionName = "Authentication";
    public string AccessKey { get; set; } = string.Empty;
}
