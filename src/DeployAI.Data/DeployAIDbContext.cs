using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Data;

public class DeployAIDbContext : DbContext
{
    public DeployAIDbContext(DbContextOptions<DeployAIDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DeployTarget> DeployTargets => Set<DeployTarget>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AgentMemoryFile> AgentMemoryFiles => Set<AgentMemoryFile>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<ProjectDomain> ProjectDomains => Set<ProjectDomain>();
    public DbSet<DomainPurchase> DomainPurchases => Set<DomainPurchase>();
    public DbSet<ProjectVerificationRun> ProjectVerificationRuns => Set<ProjectVerificationRun>();
    public DbSet<ProjectVerificationCheckResult> ProjectVerificationCheckResults => Set<ProjectVerificationCheckResult>();
    public DbSet<ProjectCheckState> ProjectCheckStates => Set<ProjectCheckState>();
    public DbSet<TargetConfigManifest> TargetConfigManifests => Set<TargetConfigManifest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.GitHubId).IsUnique();
            entity.Property(e => e.GitHubLogin).IsRequired();
        });

        modelBuilder.Entity<ProviderCredential>(entity =>
        {
            entity.ToTable("provider_credentials");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.ProviderName, e.Label }).IsUnique();
            // Stored as a string so the column stays readable and new kinds can be added
            // without renumbering; existing rows default to Deployment.
            entity.Property(e => e.Kind)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(CredentialKind.Deployment);
            entity.HasOne(e => e.User).WithMany(u => u.ProviderCredentials).HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.GitHubRepoFullName, e.AutoDeployEnabled });
            entity.HasOne(e => e.User).WithMany(u => u.Projects).HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<DeployTarget>(entity =>
        {
            entity.ToTable("deploy_targets");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne(e => e.Project).WithMany(p => p.DeployTargets).HasForeignKey(e => e.ProjectId);
            entity.HasOne(e => e.Credential).WithMany(c => c.DeployTargets).HasForeignKey(e => e.CredentialId);
        });

        modelBuilder.Entity<Deployment>(entity =>
        {
            entity.ToTable("deployments");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasOne(e => e.Project).WithMany(p => p.Deployments).HasForeignKey(e => e.ProjectId);
        });

        modelBuilder.Entity<DeploymentTarget>(entity =>
        {
            entity.ToTable("deployment_targets");
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Deployment).WithMany(d => d.Targets).HasForeignKey(e => e.DeploymentId);
            entity.HasOne(e => e.DeployTarget).WithMany(t => t.DeploymentTargets).HasForeignKey(e => e.DeployTargetId);
        });

        modelBuilder.Entity<DeploymentLog>(entity =>
        {
            entity.ToTable("deployment_logs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DeploymentTargetId, e.Sequence });
            entity.HasOne(e => e.DeploymentTarget).WithMany(t => t.Logs).HasForeignKey(e => e.DeploymentTargetId);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<AgentMemoryFile>(entity =>
        {
            entity.ToTable("agent_memory_files");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Path }).IsUnique();
            entity.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId);
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.ToTable("notification_preferences");
            entity.HasKey(e => e.UserId);
            entity.HasOne(e => e.User).WithOne().HasForeignKey<NotificationPreference>(e => e.UserId);
        });

        modelBuilder.Entity<DomainPurchase>(entity =>
        {
            entity.ToTable("domain_purchases");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
            // Strings for the same reason as every other enum here: the column stays readable and
            // a new state can be added without renumbering rows already written.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Hostname).IsRequired().HasMaxLength(253);
            entity.Property(e => e.ProviderName).IsRequired().HasMaxLength(64);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<ProjectDomain>(entity =>
        {
            entity.ToTable("project_domains");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            // One hostname can front one application. Two rows for the same name on the same target
            // would race each other through the state machine and fight over the provider's domain
            // field.
            entity.HasIndex(e => new { e.DeployTargetId, e.Hostname }).IsUnique();
            // Stored as strings for the same reason as CredentialKind: the column stays readable,
            // and a new state can be added without renumbering the ones already in the database.
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.LastConclusiveStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Hostname).IsRequired().HasMaxLength(253);
            entity.Property(e => e.DisplayHostname).IsRequired().HasMaxLength(253);
            entity.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId);
            entity.HasOne(e => e.DeployTarget).WithMany().HasForeignKey(e => e.DeployTargetId);
        });

        modelBuilder.Entity<ProjectVerificationRun>(entity =>
        {
            entity.ToTable("project_verification_runs");
            entity.HasKey(e => e.Id);
            // The history query is always "this project, most recent first".
            entity.HasIndex(e => new { e.ProjectId, e.StartedAt });
            entity.Property(e => e.Trigger).IsRequired().HasMaxLength(32);
            entity.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Summary).IsRequired().HasMaxLength(1024);
            entity.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectVerificationCheckResult>(entity =>
        {
            entity.ToTable("project_verification_check_results");
            entity.HasKey(e => e.Id);
            // "Did this check pass yesterday and fail now" — the question the table exists for, and
            // the reason ProjectId is denormalised onto the row rather than reached through the run.
            entity.HasIndex(e => new { e.ProjectId, e.CheckId, e.ObservedAt });
            entity.HasIndex(e => e.RunId);
            entity.Property(e => e.CheckId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Target).IsRequired().HasMaxLength(32);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.Url).HasMaxLength(2048);
            entity.Property(e => e.SuggestedAction).HasMaxLength(64);
            entity.HasOne(e => e.Run).WithMany(r => r.Results).HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectCheckState>(entity =>
        {
            entity.ToTable("project_check_states");
            // One row per check per project: the current picture, upserted every sweep.
            entity.HasKey(e => new { e.ProjectId, e.CheckId });
            entity.HasIndex(e => e.ProjectId);
            entity.Property(e => e.CheckId).HasMaxLength(128);
            entity.Property(e => e.Target).IsRequired().HasMaxLength(32);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.Url).HasMaxLength(2048);
            entity.Property(e => e.SuggestedAction).HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.LastConclusiveStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.LastNotifiedStatus).HasConversion<string>().HasMaxLength(16);
            entity.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TargetConfigManifest>(entity =>
        {
            entity.ToTable("target_config_manifests");
            // One per target, replaced on every deploy — the manifest is the latest answer, not a log.
            entity.HasKey(e => e.DeployTargetId);
            entity.HasIndex(e => e.ProjectId);
            entity.Property(e => e.Branch).IsRequired().HasMaxLength(255);
            entity.Property(e => e.RequiredKeysJson).IsRequired();
            entity.Property(e => e.ValueFingerprintsJson).IsRequired();
            entity.Property(e => e.InconclusiveReason).HasMaxLength(512);
            entity.HasOne(e => e.DeployTarget).WithOne().HasForeignKey<TargetConfigManifest>(e => e.DeployTargetId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
