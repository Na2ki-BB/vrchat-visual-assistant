namespace VrcVa.Core;

public sealed class ScanException : Exception
{
    public ScanException(
        ScanFailureCode failureCode,
        ScanStage stage,
        string userMessage)
        : base(userMessage)
    {
        FailureCode = failureCode;
        Stage = stage;
        UserMessage = userMessage;
    }

    public ScanException(
        ScanFailureCode failureCode,
        ScanStage stage,
        string userMessage,
        Exception innerException)
        : base(userMessage, innerException)
    {
        FailureCode = failureCode;
        Stage = stage;
        UserMessage = userMessage;
    }

    public ScanFailureCode FailureCode { get; }

    public ScanStage Stage { get; }

    public string UserMessage { get; }
}

