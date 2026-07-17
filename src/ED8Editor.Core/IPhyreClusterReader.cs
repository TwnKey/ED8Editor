namespace ED8Editor.Core;

public interface IPhyreClusterReader
{
    PhyreClusterData Read(ReadOnlyMemory<byte> data);
}
