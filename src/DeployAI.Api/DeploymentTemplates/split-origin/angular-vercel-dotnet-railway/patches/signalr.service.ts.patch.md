# Patch: signalr.service.ts (absolute hub URL in production)

## When to apply
- Gap indicates SignalR still uses relative `/hubs/*` without `environment.apiBaseUrl`.

## Instructions
1. Read the existing `signalr.service.ts`.
2. In production, build the hub URL from `environment.apiBaseUrl`:
   - `` `${environment.apiBaseUrl}/hubs/<hubName>` ``
3. Development may keep relative paths or empty `apiBaseUrl` fallback.
4. Return the complete updated file.

## Example

```typescript
const hubUrl = environment.production && environment.apiBaseUrl
  ? `${environment.apiBaseUrl.replace(/\/$/, '')}/hubs/notifications`
  : '/hubs/notifications';
```
