var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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

var app = builder.Build();
app.UseForwardedHeaders();
app.UseCors();
app.MapControllers();
app.Run();