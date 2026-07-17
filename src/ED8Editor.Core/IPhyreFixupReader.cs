namespace ED8Editor.Core;

public interface IPhyreFixupReader
{
    PhyreFixupSet Read(ReadOnlyMemory<byte> data, PhyreClusterMetadata metadata);
}
