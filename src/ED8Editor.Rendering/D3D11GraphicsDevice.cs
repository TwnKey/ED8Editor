using Vortice.Direct3D;
using Vortice.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace ED8Editor.Rendering;

public sealed class D3D11GraphicsDevice : IDisposable
{
    private D3D11GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, FeatureLevel featureLevel)
    {
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public FeatureLevel FeatureLevel { get; }

    public static D3D11GraphicsDevice Create(bool allowWarpFallback = true)
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };
        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out var device,
            out var featureLevel,
            out var context);
        if (result.Failure && allowWarpFallback)
        {
            result = D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out device,
                out featureLevel,
                out context);
        }

        result.CheckError();
        return new D3D11GraphicsDevice(device, context, featureLevel);
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
