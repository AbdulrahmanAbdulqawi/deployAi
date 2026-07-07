# DeployAI Design System & Screen Brief

## Product

DeployAI is a deployment control panel that connects a GitHub repo to Vercel (frontend) and Railway (backend/API), uses AI to infer how to deploy, and gives users a single place to publish, monitor live builds, and manage hosting.

**Tagline direction:** "Put your app live in one calm place"

## Brand

- **Name:** DeployAI
- **Font:** Inter (400, 500) — already in production
- **Palette:** Indigo → blue → cyan gradients on neutral surfaces
- **Themes:** Light and dark (user toggle in header)
- **Provider accents:** Vercel black/white, Railway purple (#853bce light / #c084fc dark)
- **Tone:** Calm, confident, developer-friendly — reduce deploy anxiety

## Layout

### App shell (authenticated)
- Top bar: wordmark → Dashboard | nav: Apps, Settings | theme toggle | Sign out
- No sidebar; desktop-first today, needs mobile breakpoints
- Settings uses horizontal sub-nav: Connections | Notifications | Account

### Public (unauthenticated)
- Login only — centered card, no app shell

## Screens to design (priority order)

### P1 — Login `/login`
- GitHub OAuth only
- Current: bare card with title + "GitHub" button
- Target: hero experience with tagline, provider branding, "Continue with GitHub"
- States: default, loading (OAuth redirect)

### P1 — Dashboard `/dashboard`
- Title "Apps" + "Add app" CTA
- Project cards: name, repo summary, status badge, Publish + overflow (Open, History, Delete)
- Empty state: "No apps yet"
- Loading: skeleton cards
- Error: retry card
- Delete confirm: destructive modal (warns Railway + Vercel cleanup)

### P1 — Add app wizard `/projects/new`
- 5 steps (3 for AI fast-path): Repository → Branch → What to deploy → Hosting → Review
- Step progress bar
- Step 3 highlights AI deploy plan moment ("Yes, deploy it" vs manual override)
- Folder pickers, Website/Server/Both toggles
- Vercel "Your site" + Railway "Your API" connection blocks

### P1 — Live publish `/projects/:id/deploy/:deploymentId`
- Real-time deploy progress — primary "moment of truth"
- Phase heading, branch/commit meta, live status badge
- Success: celebration + live URL links
- In-progress: per-provider cards with expandable terminal logs
- Partial failure: restore previous version + redeploy

### P2 — Project detail `/projects/:id`
- Publish + header menu (Edit, History)
- Readiness banner for split-origin setup
- Collapsible Advanced: service cards (env vars, restart), database cards (Postgres/Redis)
- Densest screen — needs clear hierarchy

### P2 — Edit app `/projects/:id/edit`
- Name, branch, folder paths, build summary, Save/Cancel, danger zone delete

### P2 — History `/projects/:id/history`
- Deploy list + replay panel with provider cards and logs

### P3 — Settings
- **Connections:** Vercel + Railway provider cards, OAuth, manual token panel
- **Notifications:** email toggles (success/failure); push/Slack coming soon
- **Account:** GitHub session card, sign out

## Shared components

- Buttons: primary / secondary / quiet; with icons and loading
- Status badges: success, failed, in_progress, idle, warning
- Empty states, confirm dialogs, toast notifications
- Provider status cards (expandable logs)
- Terminal-style live log panel (GitHub-dark inspired)

## Design tokens (existing)

```
Brand gradient: indigo #4f46e5 → blue #2563eb → cyan #0891b2
Surface page light: #f8fafc | dark: #000
Surface card light: #fff | dark: #111
Control height: 36px
Radius: 6px / 10px / 14px
Spacing: 4px grid (4, 8, 12, 16, 20, 24, 32, 40)
```

## User journeys

1. **First-time:** Login → empty dashboard → Add app wizard → Live publish → success links
2. **Returning:** Dashboard → Publish → Live publish
3. **Ops:** Project detail → Advanced OR History → replay/redeploy
4. **Setup:** Settings → Connections → OAuth Vercel + Railway

## Constraints (preserve in redesign)

- Route structure and screen count (API coupling)
- OAuth return flows with query params
- Terminal log readability
- Destructive delete copy mentions Railway + Vercel

## Known gaps

- No logo (text wordmark only)
- Mixed native vs custom form controls
- No mobile layout
- No marketing landing page
- Notification prefs client-side only
