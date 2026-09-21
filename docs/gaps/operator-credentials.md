# Operator credentials

**Status:** open. DeployAI delivers "nothing pasted" for the credentials its *users* bring, and
not at all for the credentials DeployAI itself runs on.

## The rule is delivered outward and not inward

"Never require a credential to be typed or pasted" is a standing rule, and for user-facing
credentials it is now real: GitHub, Railway and Vercel access arrives through OAuth, and the
Porkbun DNS work closed its own gap by making approval the offered path — requested, approved,
retrieved, validated and stored, nothing pasted.

None of that applies to the credentials DeployAI needs in order to *be* DeployAI. Those live in
`appsettings.Development.json` — gitignored, plaintext, hand-edited — and there is no flow that
acquires them, no check that they are still valid, and no path that rotates them. The rule is
satisfied at the product's edge and unenforced at its centre.

Two kinds sit in that file and they do not have the same fix:

- **OAuth *registration* secrets** (`GitHub__ClientSecret`, `Railway__ClientSecret`,
  `Vercel__ClientSecret`). It is tempting to say "DeployAI already runs OAuth, so rotation could
  be a re-auth" — that is wrong, and worth writing down so it is not proposed again. The OAuth
  flow exchanges these for *user* tokens; the client secret itself is issued by the provider's
  developer dashboard and identifies the application, not a user. No flow DeployAI runs can ever
  mint one. The reachable fix is to read them from a secret store rather than a file, not to
  acquire them interactively.
- **Plain API keys** (`Anthropic__ApiKey`). No OAuth exists at all. Today this is a paste, and
  the only question is which store it is pasted into, once.

## Nothing notices a credential is wrong

`GitHubOptions`, `RailwayOptions` and `VercelOptions` are plain classes whose secrets default to
`string.Empty`, and `Program.cs` binds them with no `ValidateOnStart` and no data annotations.
A missing, blank, expired or revoked secret therefore fails at first use, inside whichever
feature happened to need it, rather than at boot where it would be one obvious line.

The consequence is that an operator cannot answer "which of my credentials are stale?" without
exercising every provider by hand. There is no inventory and no health answer — which is the
same shape as a gap this project already closed for deployed apps: the hourly sweep now asks
whether required settings are still set and records the answer per check. DeployAI asks that
question about the apps it deploys and not about itself.

**The pattern to copy already exists.** `StorageController` recognises a rejected key and says
so in the one language that helps — "they may have been rotated" — instead of surfacing a raw
401. That is credential-staleness detection, already written, for exactly one credential.

## Worked example — this session

A routine config change (moving a local Postgres connection string out of the tracked
`appsettings.json` and into the gitignored override, where the project's own documented
mechanism says it belongs) required reading the override file to see whether a
`ConnectionStrings` key already existed. Reading it disclosed four live secrets into a chat
transcript: a GitHub App client secret, both halves of a Railway OAuth client, and a working
Anthropic API key. By the standing rule all four are now compromised and must be rotated, and
rotating them means visiting three provider dashboards and hand-editing a file — the exact
motion the rule exists to abolish.

Two things are worth separating here. The disclosure itself was avoidable: `grep -n
ConnectionStrings` answers the question without printing anything else, and a whole-file read
was the wrong instrument. But the reason a careless read was *expensive* is structural — an
unencrypted file that co-locates every provider secret means any read of it, by a person or an
agent, is a full compromise rather than a partial one. Guidance to "be careful what you cat" is
a design smell by this project's own standard.

## A read-only provider token validates green, then fails at the first write

Found live on 2026-09-22, on the first real use of the new Coolify-project flow. A Coolify API
token pasted into Settings → Connections was accepted, the card said **CONNECTED**, and the
instance name appeared. The first deploy then stopped at Coolify's own
`403 Missing required permissions: write`, on `POST /projects`.

`CoolifyProvider.ValidateCredentialsAsync` calls `GET /health` and `GET /applications`. Both are
reads, so a read-only token passes both. Every write DeployAI needs — create the project, create
the application, `PATCH /envs/bulk`, `POST /deploy` — is untested until the user is several
minutes into a deploy they believed was configured.

The connect form *warns about exactly this case* in prose ("a read-only token connects fine and
then fails at the first deploy"). The warning was right and still did not help: a sentence asking
the user to get the permission right is the design smell CLAUDE.md names, where a check would do.

**The reflected fix:** prove write access at connect time — Coolify's token-permission endpoint
if one exists, otherwise a harmless authenticated write that is immediately undone — and label
the connection *read-only* rather than *connected* when it cannot. The same question applies to
every provider: nothing anywhere checks that a stored credential can do what it will be asked to
do, only that it can authenticate.

**The shape to watch for:** a rule enforced at the product boundary and assumed at the
operator boundary. The people running DeployAI are technical, so their pasting feels acceptable
in a way a user's would not — which is precisely how it stays unexamined. The reflected fixes,
roughly in order of cost: bind the options with `ValidateOnStart` so a blank or malformed secret
fails at boot; give the credentials a health answer the way deployed apps have one; and move the
store off plaintext-on-disk so that reading one secret is not reading all of them.
