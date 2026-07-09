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
            entity.HasOne(e => e.User).WithMany(u => u.ProviderCredentials).HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
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
    }
}
