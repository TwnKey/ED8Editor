namespace ED8Editor.Packages;

public sealed class InvalidPackageException : IOException
{
    public InvalidPackageException(string message)
        : base(message)
    {
    }
}
