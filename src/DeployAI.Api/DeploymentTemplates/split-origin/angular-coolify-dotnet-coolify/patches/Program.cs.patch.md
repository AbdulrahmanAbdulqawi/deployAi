# Patch: Program.cs (CORS for Coolify full-stack)

## When to apply
- Gap indicates CORS is missing or does not allow the Coolify website origin.

## Instructions
1. Read the existing `Program.cs`.
2. If CORS with `AllowedOrigins` and `App__FrontendUrl` / `FRONTEND_URL` is already present, make no changes.
3. Otherwise insert this block **before** `var app = builder.Build();`:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            var configuredOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>();
            if (configuredOrigins?.Any(allowed =>
                    string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase)) == true)
            {
                return true;
            }

            var frontendUrl = builder.Configuration["App:FrontendUrl"]
                ?? builder.Configuration["FRONTEND_URL"]
                ?? Environment.GetEnvironmentVariable("FRONTEND_URL");

            return !string.IsNullOrWhiteSpace(frontendUrl) &&
                   string.Equals(origin.TrimEnd('/'), frontendUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        })
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
