namespace DeployAI.Core.Exceptions;

public class DeployAIException : Exception
{
    public string ErrorCode { get; }

    public DeployAIException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
