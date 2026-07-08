# Patch: angular.json (production fileReplacements)

## When to apply
- Gap indicates missing production `fileReplacements` for `environment.production.ts`.

## Instructions
1. Read the existing `angular.json` from the repository.
2. Locate `projects.<name>.architect.build.configurations.production`.
3. Add `fileReplacements` if missing:

```json
"fileReplacements": [
  {
    "replace": "src/environments/environment.ts",
    "with": "src/environments/environment.production.ts"
  }
]
```

4. Preserve all other production keys (optimization, budgets, etc.).
5. Return the complete updated `angular.json`.

## Snippet to inject inside `"production": {`

```json
"fileReplacements": [
  {
    "replace": "src/environments/environment.ts",
    "with": "src/environments/environment.production.ts"
  }
]
```
