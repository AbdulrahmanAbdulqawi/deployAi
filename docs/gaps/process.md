# Process and git hygiene

**Status:** one closed, one open.

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
