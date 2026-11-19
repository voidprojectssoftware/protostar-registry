using System.Runtime.CompilerServices;
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

    /// <summary>Lists the blobs whose keys start with <paramref name="prefix"/>, one page at a time, in key order.</summary>
    /// <remarks>Pages are bounded by the store (at most 1,000 keys for S3), so a caller never holds the whole listing.</remarks>
    IAsyncEnumerable<IReadOnlyList<StoredBlob>> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Deletes the blob at <paramref name="key"/>. Deleting a blob that is already gone succeeds.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>A blob as the store lists it.</summary>
/// <param name="Key">The blob's key.</param>
/// <param name="LastModified">When the store says the blob was written, by the store's clock.</param>
public sealed record StoredBlob(string Key, DateTimeOffset LastModified);

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

    /// <summary>Whether this instance sweeps orphaned transcript chunk blobs in the background. Default true.</summary>
    /// <remarks>
    /// The sweep treats every blob that this instance's database does not reference as unreferenced, so it is
    /// only correct when the bucket belongs to that one database. Turn it off where several databases share a
    /// bucket, as the local Aspire stacks of different checkouts do.
    /// </remarks>
    public bool OrphanSweepEnabled { get; set; } = true;

    /// <summary>How often each API instance sweeps orphaned transcript chunk blobs. Default six hours.</summary>
    /// <remarks>
    /// A pass lists the whole <c>transcripts/</c> prefix (one store request and one database query per
    /// 1,000 blobs), while orphans only come from genuine append races and cost a few MiB each, so sweeping
    /// rarely loses nothing. With the 24-hour grace an orphan lives at most about 30 hours.
    /// </remarks>
    public TimeSpan OrphanSweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How old an unreferenced chunk blob must be, by the store's clock, before the sweep deletes it. Default 24 hours.</summary>
    /// <remarks>
    /// A chunk's blob is written before its database row commits, so a young unreferenced blob may belong to
    /// an append still in progress. This period must exceed the longest such append plus the worst clock skew
    /// between the API hosts and the store. See <c>OrphanChunkBlobPolicy</c>.
    /// <para>
    /// An append stores its blob only after it has received the whole request body, so a slow client link does
    /// not stretch this window: it spans the API's upload of at most 16 MiB to the store and one database
    /// commit. Even over a 64 kbit/s link that upload takes about 35 minutes. A day covers that many times
    /// over, plus hours of clock skew, and keeping a rare orphan for a day costs nothing that matters.
    /// </para>
    /// </remarks>
    public TimeSpan OrphanSweepGrace { get; set; } = TimeSpan.FromHours(24);
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

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<StoredBlob>> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(cancellationToken);
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix };
        while (true)
        {
            var response = await _s3.ListObjectsV2Async(request, cancellationToken);

            // The SDK leaves S3Objects null, not empty, when a page has no keys.
            var page = (response.S3Objects ?? [])
                .Select(o => new StoredBlob(o.Key, new DateTimeOffset(o.LastModified!.Value.ToUniversalTime(), TimeSpan.Zero)))
                .ToList();
            if (page.Count > 0)
                yield return page;

            if (response.IsTruncated != true)
                yield break;
            request.ContinuationToken = response.NextContinuationToken;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        // S3 answers a delete of a missing key with success, which is what makes concurrent sweeps safe to repeat.
        await _s3.DeleteObjectAsync(_bucket, key, cancellationToken);
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
