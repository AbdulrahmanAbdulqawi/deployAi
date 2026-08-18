using System.Text;
using DeployAI.Core.Deployments;
using DeployAI.Data;
using DeployAI.Infrastructure.Email;
using DeployAI.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

public interface IFleetHealthNotificationService
{
    /// <summary>Tells the project's owner about checks whose conclusive answer just changed.</summary>
    Task NotifyAsync(
        Guid projectId,
        IReadOnlyList<CheckTransition> transitions,
        CancellationToken cancellationToken);
}

/// <summary>
/// Emails a project's owner when a check changes its mind, and only then.
/// </summary>
/// <remarks>
/// <para>
/// Edges, not states. A check that has been failing for six hours is not news on the seventh sweep,
/// and a monitor that says so every hour is a monitor people filter. The ledger that decides what
/// counts as an edge lives in <see cref="CheckLedgerTransitions"/>; this service only delivers what
/// that decided.
/// </para>
/// <para>
/// One email per project per sweep, listing every check that changed. Forty separate emails because
/// one Coolify token was revoked is the other way to teach people to filter it.
/// </para>
/// <para>
/// Reuses the existing preference and sender rather than adding a channel or a column:
/// <c>EmailOnFailure</c> already means "tell me when my app is broken", and a check that just started
/// failing is exactly that. <c>SmtpEmailSender</c> no-ops with a log line when SMTP is unconfigured,
/// so a local run is safe.
/// </para>
/// </remarks>
public sealed class FleetHealthNotificationService : IFleetHealthNotificationService
{
    private readonly DeployAIDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly AppOptions _appOptions;
    private readonly ILogger<FleetHealthNotificationService> _logger;

    public FleetHealthNotificationService(
        DeployAIDbContext db,
        IEmailSender emailSender,
        IOptions<AppOptions> appOptions,
        ILogger<FleetHealthNotificationService> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(
        Guid projectId,
        IReadOnlyList<CheckTransition> transitions,
        CancellationToken cancellationToken)
    {
        if (transitions.Count == 0)
        {
            return;
        }

        var project = await _db.Projects
            .AsNoTracking()
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        if (project?.User is null || string.IsNullOrWhiteSpace(project.User.Email))
        {
            return;
        }

        var prefs = await _db.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == project.UserId, cancellationToken);

        if (!(prefs?.EmailOnFailure ?? true))
        {
            return;
        }

        var (subject, body) = Compose(project.Name, projectId, transitions);

        try
        {
            await _emailSender.SendAsync(project.User.Email, subject, body, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A mail server being down must not cost the verification run that was already recorded.
            _logger.LogWarning(ex, "Could not send the health email for project {ProjectId}.", projectId);
        }
    }

    private (string Subject, string Body) Compose(
        string projectName,
        Guid projectId,
        IReadOnlyList<CheckTransition> transitions)
    {
        var startedFailing = transitions.Where(t => t.Notification == CheckNotification.StartedFailing).ToList();
        var recovered = transitions.Where(t => t.Notification == CheckNotification.Recovered).ToList();
        var blind = transitions.Where(t => t.Notification == CheckNotification.WentBlind).ToList();

        // The subject names the worst thing that happened, because that is what decides whether
        // someone opens it now or later.
        var subject = startedFailing.Count > 0
            ? $"{projectName}: {Describe(startedFailing.Count, "check")} started failing"
            : recovered.Count > 0
                ? $"{projectName}: back to normal"
                : $"{projectName}: DeployAI can't check {Describe(blind.Count, "thing")}";

        var body = new StringBuilder();
        body.AppendLine($"{projectName}");
        body.AppendLine();

        Append(body, startedFailing, "Started failing:");
        Append(body, recovered, "Recovered:");

        if (blind.Count > 0)
        {
            // Deliberately its own section with its own wording. Telling someone their app is broken
            // when the truth is that DeployAI cannot see it would send them looking for a fault that
            // is on our side, not theirs.
            body.AppendLine("DeployAI has not been able to run these checks, so they say nothing");
            body.AppendLine("about whether your app is working:");
            foreach (var transition in blind)
            {
                body.AppendLine($"  - {transition.Label}: {transition.Message}");
            }

            body.AppendLine();
        }

        body.AppendLine($"See the details: {_appOptions.FrontendUrl.TrimEnd('/')}/fleet");

        return (subject, body.ToString());
    }

    private static void Append(StringBuilder body, IReadOnlyList<CheckTransition> transitions, string heading)
    {
        if (transitions.Count == 0)
        {
            return;
        }

        body.AppendLine(heading);
        foreach (var transition in transitions)
        {
            body.AppendLine($"  - {transition.Label}: {transition.Message}");
        }

        body.AppendLine();
    }

    private static string Describe(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
