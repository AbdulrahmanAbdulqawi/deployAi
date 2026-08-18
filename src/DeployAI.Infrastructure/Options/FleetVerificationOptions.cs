namespace DeployAI.Infrastructure.Options;

/// <summary>Tuning for the sweep that re-verifies every deployed project.</summary>
/// <remarks>
/// Defaults are chosen for a fleet in the tens, where the binding cost is provider API calls rather
/// than local work. Raising <see cref="MaxDegreeOfParallelism"/> multiplies the requests a single
/// sweep makes to one provider within the same second, which is the thing most likely to be rate
/// limited.
/// </remarks>
public class FleetVerificationOptions
{
    public const string SectionName = "FleetVerification";

    /// <summary>How many projects are checked at once. Each gets its own DI scope and DbContext.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// How long one project may take before it is abandoned as inconclusive. Without a budget, one
    /// provider that accepts a connection and never answers stalls the whole sweep.
    /// </summary>
    public int PerProjectTimeoutSeconds { get; set; } = 90;

    /// <summary>How long a verification run's detail is kept.</summary>
    public int RunRetentionDays { get; set; } = 30;

    /// <summary>
    /// Runs kept regardless of age, so a project that is only checked occasionally still has
    /// something to compare today against.
    /// </summary>
    public int MinimumRunsKept { get; set; } = 20;

    /// <summary>How long a check that stopped being produced stays in the current picture before ageing out.</summary>
    public int StaleCheckRetentionDays { get; set; } = 7;

    /// <summary>How much container output to read when looking for what the app is saying.</summary>
    public int RuntimeLogLines { get; set; } = 400;

    /// <summary>How close to expiry a certificate has to be before it is worth warning about.</summary>
    public int CertificateExpiryWarningDays { get; set; } = 14;

    /// <summary>
    /// How many consecutive sweeps a check may be unrunnable before the silence is itself reported.
    /// A monitor that quietly goes blind is worse than one that reports a failure.
    /// </summary>
    public int InconclusiveRunsBeforeNotify { get; set; } = 3;
}
