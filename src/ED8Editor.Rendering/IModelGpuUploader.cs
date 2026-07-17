using ED8Editor.Core;

namespace ED8Editor.Rendering;

public interface IModelGpuUploader<out TResources>
    where TResources : IDisposable
{
    TResources Upload(CpuModel model);
}
