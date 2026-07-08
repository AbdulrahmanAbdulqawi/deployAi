# Patch: Program.cs (CORS for Vercel + Railway split-origin)

## When to apply
- Gap indicates CORS is missing or does not allow Vercel preview origins.

## Instructions
1. Read the existing `Program.cs`.
2. If CORS with `AllowedOrigins` and `*.vercel.app` is already present, make no changes.
3. Otherwise insert this block **before** `var app = builder.Build();`:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
                origin.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase) ||
                builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()?
                    .Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase)) == true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
```

4. Ensure `app.UseCors()` is called after `var app = builder.Build();`.
5. Return the complete updated `Program.cs`.

## Greenfield fallback (only if Program.cs does not exist)

Generate a minimal `Program.cs` with controllers, CORS, forwarded headers, and `app.Run()`.
