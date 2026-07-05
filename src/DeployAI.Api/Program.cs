using System.Text;
using DeployAI.Api.Hubs;
using DeployAI.Api.Middleware;
using DeployAI.Api.Services;
using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Infrastructure;
using DeployAI.Infrastructure.Options;
using DeployAI.Providers;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.MemoryStorage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSignalR();

builder.Services.AddDbContext<DeployAIDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddDeploymentProviders();

builder.Services.AddSingleton<IOAuthStateStore, InMemoryOAuthStateStore>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IProviderCredentialTokenService, ProviderCredentialTokenService>();
builder.Services.AddScoped<IDeploymentOrchestrator, DeploymentOrchestrator>();
builder.Services.AddScoped<DeploymentJobRunner>();
builder.Services.AddScoped<IRailwayDatabaseProvisioningService, RailwayDatabaseProvisioningService>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
            NameClaimType = "sub"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/deployments"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire(config => config.UseMemoryStorage());
}
else
{
    builder.Services.AddHangfire(config =>
        config.UsePostgreSqlStorage(options =>
            options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("Default"))));
}

builder.Services.AddHangfireServer();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(appOptions.FrontendUrl)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    var connectionString = app.Configuration.GetConnectionString("Default");
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        EnsureDatabaseExists(connectionString);
    }

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<DeployAIDbContext>();
        db.Database.Migrate();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DeploymentHub>("/hubs/deployments");
app.MapHangfireDashboard("/hangfire");

app.Run();

static void EnsureDatabaseExists(string connectionString)
{
    var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
    var databaseName = builder.Database;
    if (string.IsNullOrWhiteSpace(databaseName))
    {
        return;
    }

    builder.Database = "postgres";
    using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT 1 FROM pg_database WHERE datname = @name
        """;
    command.Parameters.AddWithValue("name", databaseName);
    var exists = command.ExecuteScalar() is not null;
    if (exists)
    {
        return;
    }

    command.Parameters.Clear();
    command.CommandText = $"""CREATE DATABASE "{databaseName.Replace("\"", "\"\"")}" """;
    command.ExecuteNonQuery();
}

public partial class Program;
