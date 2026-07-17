using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreClusterReader : IPhyreClusterReader
{
    public PhyreClusterData Read(ReadOnlyMemory<byte> data)
    {
        var metadata = new PhyreClusterMetadataReader().Read(data);
        var fixups = new PhyreFixupReader().Read(data, metadata);
        return new PhyreClusterData(data, metadata, fixups);
    }
}
