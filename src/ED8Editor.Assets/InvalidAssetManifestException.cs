namespace ED8Editor.Assets;

public sealed class InvalidAssetManifestException : IOException
{
    public InvalidAssetManifestException(string message)
        : base(message)
    {
    }

    public InvalidAssetManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
