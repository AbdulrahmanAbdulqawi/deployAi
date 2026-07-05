# DeployHub — Non-Technical UI/UX Standardization Guide

A single reference that keeps DeployHub feeling like a friendly consumer app, not a developer tool. Every designer and developer follows these standards so the product stays approachable no matter who builds which screen.

## 1. The core goal

The user should never feel they've wandered into something built for programmers. DeployHub should feel closer to booking a flight, sharing a photo, or sending money than to operating a console. If a screen would make a non-technical person hesitate, pause, or feel dumb, it fails this standard — regardless of how correct it is.

The mental model to design toward: **a calm assistant that handles the technical parts for you and tells you plainly how it's going.**

## 2. The five standardization pillars

Everything below organizes under five pillars. When in doubt, check a decision against these.

1. **Plain language** — no jargon reaches the user, ever.
2. **One clear action** — each screen has a single obvious thing to do.
3. **Reassuring feedback** — the app always says what's happening and what's next, kindly.
4. **Friendly visuals** — soft, warm, spacious; nothing that reads as a terminal.
5. **Invisible complexity** — technical machinery is hidden by default, reachable only if sought.

## 3. Pillar 1 — Plain language

### The vocabulary standard

A fixed translation table is the law of the product. These words never appear in the interface; their plain equivalents always do.

| Never show | Always show |
|------------|-------------|
| Deployment | Publish, go live |
| Deploy (verb) | Publish, put it live |
| Repository / repo | Your project, your app |
| Branch | Version |
| Provider / host | Where it lives, hosting |
| Build | Getting it ready |
| Build succeeded | It's ready |
| Build failed | Something went wrong getting it ready |
| API token / key | Connection, access key |
| Environment variables | Settings, secrets |
| Rollback | Undo, restore previous version |
| Commit | Change, update |
| Endpoint / URL | Web address, link |
| Log / stdout | Activity, what's happening |
| Queue / pending | Getting started |
| CI/CD | (never referenced at all) |

### Writing rules

- **Second person, present tense.** "Your app is going live," not "Deployment in progress."
- **Short sentences.** One idea per sentence. If a sentence needs a comma to survive, split it.
- **No acronyms** unless they're household words (URL is borderline — prefer "web address").
- **Describe outcomes, not mechanisms.** The user cares that it worked, not how. "It's live" beats "Build artifact deployed to edge."
- **Numbers stay human.** "About 2 minutes," not "127s." "Just now," not a raw timestamp.

### The read-aloud test

Before shipping any copy, read it aloud to someone non-technical. If they ask "what does that mean?", it fails. Rewrite until they nod.

## 4. Pillar 2 — One clear action

### The single-primary rule

Every screen has exactly one primary button — visually the strongest thing on the page. Everything else is quieter (outlined or text-only). The user should be able to squint at any screen and instantly see the one thing to do.

- Dashboard: the primary action is publish (or create your first project when empty).
- Wizard step: the primary action is continue.
- Live view: there is no competing action — the user just watches; cancel is quiet.
- Settings: save is primary; everything else is secondary.

### Progressive disclosure

Never present more than one decision at once to a non-technical user. Multi-part tasks become guided steps, not dense forms. A five-field form becomes five friendly questions where that reduces intimidation.

### No dead ends

Every screen offers a forward path. Errors include a next step. Empty states include the action that fills them. The user is never left staring at something with no obvious move.

## 5. Pillar 3 — Reassuring feedback

### Status vocabulary standard

One fixed set of states, used identically everywhere. A user learns these five once and understands the whole product.

| State | Word shown | Color | Icon | Feeling |
|-------|-----------|-------|------|---------|
| Working | Getting ready | Blue | spinner/refresh | calm, in progress |
| Live | Live | Green | check | success, done |
| Failed | Didn't go through | Red | alert | clear, non-scary |
| Needs attention | Needs a quick fix | Amber | info | gentle nudge |
| Idle | Ready to publish | Neutral | dot | at rest |

Never invent new status words per screen. Never show a raw state like `EXIT_1` or `502`.

### The tone of failure

Failures are the highest-risk moment for a non-technical user — this is where a product feels technical and scary. Standard for every error:

1. **Name it plainly and gently.** "The deploy didn't go through."
2. **Reassure where honest.** "This usually clears up on its own."
3. **Give one clear next step.** A "Try again" button, or "Get help."
4. **Never show the raw error.** Stack traces, exit codes, and provider error strings are logged internally, never surfaced. An optional "See details" disclosure can hold them for the curious, collapsed by default.

### Progress that feels alive

Long actions (publishing) always show live movement — a progress indicator, streaming activity, elapsed time. The user must never wonder whether the app froze. Even when precise progress is unknown, show motion and reassuring status text ("Getting your app ready…").

## 6. Pillar 4 — Friendly visuals

### Aesthetic standard

- **Soft and spacious.** Generous whitespace, rounded corners (12px on cards), hairline borders. Nothing cramped or dense.
- **Warm neutrals** for structure, with the four status colors used only for meaning.
- **Friendly shapes.** Circular status icons, pill badges, rounded cards. Avoid sharp, boxy, terminal-like rectangles.
- **Calm motion.** Gentle transitions, no aggressive flashing. Respect reduced-motion.

### The anti-terminal rule

The single biggest "this is technical" signal is monospace text on a dark background — a terminal. Standards:

- **Monospace is reserved for one place only:** the optional live activity panel during publishing, and even there it's framed in a friendly card with plain-language status above it, never a raw black console.
- **Never** use monospace for labels, statuses, headings, or body copy.
- **Never** show a blinking cursor, command prompt (`$`), or terminal chrome anywhere in the main flow.
- The activity log is opt-in — collapsed or secondary — so a non-technical user never has to look at it to succeed.

### Icons and imagery

- Icons are simple, rounded, and paired with words — never icon-only for anything important.
- Use warmth: a friendly illustration on empty states and the welcome screen does more to de-technicalize than any copy.
- Avoid developer iconography (brackets, terminals, gears-as-primary) as decoration.

### Consistency standard

Every instance of the same thing looks identical everywhere: one status badge component, one card style, one button hierarchy, one empty-state pattern. Visual consistency is what makes a product feel like one calm thing rather than a collection of screens.

## 7. Pillar 5 — Invisible complexity

### Hide by default, reveal on request

Everything a non-technical user doesn't need is hidden behind optional disclosures labeled in plain language ("Advanced settings," "See details"). The default path shows only what's essential.

Hidden by default: build commands, root directories, environment variables, raw logs, provider-specific configuration, deployment IDs, timing internals.

Shown by default: what you're publishing, where it's going, whether it worked, and the link to see it live.

### Smart defaults

The app makes good choices so the user doesn't have to. The default version is pre-selected. Sensible build settings are inferred. The user confirms rather than configures.

### Handle the technical parts silently

When something technical must happen (validating a connection, detecting the app type, choosing a build method), the app does it quietly and reports only the human-meaningful outcome. "Connected to Vercel" — not "Token validated, scopes verified, project list fetched."

## 8. Screen-level standards

Applying the pillars to each screen type:

### Welcome / login
One warm sentence about what the app does, one button. A friendly illustration. Zero jargon, zero choices.

### Dashboard
Projects as friendly cards with a plain status word and a publish button. Warm empty state when there's nothing yet. The word "deployment" never appears — it's "your apps" and "publish."

### Publish wizard
Guided questions, one at a time: which app, which version, where should it live, confirm. Advanced settings collapsed. Each step reassures and offers a back path.

### Live publish view
Plain-language status per destination ("Getting ready," "Live"), gentle progress, elapsed time in human units. The activity log is present but secondary and collapsible. Success ends with the live link, celebrated lightly.

### History
"Your past updates," each showing when, whether it worked, and where — in plain words. No commit hashes, no durations in raw seconds.

### Connections (settings)
"Where your apps can live." Adding one is "Connect Vercel," with plain guidance on finding the access key. Validation happens silently; the user sees "Connected" or a gentle fix-it message.

## 9. Copy standards library

Reusable, approved phrasings. Use these verbatim where they fit.

| Situation | Standard copy |
|-----------|--------------|
| Starting to publish | "Getting your app ready…" |
| Publish in progress | "Publishing to Vercel…" |
| Publish done | "You're live" |
| Publish failed | "The publish didn't go through. It's worth trying again." |
| Connection invalid | "Your Vercel connection needs refreshing. Reconnect it to keep publishing." |
| Empty projects | "Add your first app to get started." |
| Empty history | "Your updates will show up here once you publish." |
| Confirm delete | "Remove this app from DeployHub? This won't delete anything on GitHub or your hosting." |
| Success link | "Your app is live at [link]." |
| Loading | "One moment…" |

Every piece of copy: sentence case, no exclamation marks on system messages, no "please," no "successfully," no first person ("I").

## 10. The de-technicalization checklist

Before any screen ships, it passes this checklist. If any answer is no, it isn't ready.

- [ ] Would a non-technical friend understand every word on this screen?
- [ ] Is there exactly one obvious primary action?
- [ ] Is every status shown as a plain word with a consistent color and icon?
- [ ] If something fails here, is the message gentle, jargon-free, and paired with a next step?
- [ ] Is all monospace/terminal content either absent or hidden behind an optional disclosure?
- [ ] Are technical settings hidden by default with smart defaults chosen for the user?
- [ ] Does long-running work show live, reassuring progress?
- [ ] Does the screen feel warm and spacious rather than dense and boxy?
- [ ] Is there always a forward path — no dead ends?
- [ ] Read aloud to a non-technical person: did they ever ask "what does that mean?"

## 11. Governance

- This guide is the source of truth. Any new screen or component conforms to it before merge.
- The vocabulary table and status vocabulary are fixed. Adding a new user-facing term requires updating this guide first, so the whole product stays consistent.
- When a technical concept genuinely can't be avoided, the standard is to wrap it in a plain-language explanation and hide the detail — never to expose it raw.
- The checklist in section 10 is part of the definition of done for every UI task.
