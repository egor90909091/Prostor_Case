using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Prostor.Tz;

public sealed class TzConfig
{
    public string ConnectionString { get; init; } = "";
    public string S3Endpoint { get; init; } = "http://minio:9000";
    public string S3AccessKey { get; init; } = "minioadmin";
    public string S3SecretKey { get; init; } = "minioadmin";
    public string S3Bucket { get; init; } = "prostor-tz";

    public static TzConfig FromEnvironment(IConfiguration configuration)
    {
        string Get(string key, string fallback) =>
            configuration[key] is { Length: > 0 } value ? value : fallback;

        var host = Get("PGHOST", "db");
        var database = Get("PGDATABASE", "prostor");
        var user = Get("PGUSER", "prostor");
        var password = Get("PGPASSWORD", "prostor");
        var port = int.TryParse(configuration["PGPORT"], out var p) ? p : 5432;

        return new TzConfig
        {
            ConnectionString = Get("DATABASE_URL",
                $"Host={host};Port={port};Database={database};Username={user};Password={password};" +
                "Include Error Detail=true;Maximum Pool Size=10"),
            S3Endpoint = Get("S3_ENDPOINT", "http://minio:9000"),
            S3AccessKey = Get("S3_ACCESS_KEY", "minioadmin"),
            S3SecretKey = Get("S3_SECRET_KEY", "minioadmin"),
            S3Bucket = Get("S3_BUCKET", "prostor-tz")
        };
    }
}

/// <summary>
/// Хранилище документов: MinIO по S3-совместимому протоколу.
/// В БД лежат только метаданные и storage_key — тело файла в объектном хранилище.
/// </summary>
public sealed class DocumentStorage
{
    private readonly IAmazonS3 _s3;
    private readonly TzConfig _config;
    private readonly ILogger<DocumentStorage> _log;
    private bool _bucketChecked;

    public DocumentStorage(TzConfig config, ILogger<DocumentStorage> log)
    {
        _config = config;
        _log = log;
        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(config.S3AccessKey, config.S3SecretKey),
            new AmazonS3Config
            {
                ServiceURL = config.S3Endpoint,
                ForcePathStyle = true,          // MinIO не умеет virtual-host стиль по умолчанию
                AuthenticationRegion = "us-east-1"
            });
    }

    public async Task<string> PutAsync(string key, byte[] content, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        using var stream = new MemoryStream(content);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _config.S3Bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            DisablePayloadSigning = true
        }, ct);
        return key;
    }

    public async Task<Stream> GetAsync(string key, CancellationToken ct)
    {
        var response = await _s3.GetObjectAsync(_config.S3Bucket, key, ct);
        return response.ResponseStream;
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketChecked) return;
        try
        {
            var buckets = await _s3.ListBucketsAsync(ct);
            if (buckets.Buckets.All(b => b.BucketName != _config.S3Bucket))
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _config.S3Bucket }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "не удалось проверить bucket {Bucket}", _config.S3Bucket);
        }
        _bucketChecked = true;
    }
}
