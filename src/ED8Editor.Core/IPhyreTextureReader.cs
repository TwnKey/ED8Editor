namespace ED8Editor.Core;

public interface IPhyreTextureReader
{
    CpuTexture Read(string name, ReadOnlyMemory<byte> phyreData);
}
