# DeployHub — UI/UX Plan

## 1. Design philosophy

DeployHub is built for people who don't know what a terminal is. Every decision favors clarity over power. A user should always be able to answer three questions at a glance: what's happening, did it work, and what do I do next.

Three principles guide the whole interface:

1. **One obvious action per screen.** There's always a single primary thing to do. On the dashboard it's deploy; in the wizard it's continue; on the deploy screen it's watch. Secondary actions stay visually quieter.
2. **Status is never ambiguous.** Every project and every deployment shows a plain-language state with a consistent color and icon. Green means live, blue means working, red means failed. No jargon, no raw exit codes.
3. **The user is never blocked or lost.** Actions that take time (deploys) run in the background with live feedback. Errors explain what to do next, not what went wrong internally.

## 2. Design language

### Visual tone
Flat, calm, and uncluttered. White and near-white surfaces, hairline borders, generous whitespace. No gradients, heavy shadows, or decoration. The interface should feel like a well-made native app, not a busy developer console.

### Color and meaning
Color carries status, not decoration. A restrained neutral palette for structure, with four semantic states used consistently everywhere:

| State | Meaning | Used for |
|-------|---------|----------|
| Green (success) | Live / succeeded | Completed deploys, healthy projects |
| Blue (accent) | In progress | Active deploys, primary actions |
| Red (danger) | Failed | Failed deploys, invalid tokens |
| Amber (warning) | Needs attention | Partial deploys, expiring credentials |

### Typography
A single clean sans-serif for everything except logs, which use a monospace face so build output lines up the way users expect from a terminal — but framed in a friendly container.

### Density
Comfortable, not compact. This is a consumer product, so touch targets are generous and text is readable. Lists use bordered cards rather than dense table rows.

## 3. Information architecture

```
Login (GitHub)
│
└── App shell (nav: Dashboard, Settings)
    ├── Dashboard              → project list, each with status + deploy
    │   └── New project wizard → repo → branch → providers → confirm
    ├── Project detail         → overview, targets, deploy button
    │   ├── Live deploy view   → per-provider status + live logs
    │   └── History            → past deployments, replayable logs
    └── Settings
        ├── Providers          → add/validate/remove provider tokens
        ├── Notifications      → email preferences
        └── Account            → GitHub connection, sign out
```

The navigation is intentionally shallow. A user is at most three clicks from any screen, and the two things they do most — deploy and check status — are on the first screen after login.

## 4. Key screens

### 4.1 Login
A single screen with one action: continue with GitHub. No email/password form, no choices. A short line of copy explains what DeployHub does. The moment they authorize, they land on the dashboard.

### 4.2 Dashboard
The home base. A list of the user's projects, each shown as a card with:
- Project name and a plain-language status badge (Live, Deploying, Failed).
- The GitHub repo, target providers, and time since last deploy in a quiet metadata line.
- Two actions: Deploy and History.

A prominent New project button sits top-right. When the user has no projects yet, the list is replaced by an empty state that invites them to create their first one.

### 4.3 New project wizard
A short, guided flow that never shows more than one decision at a time:

1. **Choose a repository** — a searchable list of the user's GitHub repos. No URLs to type.
2. **Choose a branch** — defaulted to the repo's default branch, changeable.
3. **Choose providers** — pick one or more from the providers they've connected. If they haven't connected any, this step guides them to add one first.
4. **Confirm** — a plain summary: this repo, this branch, these providers. One button to create.

Each step has a clear back path. Progress is shown so the user knows how far they are. Provider-specific settings (build command, root directory) are hidden behind an optional "advanced" disclosure so non-technical users never see them unless they go looking.

### 4.4 Live deploy view
The signature screen. When a deploy starts, the user sees:
- A header with the project, branch, and elapsed time.
- One status card per provider, each showing its own state, timing, a progress indicator while running, and the live URL plus a success badge when done.
- A live log panel streaming build output in monospace, with timestamps, updating in real time.

Because each provider is tracked independently, the user can watch Vercel finish while Railway is still building. If one fails, only that card turns red; the other continues.

### 4.5 History
A reverse-chronological list of past deployments for a project. Each entry shows branch, overall status, duration, timestamp, and which providers were involved. Opening an entry replays its stored logs exactly as they streamed, so a user can review what happened on any past deploy.

### 4.6 Settings — providers
Where users connect hosting providers. Each connected provider is a row showing its name, an optional label, and whether its token is currently valid. Adding one opens a small form: pick the provider, paste the token, and the system validates it against the provider before saving. Clear guidance links explain where to find each provider's token.

### 4.7 Settings — notifications and account
Simple preference toggles for email on success and failure, and an account section to manage the GitHub connection and sign out.

## 5. Core interaction patterns

### Status badges
One badge component, used everywhere a state appears, with a fixed mapping of state to color, icon, and word. A user learns the vocabulary once and it holds across the whole app.

### Background actions with live feedback
Deploys never block the UI. Triggering one immediately moves the user into the live view where progress streams in. They can navigate away and the deploy keeps running; the dashboard reflects its state.

### Optimistic, reversible flows
Creating a project, adding a provider, and editing settings all confirm instantly and are easy to undo or edit. Destructive actions (deleting a project, removing a provider that's in use) ask for confirmation and explain the consequence in plain language.

### Empty states as invitations
Every list that can be empty — projects, providers, history — has a purposeful empty state that names the space and offers the action to fill it, rather than showing a blank screen.

## 6. Voice and copy

The interface speaks plainly, warmly, and in the second person. It refers to the user's things as "your projects," confirms with short past-tense messages ("Saved," "Deploying"), and never surfaces raw errors.

| Instead of | Write |
|------------|-------|
| "Deployment initiated successfully" | "Deploying now" |
| "Error: ECONNREFUSED at provider endpoint" | "Couldn't reach Vercel. Check your token in settings" |
| "No resources found" | "Create your first project to get started" |
| "Invalid credentials" | "Your Railway token is no longer valid. Reconnect it" |

Buttons are verbs: Deploy, Create project, Connect Vercel, Reconnect. Headings and labels use sentence case with no trailing punctuation.

## 7. Feedback and states

Every screen accounts for four states, not just the happy one:

- **Loading** — skeletons or subtle spinners while data arrives, never a frozen screen.
- **Empty** — an inviting prompt with the next action.
- **Error** — a plain-language message with a way forward.
- **Success** — clear confirmation, often just the updated state itself.

For deploys specifically, the states map to the live view: queued, building, deploying, live, or failed — each with its own badge and, where relevant, a URL or a retry.

## 8. Responsiveness and accessibility

- **Responsive** — the layout works from phone to desktop. Cards stack on narrow screens; the live log panel remains readable on mobile.
- **Keyboard** — every action is reachable and operable by keyboard, with visible focus rings.
- **Contrast** — text and status colors meet WCAG AA against their backgrounds.
- **Screen readers** — status changes announce themselves, so a deploy completing is perceivable without watching the screen.
- **Reduced motion** — progress animations respect the user's reduced-motion preference.

## 9. Onboarding

A first-time user is guided, not dumped into an empty app:

1. After connecting GitHub, if they have no providers connected, a gentle prompt points them to add one — with links on where to get a token.
2. Once a provider is connected, the dashboard's empty state invites them to create their first project.
3. The wizard carries them through repo, branch, and provider without assuming prior knowledge.
4. Their first deploy lands them on the live view so the payoff — watching it go live — is immediate.

The goal: from first login to first successful deploy in under ten minutes, without reading documentation.

## 10. Component inventory

The interface is built from a small, reused set of components, which keeps it consistent and quick to build:

- App shell with navigation
- Project card (used on the dashboard)
- Status badge (used everywhere state appears)
- Provider status card (used on the live deploy view)
- Live log panel
- Wizard step container with progress
- Repo picker and branch picker
- Provider credential row and add-provider form
- Confirmation dialog
- Empty state block
- Toast / inline confirmation

Building these once and composing screens from them ensures the whole product feels like one thing, and makes adding a new provider a matter of data — it flows into the existing provider picker and status cards with no new UI work.

## 11. Design-to-build sequence

The build order mirrors the implementation phases so design is ready just ahead of engineering:

1. App shell, login, and the status-badge vocabulary.
2. Dashboard and project card.
3. New project wizard with repo/branch/provider pickers.
4. Live deploy view and log panel — the centerpiece.
5. History and log replay.
6. Settings screens for providers and notifications.
7. Empty states, error states, and the onboarding polish pass.
