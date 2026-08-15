# Object storage

**Status:** both open; the first is the clearest outstanding case of the project's second core
rule ("if we fix it in one app, DeployAI should fix it for every app").

## The file-storage layer is still hand-written per app, and it is platform code

DeployAI provisions the bucket, wires the five keys, and verifies the round trip — then every
app writes its own client against them, and every app rediscovers the same four Hetzner quirks:
`ForcePathStyle`, `RequestChecksumCalculation` / `ResponseChecksumValidation` set to
`WHEN_REQUIRED`, SigV4 (Ceph rejects unsigned payloads), and buffering a non-seekable upload
stream before signing. All four failed silently in one app in one session. Two apps in this
account now have two different implementations, one of them weaker.

DeployAI already commits generated files into a repository, so it can generate this the way it
generates Dockerfiles: a storage service, an image pipeline, and a proxy endpoint so bytes go
through the API and the bucket stays private. Two rules constrain it — `UseS3` must require
*all* the settings so a blank value falls back to local disk rather than half-configured S3, and
the composition-root patch must refuse rather than guess when its anchor is missing. Rewriting
an app's own upload call sites is out of scope and must be stated in the PR, not assumed done.

## An app is handed credentials far wider than it needs, and DeployAI cannot narrow them

`ObjectStorageEnvironmentWiring` writes the storage connection's own access key and secret onto
the app, so a container that needs one bucket can list, create and delete every bucket in the
project. They are written as secrets, so Coolify will not read them back, but an app compromise
reaches the whole storage account rather than its own data.

**Researched, so it does not need investigating again:** Hetzner issues S3 credentials per
project and [each key pair is valid for every bucket in it](https://docs.hetzner.com/storage/object-storage/faq/s3-credentials/);
scoping requires a second key pair plus a bucket policy allowlisting it, and key pairs
[can only be generated in Hetzner's console](https://docs.hetzner.com/storage/object-storage/getting-started/generating-s3-keys/) —
there is no API, so DeployAI cannot mint one. The mitigation is therefore user-side: a key pair
(or project) per app, then a bucket policy. DeployAI already supports it — several
`ObjectStorage` connections can exist and each storage target records which one it belongs to —
but nothing guides a user there. What it does now is refuse to hide the exposure: provisioning
reports how many buckets the credentials it just wired can reach. Applying the bucket policy
automatically once a second key exists is the remaining work.
