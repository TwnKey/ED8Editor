namespace ED8Editor.Core;

public sealed record CpuRenderPassState(
    bool BlendEnabled,
    int SourceBlend,
    int DestinationBlend,
    int BlendOperation,
    int SourceBlendAlpha,
    int DestinationBlendAlpha,
    int BlendOperationAlpha,
    byte RenderTargetWriteMask,
    CpuRasterizerState? RasterizerState = null);

public sealed record CpuRasterizerState(
    int FillMode,
    int CullMode,
    bool FrontCounterClockwise,
    int DepthBias,
    float DepthBiasClamp,
    float SlopeScaledDepthBias,
    bool DepthClipEnabled,
    bool ScissorEnabled,
    bool MultisampleEnabled,
    bool AntialiasedLineEnabled);
