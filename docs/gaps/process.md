# Process and git hygiene

**Status:** one closed, two open.

## Generated commit messages were generic — closed, and the cause was worse than the symptom

Dockerfile generation produced several commits sharing one message because it produced several
commits *that changed nothing*. Both paths meant to skip an unchanged file; neither did.
`ContentMatches` base64-decoded its argument, but `GetFileMetadataAsync` already decodes — so it
decoded plain text, threw `FormatException`, swallowed it, and answered "different" every single
time. The server path did not call it at all. Four deploys of one app in one afternoon appended
four empty commits to a user's own history. Messages now distinguish Add from Update and name
the website build separately, but the real fix is that an unchanged file produces no commit.

**The shape to watch for:** a `catch` whose fallback is the unsafe answer is invisible. It
cannot fail loudly, it cannot be seen in a log, and the only evidence is junk arriving somewhere
nobody is looking — here, someone else's git history.

## Nothing requires a change to arrive with tests

See `docs/gaps/verification-and-config-checks.md` — recorded there since it is one entry, kept
here only as a pointer so a reader searching "process" or "tests" finds it either way.

## The top-level docs describe a product that no longer exists

`README.md` presents DeployAI as a Vercel + Railway platform. It does not contain the word
Coolify or Hetzner once — and Coolify is now the provider most of the deployment pipeline is
written around (`CoolifyComposePlanner`, three of the four `DeploymentPlanKind` values, the
whole `single-origin-compose` template family), while Hetzner object storage is provisioned
automatically on every server deploy. `docs/00-README.md` still closes with "Status: Planning
phase. This documentation set defines the target before implementation begins," under a table
that indexes docs describing shipped behaviour. `docs/mcp/README.md` documents an MCP server at
`mcp/deployai-mcp/`, a path `.gitignore` excludes — so the architecture is described in a repo
that does not contain the thing described.

None of this is wrong in a way that breaks a build, which is exactly why it survives. The cost
lands on whoever reads the README first and builds a wrong model of the system: a contributor
who assumes two providers, or an agent that scans `README.md` for the stack and never learns
that a compose deploy is the interesting case. The `curate-project-knowledge` skill already
covers `docs/00-README.md`'s index rows when a file is added or renamed; it does not cover a
file whose *contents* went stale in place, and neither does anything else.

**The shape to watch for:** documentation whose accuracy nothing depends on. A stale
`schema.graphql` fails CI; a stale README fails nothing, so it drifts until someone is misled
by it. The reflected fix is not "remember to update the README" — by this project's own
standard that is a design smell — it is to make the stack description derive from something
that moves with the code (the registered providers, the plan kinds), or to delete the
duplicated description and let one place own it.
