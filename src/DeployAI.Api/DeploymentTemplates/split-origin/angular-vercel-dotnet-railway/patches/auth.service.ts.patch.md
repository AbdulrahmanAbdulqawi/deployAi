# Patch: auth.service.ts (auth path + credentials)

## When to apply
- Gap indicates auth client uses wrong path (`/api/Auth`) or missing `withCredentials`.

## Instructions
1. Read the existing `auth.service.ts`.
2. Replace auth base paths:
   - `/api/Auth` → `/api/v1/auth`
   - `/api/auth` → `/api/v1/auth`
3. Ensure login, refresh, and logout HTTP calls use `{ withCredentials: true }`.
4. Return the complete updated file.

## Path examples

```typescript
// Before
this.http.post('/api/Auth/login', body);

// After
this.http.post('/api/v1/auth/login', body, { withCredentials: true });
```
