# Patch: app.config.ts (apiBaseInterceptor)

## When to apply
- Gap indicates `apiBaseInterceptor` is not registered in `provideHttpClient(withInterceptors([...]))`.

## Instructions
1. Read the existing `app.config.ts`.
2. Add imports if missing:
   - `provideHttpClient, withInterceptors` from `@angular/common/http`
   - `apiBaseInterceptor` from `./core/interceptors/api-base.interceptor` (adjust path to match repo layout)
3. Register `apiBaseInterceptor` in the `withInterceptors([...])` array alongside any existing interceptors.
4. Return the complete updated file.

## Example result

```typescript
import { ApplicationConfig } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { apiBaseInterceptor } from './core/interceptors/api-base.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideHttpClient(withInterceptors([apiBaseInterceptor]))
  ]
};
```
