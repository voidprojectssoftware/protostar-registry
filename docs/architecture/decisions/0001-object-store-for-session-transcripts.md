# 0001. Session transcripts live in an S3-compatible object store

- **Status:** accepted (records what registry #25 built)
- **Date:** 2026-11-19
- **Issue:** #27, following #25 and voidprojectssoftware/protostar-cli#14

## Context

Session backup (protostar-cli#14) uploads harness transcripts, which are JSONL files that only grow. They
are large: #14 cites a single Claude Code transcript of 502 MB, and the PR that built backup (#25) found
one over 500 MB on the dev machine. A developer's whole history runs to gigabytes. Until #25, Postgres
was the registry's only store.

## Decision

Transcript bytes go to an S3-compatible object store. Postgres keeps only bookkeeping and the parsed
summary that listings read.

What the code does today:

- **Chunks, not files.** A transcript is stored as append-only chunks of at most 16 MiB uncompressed
  (`Transcript.MaxChunkBytes`). Each chunk is gzip-compressed and written as one object under
  `transcripts/<owner>/<transcript>/r<revision>/<offset>-<guid>.jsonl.gz`
  (`TranscriptService.AppendAsync`). Postgres holds the `Transcript` and `TranscriptChunk` rows: offsets,
  lengths, SHA-256, the object key, and the summary.
- **One small interface.** `IBlobStore` (`Infrastructure/BlobStorage.cs`) has two operations, put and
  copy-to-stream. `S3BlobStore` is its only implementation. Nothing outside it references `AWSSDK.S3`.
- **Plain S3 over AWSSDK.S3, path-style.** `S3BlobStore` sets `ForcePathStyle = true` and calculates or
  validates checksums only when an operation requires it, which is what S3-compatible servers other
  than AWS expect. Configuration is the `ObjectStore` section: `ServiceUrl` (empty means AWS itself,
  chosen by `Region`), `Region`, `AccessKey`, `SecretKey`, `Bucket` (default `protostar-registry`). The
  bucket is created on first use if it is missing.
- **RustFS under Aspire for development.** The AppHost runs the `rustfs/rustfs:1.0.5` container with a
  named data volume (`protostar-registry-objectstore`) and passes its endpoint and credentials to the API
  as `ObjectStore__*`. The credentials are Aspire parameters generated once per machine and persisted in
  user-secrets. RustFS was chosen because MinIO, the obvious candidate, no longer publishes its images.

## Hosting

The API speaks plain S3, so a hosted registry needs any S3-compatible store reachable from the API
(for example AWS S3, Cloudflare R2, RustFS, Garage or SeaweedFS), configured through the `ObjectStore`
section, plus Postgres. Where the shared registry runs is undecided (#23).

## Consequences

- A second piece of infrastructure beside Postgres, in development and in any deployment.
- Swapping the store means writing another `IBlobStore`. Nothing else changes.
- The object is written before the database commit. If two appends to one transcript race, the loser's
  object is left in the store with no chunk row pointing at it. The position is checked first, so only
  a genuine race does this.
- **Not decided here, and not implemented:** retention, deletion, lifecycle rules, backup of the object
  store or of Postgres, and encryption beyond what the chosen store provides. `IBlobStore` has no delete
  operation. Each of these needs its own decision when it is built.

## Options considered

- **Postgres rows (`bytea` or large objects).** Rejected: hundreds of MB per transcript and gigabytes per
  user would bloat the database and its backups, and every read would pass through the database.
- **The API's local disk.** Rejected: ties content to one host and its disk, and gives nothing an S3
  store does not, since an S3-compatible server can run on that same host.
- **MinIO as the development store.** Rejected: its images are no longer published.
