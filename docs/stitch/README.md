# DeployAI Stitch Design Project

**Stitch project:** [DeployAI Frontend Redesign Handoff](https://stitch.withgoogle.com/projects/17064168259508941640)

**Design system:** Kinetic Glass (dark-first, glassmorphism, indigo→cyan gradients)

## Screens (8 total)

| Stitch screen | Angular route | Reference HTML |
|---|---|---|
| Login - DeployAI | `/login` | `screens/login-deployai.html` |
| Apps Dashboard - DeployAI | `/dashboard` | `screens/apps-dashboard-deployai.html` |
| Add New App - DeployAI | `/projects/new` | `screens/add-new-app-deployai.html` |
| Live Deployment - DeployAI | `/projects/:id/deploy/:deploymentId` | `screens/live-deployment-deployai.html` |
| Settings Connections - DeployAI | `/settings/connections` | `screens/settings-connections-deployai.html` |
| Project Detail - DeployAI | `/projects/:id` | `screens/project-detail-my-saas-app-deployai.html` |
| Edit App - DeployAI | `/projects/:id/edit` | `screens/edit-app-deployai.html` |
| Deployment History - DeployAI | `/projects/:id/history` | `screens/deployment-history-deployai.html` |

## Regenerating / adding screens

Use the Stitch MCP server (`stitch` in `.cursor/mcp.json`) with project ID `17064168259508941640` and design system `assets/d4bd7287c477497b9d107603292de87b`.

```powershell
# Example: generate a new screen
Invoke-RestMethod -Uri "https://stitch.googleapis.com/mcp" -Method POST `
  -Headers @{"Content-Type"="application/json"; "X-Goog-Api-Key"="YOUR_KEY"} `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"generate_screen_from_text","arguments":{"projectId":"17064168259508941640","prompt":"...","deviceType":"DESKTOP","designSystem":"assets/d4bd7287c477497b9d107603292de87b"}}}'
```
