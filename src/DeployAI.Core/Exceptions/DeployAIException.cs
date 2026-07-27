namespace DeployAI.Core.Exceptions;

/// <summary>
/// The standard exception type for expected, user-facing failures (invalid input, missing
/// resources, provider errors) - <see cref="ErrorCode"/> is a stable machine-readable code and
/// <see cref="Exception.Message"/> is a message safe to show directly to the user. Controllers
/// catch these centrally and map them to error responses rather than 500s.
/// </summary>
public class DeployAIException : Exception
{
    /// <summary>A stable, machine-readable error code (e.g. "not_found", "invalid_credential") for callers to branch on.</summary>
    public string ErrorCode { get; }

    public DeployAIException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
