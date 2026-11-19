using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>Stores opaque byte blobs by key, outside the database.</summary>
/// <remarks>
/// Transcript content is far too large for database rows (a developer's history easily runs to tens of
/// gigabytes), so it lives in an object store and the database keeps only keys and bookkeeping.
/// </remarks>
public interface IBlobStore
{
    /// <summary>Stores <paramref name="content"/> at <paramref name="key"/>, replacing anything already there.</summary>
    Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Copies the blob at <paramref name="key"/> into <paramref name="destination"/>.</summary>
    Task CopyToAsync(string key, Stream destination, CancellationToken cancellationToken);
}

/// <summary>Where the S3-compatible object store is and how to sign in to it (configuration section <c>ObjectStore</c>).</summary>
public sealed class ObjectStoreOptions
{
    public const string Section = "ObjectStore";

    /// <summary>The store's base URL, for example <c>http://localhost:9000</c>. Empty means AWS itself, chosen by <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string AccessKey { get; set; } = default!;

    public string SecretKey { get; set; } = default!;

    /// <summary>The bucket transcript content is stored in. Created on first use if missing.</summary>
    public string Bucket { get; set; } = "protostar-registry";
}

/// <summary><see cref="IBlobStore"/> over any S3-compatible object store (AWS S3, Cloudflare R2, RustFS, Garage, SeaweedFS).</summary>
/// <remarks>
/// Uses path-style addressing and sends checksums only when the operation requires them, which is what
/// S3-compatible servers other than AWS expect. The bucket is created on first use if it does not exist.
/// </remarks>
public sealed class S3BlobStore : IBlobStore, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    public S3BlobStore(IOptions<ObjectStoreOptions> options)
    {
        var o = options.Value;
        var config = new AmazonS3Config
        {
            ForcePathStyle = true,
            AuthenticationRegion = o.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl))
            config.ServiceURL = o.ServiceUrl;
        else
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);

        _s3 = new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
        _bucket = o.Bucket;
    }

    /// <inheritdoc />
    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(cancellationToken);
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/gzip",
            AutoCloseStream = false,
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CopyToAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await _s3.GetObjectAsync(_bucket, key, cancellationToken);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public void Dispose()
    {
        _s3.Dispose();
        _bucketGate.Dispose();
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketReady)
            return;

        await _bucketGate.WaitAsync(cancellationToken);
        try
        {
            if (!_bucketReady)
            {
                try
                {
                    await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);
                }
                catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
                {
                    // Already there.
                }

                _bucketReady = true;
            }
        }
        finally
        {
            _bucketGate.Release();
        }
    }
}
