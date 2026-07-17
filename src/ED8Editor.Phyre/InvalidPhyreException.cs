namespace ED8Editor.Phyre;

public sealed class InvalidPhyreException : IOException
{
    public InvalidPhyreException(string message) : base(message) { }
}
