namespace ED8Editor.Core;

public interface IPhyreModelReader
{
    CpuModel Read(string assetId, ReadOnlyMemory<byte> phyreData);
}
