namespace ED8Editor.ScriptHeaders;

public sealed class InvalidScriptHeaderException : IOException
{
    public InvalidScriptHeaderException(string message)
        : base(message)
    {
    }
}
