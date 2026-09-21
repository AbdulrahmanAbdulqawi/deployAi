# DeployAI MCP Server

Model Context Protocol server that exposes DeployAI deployment orchestration, GitHub intelligence, verification, and Claude agent workflows to any MCP client (Cursor, Claude Desktop, custom agents).

## Prerequisites

- Node.js 18+
- Running [DeployAI API](https://github.com/) (default `http://localhost:5000`)
- Valid DeployAI session (JWT access + refresh tokens)

## Install

```bash
cd mcp/deployai-mcp
npm install
npm run build
```

## Cursor configuration

Add to `.cursor/mcp.json` (do not commit tokens):

```json
{
  "mcpServers": {
    "deployai": {
      "command": "node",
      "args": ["mcp/deployai-mcp/dist/index.js"],
      "env": {
        "DEPLOYAI_API_URL": "http://localhost:5000",
        "DEPLOYAI_ACCESS_TOKEN": "<your-access-token>",
        "DEPLOYAI_REFRESH_TOKEN": "<your-refresh-token>"
      }
    }
  }
}
```

Restart Cursor after saving.

## Authentication

### Option 1: Environment variables

Copy tokens from the DeployAI web app after GitHub login:

- `deployai_access_token` → `DEPLOYAI_ACCESS_TOKEN`
- `deployai_refresh_token` → `DEPLOYAI_REFRESH_TOKEN`

### Option 2: `mcp_auth` tool

1. Call `mcp_auth` without arguments — opens GitHub login in browser
2. After redirect, copy the full URL from the address bar
3. Call `mcp_auth` with `callback_url` set to that URL

Tokens are cached at `~/.deployai/mcp-tokens.json`.

## Available tools

### Core

| Tool | Description |
|------|-------------|
| `health_check` | API connectivity (no auth) |
| `mcp_auth` | Authenticate via tokens or browser |
| `list_projects` | List user projects |
| `get_project` | Project details + targets |
| `list_deployments` | Paginated deployment history |
| `get_deployment` | Status + failure analysis |
| `trigger_deployment` | Start a deploy |
| `get_deployment_logs` | Build/runtime logs |
| `verify_deployment` | Post-deploy checks |

### GitHub intelligence

| Tool | Description |
|------|-------------|
| `list_github_repos` | User's GitHub repos |
| `get_deployment_plan` | Auto-classify repo |
| `scan_deployment_readiness` | Missing deployment files |

### AI agents

| Tool | Description |
|------|-------------|
| `generate_deployment_setup` | Claude/template setup (may take minutes) |
| `merge_deployment_setup` | Merge setup PR |
| `generate_deployment_fix` | Claude build fix |
| `generate_verification_fix` | Claude verification fix |
| `merge_deployment_fix` | Merge fix PR |

### Credentials

| Tool | Description |
|------|-------------|
| `list_credentials` | Stored provider credentials |
| `list_provider_projects` | Projects under a credential |

## Development

```bash
npm run dev    # watch TypeScript
npm test       # unit tests
npm start      # run stdio server
```

## Architecture

This package is a **thin adapter** over the DeployAI REST API. All business logic lives in `DeployAI.Api` — the MCP server only translates tool calls to HTTP requests and NDJSON streams.

See [docs/mcp/README.md](../../docs/mcp/README.md) for full architecture documentation.
