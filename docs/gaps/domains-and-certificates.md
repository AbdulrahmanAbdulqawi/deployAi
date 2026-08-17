# Domains and certificates — a name a user owns, proven before a certificate is asked for

**Status:** as of 2026-08-17, the lifecycle exists end to end and is covered by tests, and buying a
domain now works — exercised against Porkbun's sandbox through the UI, from search to a written A
record. Everything past the DNS check remains unproven against a real domain, and the gaps below
are open.

## The problem, stated once

Every app DeployAI deployed came up on `http://{uuid}.{server-ip}.sslip.io` — plain HTTP, no
certificate, unreadable, and derived only when the Coolify instance happened to be addressed by a
raw IPv4. The product's promise is that a non-technical user never touches a terminal or a
provider dashboard, and yet the only thing it could say about domains was, in its own UI copy:
*"Point this name at your server first."*

This is not a hypothetical. Four separate things were wrong at once:

| # | What silently did nothing | Because |
|---|---|---|
| 1 | A custom domain typed into the wizard | `CreateProviderProjectRequest.CustomDomain` was passed at create time and never persisted; `DeployTargetConfig` stored `DomainServiceName` but not the domain |
| 2 | Domain assignment for a compose app | `CoolifyProvider.Management.cs:292` skipped it when `isCompose`, correctly — Coolify rejects `docker_compose_domains` before the first deploy — but nothing came back afterwards |
| 3 | The post-deploy retry that should have | `DeploymentOrchestrator.cs:847` hard-coded `domain: null`, because there was nothing persisted to pass |
| 4 | The wizard's domain field itself | Shown **only** on single-origin compose plans — the exact case guaranteed to discard it |

The result: the one place the product asked for a domain was the one place it could not keep one,
and the user got sslip.io with no error.

## The rule the design rests on

**An `https://` FQDN is never written to a provider until DNS resolves to the server.**

Coolify/Traefik begin an ACME HTTP-01 challenge the moment an `https://` FQDN is attached. Against
a domain that does not resolve there yet, three things go wrong together: one of Let's Encrypt's
five failed validations per hour is spent, Traefik installs its own self-signed fallback
certificate, and nothing reports a problem because the deploy itself succeeded.

This nearly shipped as a regression. Fixing the persistence bug alone would have sent every
user-typed domain through `CoolifyProvider.NormalizeUrl`, which upgrades any scheme-less value to
`https://` — converting a silently-ignored domain into an ungated certificate request. The
persistence fix therefore ships with the scheme stated explicitly (`http://`) and the upgrade to
`https://` gated behind `DomainTransitions.MayRequestCertificate`.

Two corollaries, both learned the same way:

- **Records DeployAI writes are DNS-only, never proxied.** A Cloudflare-proxied record resolves to
  Cloudflare's own addresses, so HTTP-01 reaches the CDN and 404s at the origin. When DeployAI
  *finds* a proxied record it says so specifically — `DnsCheckResult.IsProxiedByCdn` — because
  "this does not point at your server" would send the user to change an address that is correct.
- **A domain change reaches the proxy one deploy late.** Deploy N attaches it, deploy N+1 routes
  it. `Assigned` and `CertificatePending` are separate states with a deploy between them for this
  reason, and the certificate probe never runs before that deploy.

## Absence has to say which absence it is

Four terminal states, in two pairs, and the split within each pair is the point:

| Conclusive | Inconclusive |
|---|---|
| `DnsFailed` — checked, and the record is wrong | `DnsUnverifiable` — no resolver ever answered |
| `CertificateFailed` — the proxy is serving its own fallback | `CertificateUnverifiable` — the handshake never completed |

`ProjectDomain.LastConclusiveStatus` is stored separately from the most recent check precisely so
a run of inconclusive checks cannot terminate as a failure. NXDOMAIN from a resolver *is* an
answer and is conclusive; a timeout is not. The UI carries the distinction to the pixel — a fourth
tone, `unknown`, that is neither green nor red.

This is also why `DnsClient.NET` was necessary rather than `System.Net.Dns`, which cannot target a
specific resolver (Coolify validates against 1.1.1.1, and disagreeing with it green-lights an
assignment Coolify then refuses), cannot distinguish NXDOMAIN from a timeout, cannot see that a
name is a CNAME where Coolify requires an A, and has no concept of CAA — the one record that makes
issuance fail every single time.

## What was built

| Piece | Where |
|---|---|
| `DnsCheckResult`, `CertificateInspection`, `DomainTransitions`, `DomainNameRules`, `PlatformSubdomain` | `src/DeployAI.Core/Domains/` — pure, no I/O |
| `DnsClientResolver`, `SslStreamCertificateInspector` | `src/DeployAI.Infrastructure/Dns/` |
| `CloudflareDnsProvider` | `src/DeployAI.Providers/Cloudflare/` |
| `IServerAddressProvider`, `IApplicationDomainAssignment` | `src/DeployAI.Core/Providers/`, implemented on `CoolifyProvider` |
| `ProjectDomain` + `AddProjectDomains` migration | `src/DeployAI.Data/` |
| `DomainService`, `DomainReconciliationJob`, `DomainsController` | `src/DeployAI.Api/` |
| `domain-panel` | `client/src/app/project/domains/` |

Two details worth knowing before touching it:

- **`SslStream`, not `HttpClient`.** The default HTTP path throws on exactly the certificate worth
  observing. It also sets `TargetHost` explicitly, because Traefik picks its router by SNI —
  connecting without it returns the default certificate for every domain.
- **`DomainReconciliationJob` carries `[AutomaticRetry(Attempts = 0)]`, and that is load-bearing.**
  Hangfire's default of ten retries on its own ladder, against a job that already reschedules
  itself, produces two ladders, two clocks, and duplicate provider writes.

## Three things only running it found

All three were invisible to a green suite, and each was caught within minutes of using the page.

**Coolify reports its localhost server's `ip` as `host.docker.internal`.** `TryGetServerAddressAsync`
read the field and trusted it, so the panel told the user to point an A record at a Docker
hostname — and the DNS check then compared real resolved addresses against a string no record
could ever match, meaning a perfectly configured domain would have waited out its deadline and
been reported as their mistake. The address is now validated as a public IPv4, falling back to the
instance URL's host, and null when neither qualifies. The original test asserted the `ip` field was
*read*; it never asked whether the value was usable.

**Nothing in this API configures enum-as-string, so a response enum crosses the wire as an
integer.** The TypeScript enums compare against names, so every comparison missed and the panel
rendered its default branch: a domain waiting on DNS was labelled *"Removed"*, with its setup
instructions hidden because the `source` comparison missed too. `DomainStatus` and `DomainSource`
now carry `[JsonConverter(typeof(JsonStringEnumConverter))]`. **This applies to any enum added to a
response DTO, not just these** — worth promoting into the code conventions.

**The DNS record was written once or never.** `TryWriteRecordAsync` was reachable only from
`ResolveTargetAddressAsync`, which runs only in `Pending`. A registrar's zone listing does not
include a registration that completed a second earlier, so buying a domain through the UI — where
purchase and attach are seconds apart — found no covering zone, gave up permanently, and left the
domain waiting out a sixty-minute deadline for a record DeployAI was supposed to write, then
reported it as the user's failure to point their domain. The race is the *normal* case on the
buy-a-domain path, not an unlucky one, and 1143 passing tests did not see it. The write is now
retried every `DnsPending` tick until it lands, and the deadline drops to the ten-minute managed
one when it does. Fixing it recovered a domain that was already stuck, which is the standard: a fix
that only reaches resources created after it leaves the reporter still broken.

Found alongside it: with two DNS accounts connected, the account that got the write was whichever
the database returned first, ordered by nothing. Candidates are now gathered across all accounts
and the most specific zone wins, with provider and id breaking genuine ties so the choice is at
least reproducible. Two accounts claiming the same zone is logged, because only one of them can be
the one actually answering queries.

## What remains

- **A domain DeployAI sold is not recorded as one it sold.** Buying works now, but
  `DomainSource.Registrar` is still assigned nowhere in production code — only inside a test mock.
  `AttachAsync` marks a just-bought domain `UserProvided`, and `TryWriteRecordAsync` then overwrites
  it to `ManagedZone`. So disconnecting hands it back as though the user had brought it, and nothing
  can distinguish "they own this elsewhere" from "we sold it to them and owe them a renewal."
- **A stored DNS connection that holds no zones renders blank.** A valid credential on an empty
  account can now be stored, which is what makes buying the first domain possible — but the panel
  shows a header and empty space. The message explaining *why* it is empty is written at connect
  time and never persisted, so the state that most needs an explanation is the one with none.
- **Two DNS connections can be labelled identically.** Both default to `Default`, so the settings
  page shows two `Remove Default` buttons with nothing to tell them apart. Removing the wrong one
  hands a live domain's DNS back to the user by accident. Only reachable since an empty account
  became storable, i.e. since there could be two.
- **The approval flow has no UI.** `POST /api/dns/authorizations` and its poll endpoint exist and
  are tested, and `client/src` contains no reference to either. Pasting keys is still the only path
  the product offers, which is the thing that flow was built to remove.
- **Nothing re-checks a domain that is `Active`.** Traefik renews on its own, but nothing notices
  when a renewal fails, and nothing notices if the DNS record is later changed or deleted.
  `ProjectHealthMonitorJob` already runs hourly and is the obvious home.
- **The platform wildcard serves one server.** `AppOptions.PlatformDomain` points a single
  wildcard A record at a single address; a second deployment server needs per-app records through
  a connected DNS account instead.
- **`DeploymentEndpointProbes.CheckReachableAsync` still misreports a TLS failure.** It catches
  every exception and suggests `redeploy_server`, which cannot fix a certificate that never
  issued. The certificate check belongs there as its own probe with its own remediation.
- **No real domain has been all the way through.** Exercised on 2026-08-16 against the live
  instance up to `DnsPending`: the migration applied, the panel rendered, the server address was
  read from Coolify, the A-record instruction showed the right IP, and the gate held — Coolify was
  never touched for a domain that did not resolve. What is still unproven is everything past that
  point: assignment, the routing deploy, and issuance. Only a padlock proves those.

## Non-goals

- **Not a DNS host.** DeployAI writes records into an account the user already owns and can revoke.
- **Not a certificate authority.** Traefik issues and renews; DeployAI's job is to gate the request
  on DNS and then confirm what was actually served.
- **Not wildcard domains.** `DomainNameRules` refuses them: HTTP-01 cannot validate a wildcard, so
  accepting one would mean accepting a domain whose certificate could never issue.
