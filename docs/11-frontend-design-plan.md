# DeployHub — Frontend Design Plan

The concrete blueprint for building the Angular 18 frontend: architecture, component system, state, styling, routing, and how the intelligent-entry-point experience maps to real components. This assumes the redesign (automatic provider selection, one intelligent deploy action) and the non-technical standardization guide.

## 1. Goals for the frontend

The frontend has one job above all others: make deploying feel like nothing. A non-technical user should move from "here's my code" to "it's live" without ever feeling they touched a developer tool. Every architectural choice serves that.

Concretely, the frontend must be:

- **Calm and non-technical** — the standardization guide is the law; no jargon, one clear action per screen, plain-language status everywhere.
- **Real-time** — deploys stream live; the UI reflects state as it changes without refreshes.
- **Fast-feeling** — optimistic updates, instant transitions, cached data, so it never feels like it's waiting.
- **Consistent** — one small component set, reused everywhere, so the whole product feels like one thing.
- **Accessible and responsive** — works for everyone, on any device, including deploying from a phone.

## 2. Technology choices

| Concern | Choice | Why |
|---------|--------|-----|
| Framework | Angular 18 (standalone components) | Team's stack; standalone drops NgModule boilerplate |
| Reactivity | Angular signals + RxJS | Signals for local/derived state, RxJS for streams (SignalR, HTTP) |
| Routing | Angular Router with lazy-loaded routes | Fast first load; each area loads on demand |
| Styling | Custom design tokens + lightweight utility layer | Full control of the calm, consumer look; no heavy UI library fighting us |
| Real-time | SignalR client | Matches the .NET backend hub for live logs and status |
| HTTP | Angular HttpClient + typed API layer | Typed contracts mirror the API spec |
| State | Signal stores (service-based) | Simple, no heavy state library needed at this scale |
| Forms | Reactive forms where needed (few) | The redesign minimizes forms; used only where real input exists |
| Testing | Vitest/Jest + Testing Library + Playwright | Unit, component, and end-to-end coverage |

The deliberate decision: no large component library (Material, PrimeNG). The product's whole value is a calm, distinctive, non-technical feel, and a generic library fights that. A small custom component set on top of our own tokens gives the control that makes it feel like a real product rather than a themed admin panel.

## 3. Design token foundation

Everything visual resolves to tokens, so the calm look is consistent and dark mode is free. Tokens are defined once in CSS custom properties and consumed everywhere.

### Token categories

- **Surfaces** — page background, card, raised card, popover. A short elevation scale, nothing more.
- **Text** — primary, secondary, muted, plus the four status text colors.
- **Status roles** — success (live), accent (working), danger (failed), warning (needs attention). These four carry all meaning; they're never decorative.
- **Borders** — a hairline default and one stronger step for hover/emphasis.
- **Typography** — one sans family for everything, one mono family reserved solely for the live activity panel.
- **Spacing and radius** — a comfortable (not compact) density scale; 12px card corners.
- **Motion** — a few durations and easing curves for calm, consistent transitions.

### Rules baked into tokens

- Two font weights only (regular and medium); nothing heavier, to stay calm against content.
- Sentence case everywhere, enforced by convention and lint.
- Color encodes status, never decoration.
- Light and dark mode both derive from the same tokens; no hardcoded colors anywhere in components.

## 4. Application architecture

### Shell and routing

A thin app shell holds the top navigation (Dashboard, Settings) and the authenticated user's menu. Everything else is a lazy-loaded feature area under the shell.

```
/                    → redirect to /dashboard (or /login if unauthenticated)
/login               → login (GitHub)
/dashboard           → project list
/projects/new        → new project flow (repo pick → plan → confirm)
/projects/:id        → project detail
/projects/:id/deploy/:deploymentId → live deploy view
/projects/:id/history → history + log replay
/settings            → settings shell
  /settings/connections   → provider/GitHub connections
  /settings/notifications → notification preferences
  /settings/account       → account
```

Each top-level route is its own lazy chunk. The live deploy view and history load only when entered, keeping the initial bundle small.

### Feature-folder structure

```
src/app/
├── core/                  # cross-cutting singletons
│   ├── api/               # typed API client, one file per resource
│   ├── realtime/          # SignalR connection + typed hub events
│   ├── auth/              # auth state, guard, interceptor
│   ├── stores/            # signal stores (projects, deployments, session)
│   └── models/            # TypeScript types mirroring API contracts
├── shared/                # reusable presentational components
│   ├── status-badge/
│   ├── project-card/
│   ├── provider-status-card/
│   ├── live-log-panel/
│   ├── deploy-plan/
│   ├── empty-state/
│   ├── confirm-dialog/
│   └── ui/                # buttons, inputs, toasts — the primitives
├── features/
│   ├── login/
│   ├── dashboard/
│   ├── new-project/
│   ├── project-detail/
│   ├── live-deploy/
│   ├── history/
│   └── settings/
└── styles/                # tokens, base, utilities
```

The rule: `features` compose `shared` components and read from `core` stores. `shared` components are presentational — they take inputs and emit outputs, holding no business logic. `core` holds all state and I/O. This keeps screens thin and components reusable.

## 5. State management

State lives in a small set of signal-based stores in `core/stores`, each a service exposing readonly signals and methods. No heavy state library; at this scale signals are enough and keep things legible.

### The stores

- **SessionStore** — the authenticated user, auth status, notification preferences. Populated on login, cleared on logout.
- **ProjectsStore** — the user's projects and their latest-deployment summaries. Backs the dashboard. Loads from cache first, refreshes in the background.
- **DeploymentStore** — the currently viewed deployment: its per-provider target states and streaming logs. Fed by both the API (initial load, history) and SignalR (live updates).
- **ConnectionsStore** — provider and GitHub connections shown in settings.

### The pattern

Each store exposes readonly signals for state and derived signals for computed views (e.g. a project's overall status derived from its targets). Components read signals directly in templates; Angular's change detection updates them efficiently. Writes go through store methods that call the API layer and update signals, so components never touch HTTP directly.

### Real-time into state

The realtime layer subscribes to SignalR hub events and routes them into the DeploymentStore. A `LogLine` event appends to the log signal; a `StatusChanged` event updates the relevant target's status; a `DeploymentCompleted` event flips the overall status and triggers the success moment. Because these feed signals, every subscribed view updates automatically — the live deploy screen, the dashboard card, and any status badge all reflect the change at once.

## 6. The component system

A small, deliberate set of components composes the entire product. Building these once, well, is what makes it feel unified and makes new providers require zero new UI.

### Primitives (`shared/ui`)

- **Button** — one component, a clear hierarchy (primary, secondary, quiet/text). Exactly one primary per screen, enforced by usage.
- **Input / Select** — pre-styled, used sparingly since the redesign minimizes forms.
- **Toast** — transient confirmations ("Saved," "Deploying").
- **Icon** — thin wrapper over the icon set, sized and colored by tokens.

### Domain components (`shared`)

- **StatusBadge** — the single most important component. Takes a status, renders the fixed word + color + icon from the status vocabulary. Used on cards, in the deploy view, in history — everywhere state appears. One source of truth for what a status looks like.
- **ProjectCard** — a project's name, plain-language status (via StatusBadge), quiet metadata line, and the primary action. Composes the dashboard.
- **DeployPlan** — the redesign's centerpiece. Takes the inspection result and renders the plain-language plan ("your site goes here, your API and database go there, connected automatically") with the confident primary action and the quiet override link.
- **ProviderStatusCard** — one card per destination in the live view: its plain-language state, timing, progress while running, and the live link on success. Independent per provider so one can succeed while another runs.
- **LiveLogPanel** — the framed, friendly activity panel. Monospace lives only here, inside a calm card with plain-language status above it. Collapsible and secondary, never a raw console.
- **EmptyState** — the invitation shown when a list is empty; names the space and offers the action.
- **ConfirmDialog** — plain-language confirmation for the few destructive actions, always explaining the consequence.

### Why this set is enough

Every screen is a composition of these. Adding a provider adds data, not UI: a new destination simply flows into DeployPlan and appears as another ProviderStatusCard. The extensibility goal holds at the frontend layer precisely because no component knows about specific providers — they all render generic plan and status data.

## 7. Screen-by-screen build

### Login
The `LoginComponent`: one line of copy, one button that starts GitHub OAuth, a friendly illustration. No form, no choices. On success, the auth flow populates SessionStore and routes to the dashboard.

### Dashboard
The `DashboardComponent` reads ProjectsStore and renders a list of `ProjectCard`s, with a prominent "New project" primary action. When the store is empty, it renders `EmptyState` instead. Cards show live status because they're bound to signals the realtime layer updates — a deploy running anywhere is visible here without a refresh.

### New project (the redesigned flow)
A short guided flow, one decision per step:

1. `RepoPickerComponent` — a searchable list of the user's GitHub repos (from the API). No URLs typed.
2. On selection, the frontend requests an inspection and shows a loading moment ("Looking at your project…").
3. `DeployPlanComponent` renders the returned plan as a confident default. The primary action deploys; the quiet override opens the advanced path for the few who want it.

The whole flow is four taps to a first deploy, and never asks a provider or token question on the happy path.

### Live deploy view
The `LiveDeployComponent` reads DeploymentStore. It renders a header (project, branch, elapsed time), one `ProviderStatusCard` per destination, and the `LiveLogPanel`. It joins the SignalR group for the deployment on enter and leaves on exit. As events stream in, cards and logs update independently. On completion it shows the success moment — the live links, a light celebration — or, on failure, the plain-language failure pattern with a clear next step and the quiet "see details" disclosure.

### History
The `HistoryComponent` lists past deployments in plain language (when, whether it worked, where). Opening one replays its stored logs through the same `LiveLogPanel`, so the review experience matches the live one.

### Settings
`ConnectionsComponent` lists provider and GitHub connections with plain validity states; adding one uses OAuth where available (the redesign's Model A) rather than token pasting. `NotificationsComponent` is a couple of plain toggles. `AccountComponent` manages the GitHub connection and sign-out.

## 8. Interaction and feedback standards

These apply everywhere, enforced as shared behavior rather than re-decided per screen:

- **Optimistic actions** — clicking deploy immediately shows "getting ready" and routes to the live view; the UI doesn't wait for the server round-trip to feel responsive.
- **Four states per view** — every data-bound view handles loading (skeletons), empty (invitation), error (plain-language + next step), and success. No view ships with only the happy path.
- **Live reassurance** — long actions always show motion and plain status text; the user never wonders if it froze.
- **Plain-language failures** — errors never surface raw text; the failure component names it gently, reassures, and offers one action, with details tucked behind an optional disclosure.
- **Celebration on success** — the first deploy especially gets a light, brief celebratory moment and the clickable live link.

## 9. Accessibility

Accessibility is built in from the first component, not retrofitted:

- Every action is keyboard-reachable with visible focus rings.
- Status changes announce to screen readers, so a deploy completing is perceivable without watching — live regions wrap the status area.
- Color never carries meaning alone; every status pairs a word and an icon with its color.
- Contrast meets WCAG AA in both light and dark mode, guaranteed by the token choices.
- Motion respects the reduced-motion preference; the celebration and progress animations soften or disable accordingly.
- Touch targets are generous (consumer density), helping motor accessibility and mobile alike.

## 10. Responsive and mobile

The layout works from phone to desktop with the same components. Cards stack on narrow screens; the live log panel stays readable at phone width; buttons are thumb-sized. The dashboard on mobile strips to essentials — project name, status, deploy — with everything else one tap away. Because a user might deploy from their phone, the live deploy view is treated as a first-class mobile screen, not a shrunk desktop one.

## 11. Performance

- **Lazy routes** keep the initial bundle to the shell, login, and dashboard.
- **Cache-first data** — stores hydrate from cached data instantly, then refresh in the background, so screens appear populated immediately.
- **Optimistic UI** hides latency on the actions that matter most.
- **Instant log panel** — the live panel shows "connecting…" the moment it opens, so it feels fast even when the provider is slow.
- **Signals** give fine-grained, efficient updates, so streaming logs don't thrash change detection.
- **On-demand realtime** — the SignalR group is joined only while viewing a deploy, and left on exit, avoiding needless connections.

## 12. Theming and dark mode

Light and dark mode both derive entirely from tokens; no component hardcodes a color. Dark mode is treated as intentional, not inverted — the calm, safe feeling is preserved, status colors stay distinct and readable, and the accent is tuned for the dark surface. The mode switch is a single attribute at the root; everything else follows.

## 13. Testing strategy

- **Unit** — store logic and the API/realtime layers, with HTTP and the hub mocked.
- **Component** — each shared component in isolation across its states (a StatusBadge for every status; a ProviderStatusCard running, succeeded, failed; DeployPlan for single-target and split-monorepo plans).
- **End-to-end** — the core journey with Playwright: login → pick repo → see plan → deploy → watch live → see it live, plus the failure path and the undo/restore path.
- **Accessibility** — automated checks in CI (axe) plus keyboard-only and screen-reader passes on the core flow.

## 14. Build sequence

The frontend is built in the order that lets the backend integrate against it phase by phase, matching the implementation plan:

1. Tokens, base styles, and the `ui` primitives — the visual foundation.
2. App shell, routing, login, and the StatusBadge vocabulary.
3. Dashboard and ProjectCard against ProjectsStore.
4. New-project flow with RepoPicker and the DeployPlan component (the redesign's signature screen).
5. Live deploy view with ProviderStatusCards and LiveLogPanel, wired to the realtime layer — the centerpiece.
6. History and log replay.
7. Settings (connections via OAuth, notifications, account).
8. The polish pass: empty states, failure states, celebration moments, onboarding, and the full accessibility sweep.

Each step produces something usable and testable on its own, and the component set means later steps mostly compose what earlier steps built rather than introducing new UI.

## 15. How the frontend upholds the two big promises

**Non-technical feel.** The standardization guide is enforced structurally: one StatusBadge owns the status vocabulary, one failure pattern owns errors, monospace is confined to one collapsible panel, and forms barely exist because the redesign decides instead of asking. A screen can't drift technical because the components that would make it technical don't exist.

**Extensibility.** No frontend component knows about a specific provider. Plans and statuses are generic data rendered by DeployPlan and ProviderStatusCard. Adding Render or Fly.io is a backend change the frontend picks up automatically — the provider simply appears in a plan and as another status card, with no new screens, no new components, and no edits to existing ones.
