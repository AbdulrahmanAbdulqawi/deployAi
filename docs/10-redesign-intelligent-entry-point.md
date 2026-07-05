# DeployHub — Redesign: The Intelligent Entry Point

A fundamental reframing. DeployHub stops being a dashboard where you pick providers and becomes the single place code goes to become live — a system that decides where your code should run and puts it there.

## 1. The shift in one sentence

**Before:** "Connect your providers, pick which ones, click deploy."
**After:** "Point us at your code. We'll figure out where it should live and make it live."

The old model is a multi-provider control panel. The new model is a deployment *intelligence* — an opinionated layer that removes the decisions a non-technical user can't make anyway.

## 2. Why the old model quietly fails its own promise

The original DeployHub still asked the user to answer questions they don't understand:

- "Which provider do you want — Vercel or Railway?" A non-technical founder has no basis to answer this. They don't know that Vercel is for frontends and Railway is for backends. Asking them to choose is asking them to have the exact knowledge the product claims they don't need.
- "Paste your Vercel API token." This sentence alone ends the "non-technical" promise. They have to leave, create a provider account, find the token page, generate a token, understand scopes, and come back. That's not one-click; that's a technical onboarding wearing a friendly coat.
- "Configure your deploy target." Build commands, root directories, environment variables — even hidden behind "advanced," their existence means the happy path still assumes the user could need them.

Each of these is a small crack in the core promise. The redesign seals them by making the product *decide* instead of *ask*.

## 3. The three pillars of the redesign

### Pillar 1 — Provider selection becomes automatic
The user never picks Vercel vs Railway. DeployHub inspects the repository and decides.

### Pillar 2 — One deploy action, routed intelligently
"Deploy" doesn't mean "deploy to the providers I picked." It means "put this code where it belongs" — potentially splitting a monorepo across providers automatically.

### Pillar 3 — Credentials become infrastructure, not a feature
Ideally the user never brings a provider token. DeployHub holds the infrastructure relationships so the user just brings code. This is the biggest leap and the latest-stage move.

Each pillar raises the trust stakes, because each one moves a decision from the user to DeployHub. That's the whole trade: less burden on the user, more responsibility on us.

## 4. Pillar 1 — Automatic provider selection

### The core idea
When a user connects a repository, DeployHub reads it and classifies what it is, then picks the right home for it. No provider question is ever asked on the happy path.

### What the inspection looks for

The classifier examines the repo for signals and maps them to a deployment shape:

| Signal in the repo | What it implies | Where it should live |
|--------------------|-----------------|----------------------|
| Only static files / `index.html` / built assets | Static site | Static/edge host (Vercel, Netlify) |
| Next.js, Nuxt, SvelteKit, Astro config | Frontend framework, possibly SSR | Frontend-optimized host (Vercel) |
| `package.json` with a server (Express, Fastify, Nest) | Long-running Node backend | Container host (Railway, Render) |
| `Dockerfile` present | Containerized app, custom runtime | Container host (Railway, Render, Fly) |
| .NET / Python / Go / Rust project files | Backend service | Container host (Railway, Render) |
| Reference to a database (Prisma schema, connection string env, ORM config) | Needs a database | Host with managed DB (Railway, Render) |
| Both a frontend app and a server folder in one repo | Monorepo, full-stack | Split: frontend + backend to different hosts |
| Background worker / cron / queue code | Needs always-on compute | Container host, not serverless |

### The output: a deployment plan
The inspection doesn't just pick one provider — it produces a **plan**: "This is a full-stack app. The web part goes to Vercel, the API and database go to Railway." The plan is what the user sees and confirms, in plain language, before anything happens.

### One confident default, not a menu
The user is shown the plan as a decision already made, phrased plainly:

> "Looks like a Next.js app with an API and a database. We'll put your site on fast global hosting and your API and database on a server that keeps them running. Sound good?"

One primary button: **Yes, deploy it.** A quiet secondary link: **Change how this deploys** (the advanced override, for the small minority who know what they want). The default is confident. The override exists but is never in the way.

### Handling uncertainty honestly
Sometimes the repo is ambiguous. The redesign's rule: when confidence is high, decide silently; when confidence is low, ask one plain question rather than guessing wrong.

- High confidence ("this is clearly a static site"): just state the plan.
- Low confidence ("this could be a static export or a server-rendered app"): ask one human question — "Does your app need to run code on a server, or is it just pages? — with plain descriptions, not framework names.

Never guess wrong silently. A wrong silent guess is the fastest way to destroy trust in an opinionated product.

## 5. Pillar 2 — One deploy action, routed intelligently

### From "deploy to my providers" to "deploy where it belongs"
In the old model, "Deploy now" pushed to whatever the user had picked. In the redesign, "Deploy now" executes the plan — which may mean deploying different parts of one repository to different providers, in the right order, automatically.

### Monorepo splitting
The signature capability. A single repo containing a web app and an API becomes two coordinated deployments:

- The frontend is built and sent to the frontend host.
- The API (and its database) is built and sent to the backend host.
- The two are wired together — the frontend is told where the API lives (the API's URL is injected into the frontend's environment automatically).

The user did nothing except click deploy. They never learned the word "monorepo." They never copied an API URL into a config. DeployHub did the coordination that a developer would otherwise do by hand.

### Ordering and dependencies
Intelligent routing also means correct sequencing. If the frontend needs to know the backend's URL, the backend deploys first, its URL is captured, and the frontend build receives it. The user sees "Setting up your API… now connecting your site to it…" in plain language — the orchestration is visible as reassurance, not as configuration.

### The deploy becomes a single outcome
Even when three things happen across two providers, the user experiences one deploy with one status: getting ready → going live → live. The multi-provider reality is invisible unless they look. Success is one message: "Everything's live," with the links.

## 6. Pillar 3 — Credentials become infrastructure

### The biggest gap between "feels technical" and "actually one-click"
The single most technical moment in the original product is "go get your Vercel token." No matter how friendly the surrounding UI, that step requires the user to understand and operate a provider account. To be truly one-click for non-technical people, that step has to disappear.

### The vision: the user brings code, not accounts
In the fully realized product, DeployHub holds the infrastructure relationships. The user connects their GitHub and clicks deploy. Behind the scenes, their app is provisioned onto hosting that DeployHub manages. The user may never create a Vercel or Railway account at all — the hosting is just "where DeployHub put my app."

### Two ways to get there (a spectrum, not a switch)

**Model A — Slicker bring-your-own with OAuth.** Instead of pasting tokens, the user connects each provider with a single OAuth click (like connecting GitHub), and DeployHub manages the relationship after that. This keeps the user's own provider accounts but removes the token-hunting. Achievable earlier; a meaningful UX jump.

**Model B — DeployHub as the umbrella account.** DeployHub holds real infrastructure relationships with the providers and provisions apps under its own umbrella. The user has no direct provider relationship — they just have DeployHub. This is the true single entry point. It's a bigger, later-stage move that turns DeployHub from a convenience layer into an infrastructure business.

The redesign treats these as stages: start by removing token-hunting (Model A), grow toward holding infrastructure (Model B) once trust, scale, and reliability justify it.

### What Model B changes about the business
Holding infrastructure relationships means:
- DeployHub buys hosting wholesale and provisions it for users — a margin business, not just a subscription.
- Billing can be unified — the user pays DeployHub, not four separate providers.
- The user's mental model becomes "my app lives on DeployHub," which is the strongest possible position — but also the heaviest responsibility.

## 7. The stakes: trust and reliability become the product

### You are now the thing between "I wrote code" and "it's live"
In the old model, if Vercel had an outage, the user understood it was Vercel's fault — DeployHub just showed the logs. In the redesign, DeployHub *chose* Vercel, *routed* the code there, and possibly *owns* the relationship. Every failure is now DeployHub's failure in the user's eyes. That's the price of removing decisions from the user: you inherit responsibility for them.

### Three new categories of risk the redesign creates

**Bad routing decisions.** If the classifier sends a backend to a static host, or misses that an app needs a database, the deploy fails or the app breaks — and it's DeployHub's judgment that was wrong, not the user's. The inspection has to be genuinely good, and honest about uncertainty.

**Outages you can't blame away.** When the underlying provider goes down, the user doesn't see the provider — they see DeployHub. Status transparency (a real status page, honest incident communication) stops being a nice-to-have and becomes essential to survival.

**Owning the whole path.** Once credentials are infrastructure (Model B), you're responsible for provisioning, uptime, billing, and the relationship with the provider. A problem anywhere in that chain is yours.

### Why this is worth the risk
Because it's the only version of the product that fully delivers the original promise. "One entry point for non-technical people to deploy" is *only* true if the user doesn't pick providers, doesn't manage tokens, and doesn't coordinate multi-part deploys. The redesign is what it actually takes to be the thing the product always claimed to be. The stakes are higher because the value is higher.

### The reliability bargain
The redesign only works if DeployHub is demonstrably reliable. That means:
- The routing intelligence is accurate and cautious, asking rather than guessing wrong.
- The status of every provider is monitored and shown honestly.
- Failures are communicated immediately and owned, never hidden.
- There's always a way back (undo/restore) when a decision or deploy goes wrong.

Trust is the entire product now. Every reliability investment is a product investment.

## 8. How the user experience changes end to end

### Old flow
1. Sign in with GitHub.
2. Go to settings, add a Vercel token (leave the app, generate it, come back).
3. Add a Railway token (same again).
4. Create a project, pick the repo and branch.
5. Choose which providers to deploy to.
6. Configure each target.
7. Click deploy.

Seven steps, two of which require leaving the product and operating provider accounts.

### Redesigned flow
1. Sign in with GitHub.
2. Pick the repo.
3. DeployHub inspects it and shows a plain-language plan.
4. Click "Yes, deploy it."

Four steps, none of which require understanding providers, tokens, or configuration. The two most technical steps of the old flow (getting tokens, picking providers) are gone — absorbed into DeployHub's intelligence and infrastructure.

## 9. What has to be true for this to work

The redesign depends on capabilities the original didn't need:

- **A genuinely good repository classifier** — the accuracy of the whole product now rests on inspecting code and deciding correctly. This is the new core competency.
- **Automatic wiring between split deployments** — injecting the API URL into the frontend, sequencing deploys, managing shared environment values, all without user involvement.
- **Provider relationships beyond a stored token** — OAuth connections (Model A) or true infrastructure agreements (Model B).
- **Reliability infrastructure** — monitoring, status, incident response, and rollback as first-class, always-on systems.

The original DeployHub was a well-designed integration layer. The redesign is an opinionated infrastructure product. It's a bigger build and a bigger business.

## 10. A staged path from here to there

The redesign doesn't have to ship all at once. A sane sequence:

1. **Add inspection on top of the existing model.** Keep bring-your-own tokens, but stop asking users to pick providers — inspect the repo and recommend a confident default with an override. This delivers Pillar 1 immediately with the infrastructure you already planned.
2. **Add intelligent routing and monorepo splitting.** Make "deploy" execute a multi-provider plan with automatic wiring. This delivers Pillar 2.
3. **Replace token-pasting with OAuth connections (Model A).** Remove the most technical step without yet holding infrastructure. A major UX leap.
4. **Move toward umbrella infrastructure (Model B).** Once trust and scale justify it, hold the provider relationships and become the true single entry point — and a margin business.
5. **Harden reliability at every stage.** Status, incident communication, and undo grow in importance with each step, because each step makes more of the outcome your responsibility.

Each stage is shippable and valuable on its own, and each one moves the product closer to the real promise: the user brings code, and it becomes live — nothing else asked of them.

## 11. The redesigned positioning

**Old:** "Deploy to multiple providers from one dashboard."

**New:** "Point us at your code. We'll put it live in the right place. You don't need to know where."

The old pitch sells control over providers. The new pitch sells freedom from having to think about providers at all. For a non-technical audience, the second is dramatically more compelling — it's the difference between a tool for people who already understand deployment and a tool for people who just want their thing to be live.
