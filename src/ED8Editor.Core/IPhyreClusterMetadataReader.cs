namespace ED8Editor.Core;

public interface IPhyreClusterMetadataReader
{
    PhyreClusterMetadata Read(ReadOnlyMemory<byte> data);
}
