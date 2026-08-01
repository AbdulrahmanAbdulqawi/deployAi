---
name: curate-project-knowledge
description: End-of-session pass to externalize what was learned before it's lost to a conversation compaction — update CLAUDE.md's Known Gaps, docs/gaps/, docs/00-README.md's index, and the memory system. Use when wrapping up a substantial session with real findings, or when explicitly asked to curate/update CLAUDE.md or the docs.
---

# Curating project knowledge

This is the mechanism, not a one-off task. `CLAUDE.md`'s Known Gaps section and this project's
memory both exist to survive a conversation ending or being compacted — but only if something
actually writes to them before that happens. Run this checklist after a session with real
findings; skip it after a trivial one (a typo fix doesn't need a memory entry).

## 1. Review what actually happened this session

What was fixed (with commit SHAs), what was found but not fixed, what was investigated and
ruled out. Be specific — "looked into the storage layer" is not reviewable later; "confirmed
Coolify's logs API always returns the first container, per its own controller source" is.

## 2. Update `CLAUDE.md`'s Known Gaps + `docs/gaps/`

- **Closed a gap?** Mark its one-liner in `CLAUDE.md` with `~~strikethrough~~` and a short
  "closed" note (see existing entries for the pattern), and update the matching section in its
  `docs/gaps/*.md` file with what changed and how it was verified — don't delete the historical
  narrative, extend it the way `docs/gaps/compose-deployments.md`'s closed entries do.
- **Found a new gap?** Add one line to `CLAUDE.md` under the right theme (or a new theme
  heading if none fits), and write the full narrative in the matching `docs/gaps/*.md` file (or
  a new one) following the shape in `docs/12-repository-scanning.md`: problem, prior state,
  current status, worked example. If it doesn't fit an existing doc, add a row to
  `docs/gaps/README.md`'s index too.
- **Neither closed nor new, just re-confirmed?** Don't touch it — re-stating an open gap that
  didn't change this session is noise, not curation.

## 3. Check `docs/00-README.md`'s index

If any `docs/*.md` file was added, removed, or renamed this session, add/fix its row. This file
goes stale silently — it was already out of date before this skill existed, which is exactly
the failure mode being closed here.

## 4. Write memory entries — only for what belongs there

Follow the `user`/`feedback`/`project`/`reference` type definitions and exclusions already
defined for this session (code patterns, git history, and anything derivable by reading the
repo do **not** belong in memory). Ask, for each candidate fact:

- Is it about **how the user works or wants to be worked with** (a correction, or a judgment
  call they confirmed without pushback)? → `feedback`.
- Is it a **fact about an external system** (a provider API's real behavior, confirmed against
  its source or docs) that would otherwise need re-deriving? → `reference`.
- Is it **durable project context** (not "what's in flight right now," which decays too fast to
  be worth writing) that shapes how future work should be approached? → `project`.
- Is it about **who the user is**, stable across sessions? → `user`.

Check for an existing memory file to update before writing a new one — don't duplicate.

## 5. Sanity-check before finishing

Re-read `CLAUDE.md` top to bottom: does every gap that existed before this pass still show up
somewhere, either inline or as a one-liner + link? Nothing should be silently dropped in the
course of "cleaning up." If a `docs/gaps/*.md` file was touched, does it still read standalone —
could someone with no session context follow problem → status without asking a question?
