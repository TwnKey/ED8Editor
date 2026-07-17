namespace ED8Editor.Ops;

public sealed class InvalidOpsException : IOException
{
    public InvalidOpsException(string message)
        : base(message)
    {
    }

    public InvalidOpsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
