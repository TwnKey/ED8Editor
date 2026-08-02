using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

public static partial class PhyreSchemaLibrary
{
private static readonly string[] AssetProcessorPrimitiveTypes =
{
    "bool",
    "float",
    "PChar",
    "PContextSwitch",
    "PInt32",
    "PLightType",
    "PLODBlendType",
    "PLODMetricType",
    "PRenderDataType",
    "PSceneRenderPassType",
    "PShadowCasterType",
    "PTextureFormatBase",
    "PUInt16",
    "PUInt32",
    "PUInt8",
};

private static readonly ClassRow[] AssetProcessorRows =
{
    new("CD3D11_BLEND_DESC", "-", 264, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("AlphaToCoverageEnable", "PInt32", 0, 4, 0x10, 0),
        new("IndependentBlendEnable", "PInt32", 4, 4, 0x10, 0),
        new("RenderTarget", "D3D11_RENDER_TARGET_BLEND_DESC", 8, 32, 0x10, 8),
    }),
    new("CD3D11_DEPTH_STENCIL_DESC", "-", 52, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("DepthEnable", "PInt32", 0, 4, 0x10, 0),
        new("DepthWriteMask", "PInt32", 4, 4, 0x30, 0),
        new("DepthFunc", "PInt32", 8, 4, 0x30, 0),
        new("StencilEnable", "PInt32", 12, 4, 0x10, 0),
        new("StencilReadMask", "PUInt8", 16, 1, 0x10, 0),
        new("StencilWriteMask", "PUInt8", 17, 1, 0x10, 0),
        new("FrontFace", "D3D11_DEPTH_STENCILOP_DESC", 20, 16, 0x10, 0),
        new("BackFace", "D3D11_DEPTH_STENCILOP_DESC", 36, 16, 0x10, 0),
    }),
    new("CD3D11_RASTERIZER_DESC", "-", 40, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("FillMode", "PInt32", 0, 4, 0x30, 0),
        new("CullMode", "PInt32", 4, 4, 0x30, 0),
        new("FrontCounterClockwise", "PInt32", 8, 4, 0x10, 0),
        new("DepthBias", "PInt32", 12, 4, 0x10, 0),
        new("DepthBiasClamp", "float", 16, 4, 0x10, 0),
        new("SlopeScaledDepthBias", "float", 20, 4, 0x10, 0),
        new("DepthClipEnable", "PInt32", 24, 4, 0x10, 0),
        new("ScissorEnable", "PInt32", 28, 4, 0x10, 0),
        new("MultisampleEnable", "PInt32", 32, 4, 0x10, 0),
        new("AntialiasedLineEnable", "PInt32", 36, 4, 0x10, 0),
    }),
    new("D3D11_DEPTH_STENCILOP_DESC", "-", 16, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("StencilFailOp", "PInt32", 0, 4, 0x30, 0),
        new("StencilDepthFailOp", "PInt32", 4, 4, 0x30, 0),
        new("StencilPassOp", "PInt32", 8, 4, 0x30, 0),
        new("StencilFunc", "PInt32", 12, 4, 0x30, 0),
    }),
    new("D3D11_RENDER_TARGET_BLEND_DESC", "-", 32, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("BlendEnable", "PInt32", 0, 4, 0x10, 0),
        new("SrcBlend", "PInt32", 4, 4, 0x30, 0),
        new("DestBlend", "PInt32", 8, 4, 0x30, 0),
        new("BlendOp", "PInt32", 12, 4, 0x30, 0),
        new("SrcBlendAlpha", "PInt32", 16, 4, 0x30, 0),
        new("DestBlendAlpha", "PInt32", 20, 4, 0x30, 0),
        new("BlendOpAlpha", "PInt32", 24, 4, 0x30, 0),
        new("RenderTargetWriteMask", "PUInt8", 28, 1, 0x10, 0),
    }),
    new("PArray<PContextSwitch *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PContextSwitch", 4, 4, 0x80000002, 0),
    }),
    new("PArray<PContextVariantFoldingTable>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PContextVariantFoldingTable", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PDataBlockD3D11>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PDataBlockD3D11", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PDynamicSegmentDesc>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PDynamicSegmentDesc", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PEffectVariant *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PEffectVariant", 4, 4, 0x80000002, 0),
    }),
    new("PArray<PInt32>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PInt32", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PLightType *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PLightType", 4, 4, 0x80000002, 0),
    }),
    new("PArray<PLODLevel>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PLODLevel", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PMaterialSwitch>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PMaterialSwitch", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PMatrix4>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PMatrix4", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PMeshInstanceSegmentContext>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PMeshInstanceSegmentContext", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PMeshSegment>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PMeshSegment", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PNodeContext>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PNodeContext", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PSceneRenderPass *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PSceneRenderPass", 4, 4, 0x80000002, 0),
    }),
    new("PArray<PSceneRenderPass>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PSceneRenderPass", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShader>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShader", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShaderParameterCaptureBufferLocation>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShaderParameterCaptureBufferLocation", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShaderParameterCaptureBufferLocationTypeConstantBuffer>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShaderParameterCaptureBufferLocationTypeConstantBuffer", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShaderParameterDefinition>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShaderParameterDefinition", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShaderPass>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShaderPass", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShaderPassInfo>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShaderPassInfo", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShaderStreamDefinition>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShaderStreamDefinition", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PShadowCasterType *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PShadowCasterType", 4, 4, 0x80000002, 0),
    }),
    new("PArray<PSkeletonJointBounds>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PSkeletonJointBounds", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PSkinBoneRemap>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PSkinBoneRemap", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PStreamInputDescD3D11>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PStreamInputDescD3D11", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PString>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PString", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PUInt32>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PUInt32", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PUInt8>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PUInt8", 4, 4, 0x80000000, 0),
    }),
    new("PArray<PVertexStream>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
        new("m_els", "PVertexStream", 4, 4, 0x80000000, 0),
    }),
    new("PAssetReference", "PBase", 40, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_id", "PString", 24, 4, 0x10, 0),
        new("m_asset", "PBase", 28, 4, 0x16, 0),
        new("m_assetType", "PClassDescriptor", 32, 4, 0x12, 0),
    }),
    new("PAssetReferenceImport", "PBase", 12, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_id", "PString", 4, 4, 0x10, 0),
        new("m_targetAssetType", "PClassDescriptor", 0, 4, 0x12, 0),
    }),
    new("PBase", "-", 0, 1, 0, 0, 0, 0x20, 0, Array.Empty<MemberRow>()),
    new("PClassDescriptor", "PBase", 148, 16, 24, 24, 24, 0x2, 0, Array.Empty<MemberRow>()),
    new("PClusterHeader", "PClusterHeaderD3D11", 84, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PClusterHeaderBase", "PBase", 72, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_phyreMarker", "PUInt32", 0, 4, 0x8, 0),
        new("m_size", "PUInt32", 4, 4, 0x8, 0),
        new("m_instanceListCount", "PUInt32", 16, 4, 0x8, 0),
        new("m_packedNamespaceSize", "PUInt32", 8, 4, 0x8, 0),
        new("m_arrayFixupSize", "PUInt32", 20, 4, 0x8, 0),
        new("m_arrayFixupCount", "PUInt32", 24, 4, 0x8, 0),
        new("m_pointerFixupSize", "PUInt32", 28, 4, 0x8, 0),
        new("m_pointerFixupCount", "PUInt32", 32, 4, 0x8, 0),
        new("m_pointerArrayFixupSize", "PUInt32", 36, 4, 0x8, 0),
        new("m_pointerArrayFixupCount", "PUInt32", 40, 4, 0x8, 0),
        new("m_pointersInArraysCount", "PUInt32", 44, 4, 0x8, 0),
        new("m_userFixupCount", "PUInt32", 48, 4, 0x8, 0),
        new("m_userFixupDataSize", "PUInt32", 52, 4, 0x8, 0),
        new("m_totalDataSize", "PUInt32", 56, 4, 0x8, 0),
        new("m_headerClassInstanceCount", "PUInt32", 60, 4, 0x8, 0),
        new("m_headerClassChildCount", "PUInt32", 64, 4, 0x8, 0),
        new("m_platformID", "PUInt32", 12, 4, 0x8, 0),
        new("m_physicsEngineID", "PUInt32", 68, 4, 0x8, 0),
    }),
    new("PClusterHeaderD3D11", "PClusterHeaderBase", 84, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_indexBufferSize", "PUInt32", 72, 4, 0x0, 0),
        new("m_vertexBufferSize", "PUInt32", 76, 4, 0x0, 0),
        new("m_maxTextureBufferSize", "PUInt32", 80, 4, 0x0, 0),
    }),
    new("PContextVariantFoldingTable", "PBase", 20, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_contextVariantIndex", "PUInt32", 0, 4, 0x0, 0),
        new("m_contextVariantVpIndex", "PUInt32", 4, 4, 0x0, 0),
        new("m_contextVariantFpIndex", "PUInt32", 8, 4, 0x0, 0),
        new("m_contextVariantGsIndex", "PUInt32", 12, 4, 0x0, 0),
        new("m_contextVariantCsIndex", "PUInt32", 16, 4, 0x0, 0),
    }),
    new("PDataBlockBase", "PBase", 24, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_memoryType", "PUInt8", 21, 1, 0x10, 0),
        new("m_stride", "PUInt32", 0, 4, 0x10, 0),
        new("m_elementCount", "PUInt32", 4, 4, 0x10, 0),
        new("m_streams", "PArray<PVertexStream>", 8, 8, 0x10, 0),
    }),
    new("PDataBlockBufferD3D11", "PBase", 20, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PDataBlockD3D11", "PDataBlockBase", 64, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_buffers", "PSharray<PDataBlockBufferD3D11>", 24, 24, 0x0, 0),
        new("m_dataSize", "PUInt32", 56, 4, 0x0, 0),
        new("m_offsetInVertexBuffer", "PUInt32", 48, 4, 0x8, 0),
    }),
    new("PDynamicMesh", "PBase", 28, 16, 4, 4, 4, 0x2, 0, new MemberRow[]
    {
        new("m_dynamicStreams", "PArray<PVertexStream>", 8, 8, 0x8, 0),
        new("m_segments", "PArray<PDynamicSegmentDesc>", 16, 8, 0x8, 0),
        new("m_mesh", "PMesh", 4, 4, 0xA, 0),
    }),
    new("PDynamicMeshInstance", "PBase", 12, 4, 4, 4, 4, 0x8, 0, new MemberRow[]
    {
        new("m_dynamicMesh", "PDynamicMesh", 4, 4, 0xA, 0),
    }),
    new("PDynamicSegmentDesc", "PBase", 16, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_startStreamIndex", "PUInt32", 0, 4, 0x8, 0),
        new("m_streamCount", "PUInt32", 4, 4, 0x8, 0),
        new("m_elementCount", "PUInt32", 8, 4, 0x8, 0),
        new("m_indexCount", "PInt32", 12, 4, 0x8, 0),
    }),
    new("PEffect", "PBase", 64, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_supportedLightMask", "PUInt32", 0, 4, 0x0, 0),
        new("m_supportedShadowCasterMask", "PUInt32", 4, 4, 0x0, 0),
        new("m_effectFile", "PString", 8, 4, 0x0, 0),
        new("m_effectSource", "PString", 52, 4, 0x0, 0),
        new("m_effectVariants", "PArray<PEffectVariant *>", 12, 8, 0x0, 0),
        new("m_supportedLightTypes", "PArray<PLightType *>", 20, 8, 0x0, 0),
        new("m_supportedShadowCasterTypes", "PArray<PShadowCasterType *>", 28, 8, 0x0, 0),
        new("m_contextSwitches", "PArray<PContextSwitch *>", 36, 8, 0x0, 0),
        new("m_contextVariantSwitches", "PArray<PNodeContext>", 44, 8, 0x0, 0),
        new("m_maxLightCount", "PUInt32", 56, 4, 0x0, 0),
        new("m_numSupportedShaderLODLevels", "PUInt32", 60, 4, 0x0, 0),
    }),
    new("PEffectVariant", "PBase", 52, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_effect", "PEffect", 0, 4, 0x2, 0),
        new("m_switches", "PArray<PMaterialSwitch>", 4, 8, 0x0, 0),
        new("m_sceneRenderPasses", "PArray<PSceneRenderPass>", 12, 8, 0x0, 0),
        new("m_sceneRenderPassLookup", "PArray<PSceneRenderPass *>", 20, 8, 0x0, 0),
        new("m_largestShaderPassCount", "PUInt16", 28, 2, 0x0, 0),
        new("m_tweakableShaderParameterDefinitions", "PArray<PShaderParameterDefinition>", 32, 8, 0x0, 0),
        new("m_untweakableShaderParameterDefinitions", "PArray<PShaderParameterDefinition>", 40, 8, 0x0, 0),
        new("m_tweakableParameterBufferSize", "PUInt16", 48, 2, 0x0, 0),
        new("m_untweakableParameterBufferSize", "PUInt16", 50, 2, 0x0, 0),
    }),
    new("PIndexDataBlock", "PIndexDataBlockD3D11", 52, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PIndexDataBlockBase", "PBase", 20, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_memoryType", "PUInt8", 13, 1, 0x10, 0),
        new("m_elementCount", "PUInt32", 8, 4, 0x10, 0),
        new("m_type", "PUInt8", 12, 1, 0x10, 0),
        new("m_minimumIndex", "PUInt32", 0, 4, 0x10, 0),
        new("m_maximumIndex", "PUInt32", 4, 4, 0x10, 0),
    }),
    new("PIndexDataBlockBufferD3D11", "PBase", 12, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PIndexDataBlockD3D11", "PIndexDataBlockBase", 52, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_buffers", "PSharray<PIndexDataBlockBufferD3D11>", 20, 16, 0x0, 0),
        new("m_dataSize", "PUInt32", 44, 4, 0x0, 0),
        new("m_offsetInIndexBuffer", "PUInt32", 36, 4, 0x8, 0),
    }),
    new("PInstanceListHeader", "PBase", 36, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_classID", "PUInt32", 0, 4, 0x8, 0),
        new("m_count", "PUInt32", 4, 4, 0x8, 0),
        new("m_size", "PUInt32", 8, 4, 0x8, 0),
        new("m_objectsSize", "PUInt32", 12, 4, 0x8, 0),
        new("m_arraysSize", "PUInt32", 16, 4, 0x8, 0),
        new("m_pointersInArraysCount", "PUInt32", 20, 4, 0x8, 0),
        new("m_arrayFixupCount", "PUInt32", 24, 4, 0x8, 0),
        new("m_pointerFixupCount", "PUInt32", 28, 4, 0x8, 0),
        new("m_pointerArrayFixupCount", "PUInt32", 32, 4, 0x8, 0),
    }),
    new("PLODGroup", "PBase", 72, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_levels", "PArray<PLODLevel>", 60, 8, 0x0, 0),
        new("m_minBounds", "Vector3", 0, 16, 0x0, 0),
        new("m_maxBounds", "Vector3", 16, 16, 0x0, 0),
        new("m_blendRange", "float", 40, 4, 0x0, 0),
        new("m_shaderLODLevelDistance", "float", 44, 4, 0x0, 0),
        new("m_lodMetricType", "PLODMetricType", 52, 4, 0x2, 0),
        new("m_lodBlendType", "PLODBlendType", 56, 4, 0x2, 0),
        new("m_isEnabled", "bool", 68, 1, 0x0, 0),
    }),
    new("PLODLevel", "PBase", 36, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_lodGroup", "PLODGroup", 0, 4, 0x2, 0),
        new("m_meshInstances", "PSharray<PMeshInstance *>", 4, 8, 0x0, 0),
        new("m_minimumThreshold", "float", 12, 4, 0x0, 0),
        new("m_maximumThreshold", "float", 16, 4, 0x0, 0),
    }),
    new("PMaterial", "PBase", 16, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_effectVariant", "PEffectVariant", 0, 4, 0x2, 0),
        new("m_parameterBuffer", "PParameterBuffer", 4, 4, 0x2, 0),
        new("m_remapFrom", "PSceneRenderPassType", 8, 4, 0x2, 0),
        new("m_remapTo", "PSceneRenderPassType", 12, 4, 0x2, 0),
    }),
    new("PMaterialSet", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_materials", "PSharray<PMaterial *>", 0, 8, 0x8, 0),
    }),
    new("PMaterialSwitch", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_name", "PString", 0, 4, 0x0, 0),
        new("m_value", "PString", 4, 4, 0x0, 0),
    }),
    new("PMatrix4", "-", 64, 4, 0, 0, 0, 0x20, 0, new MemberRow[]
    {
        new("m_elements", "float", 0, 4, 0x0, 16),
    }),
    new("PMatrix4x3", "-", 48, 4, 0, 0, 0, 0x20, 0, new MemberRow[]
    {
        new("m_elements", "float", 0, 4, 0x0, 12),
    }),
    new("PMesh", "PBase", 56, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_meshSegments", "PArray<PMeshSegment>", 0, 8, 0x8, 0),
        new("m_skeletonMatrices", "PArray<PMatrix4>", 8, 8, 0x8, 0),
        new("m_skeletonBounds", "PArray<PSkeletonJointBounds>", 16, 8, 0x8, 0),
        new("m_defaultPose", "PArray<PMatrix4>", 24, 8, 0x8, 0),
        new("m_matrixNames", "PArray<PString>", 32, 8, 0x8, 0),
        new("m_matrixParents", "PArray<PInt32>", 40, 8, 0x8, 0),
        new("m_defaultMaterials", "PMaterialSet", 48, 8, 0x8, 0),
    }),
    new("PMeshInstance", "PBase", 112, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_mesh", "PMesh", 0, 4, 0xA, 0),
        new("m_localToWorldMatrix", "PWorldMatrix", 4, 4, 0xA, 0),
        new("m_currentPose", "PArray<PMatrix4>", 8, 8, 0x8, 0),
        new("m_materialSet", "PMaterialSet", 16, 4, 0xA, 0),
        new("m_instanceSegment", "PMeshSegment", 20, 4, 0xA, 0),
        new("m_dynamicMeshInstance", "PDynamicMeshInstance", 24, 4, 0xA, 0),
        new("m_bounds", "PMeshInstanceBounds", 28, 4, 0xA, 0),
        new("m_lodLevel", "PLODLevel", 32, 4, 0xA, 0),
        new("m_segmentContext", "PArray<PMeshInstanceSegmentContext>", 36, 8, 0x8, 0),
        new("m_exFlags", "PUInt32", 48, 4, 0x8, 0),
        new("m_edgeParameters", "Vector4", 52, 16, 0x8, 0),
        new("m_nodeMaterialDiffuse", "Vector4", 68, 16, 0x8, 0),
        new("m_nodeMaterialEmission", "Vector4", 84, 16, 0x8, 0),
        new("m_gameMaterialIDs", "PArray<PUInt32>", 100, 8, 0x8, 0),
        new("m_drawOrder", "float", 108, 4, 0x8, 0),
    }),
    new("PMeshInstanceBounds", "PBase", 32, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_meshInstance", "PMeshInstance", 28, 4, 0x2, 0),
        new("m_worldMatrix", "PWorldMatrix", 12, 4, 0x2, 0),
        new("m_min", "float", 0, 4, 0x0, 3),
        new("m_size", "float", 16, 4, 0x0, 3),
    }),
    new("PMeshInstanceSegmentContext", "PMeshSegmentContext", 36, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_streamBindings", "PSharray<PMeshInstanceSegmentStreamBinding *>", 28, 8, 0x8, 0),
    }),
    new("PMeshInstanceSegmentStreamBinding", "PBase", 12, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_renderDataType", "PRenderDataType", 0, 4, 0xA, 0),
        new("m_name", "PString", 4, 4, 0x8, 0),
        new("m_nameHash", "PUInt16", 8, 2, 0x8, 0),
        new("m_index", "PUInt8", 10, 1, 0x8, 0),
        new("m_inputSet", "PUInt8", 11, 1, 0x8, 0),
    }),
    new("PMeshSegment", "PMeshSegmentD3D11", 80, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PMeshSegmentBase", "PBase", 20, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_materialIndex", "PUInt32", 0, 4, 0x10, 0),
        new("m_matrixIndex", "PInt32", 4, 4, 0x10, 0),
        new("m_skinBones", "PArray<PSkinBoneRemap>", 8, 8, 0x10, 0),
        new("m_primitiveType", "PInt32", 16, 4, 0x30, 0),
    }),
    new("PMeshSegmentContext", "PBase", 20, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PMeshSegmentD3D11", "PMeshSegmentBase", 80, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_vertexData", "PArray<PDataBlockD3D11>", 20, 8, 0x10, 0),
        new("m_indexData", "PIndexDataBlockD3D11", 28, 52, 0x10, 0),
    }),
    new("PNode", "PBase", 84, 4, 4, 4, 4, 0x0, 0, new MemberRow[]
    {
        new("m_parent", "PNode", 4, 4, 0x12, 0),
        new("m_firstChild", "PNode", 8, 4, 0x12, 0),
        new("m_next", "PNode", 0, 4, 0x12, 0),
        new("m_worldMatrix", "PWorldMatrix", 12, 4, 0x12, 0),
        new("m_localMatrix", "PMatrix4", 16, 64, 0x10, 0),
        new("m_name", "PString", 80, 4, 0x10, 0),
    }),
    new("PNodeContext", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_packedSwitches", "PSharray<PUInt32>", 0, 8, 0x8, 0),
    }),
    new("PParameterBuffer", "PParameterBufferBase", 16, 4, 0, 0, 0, 0xC, 0, new MemberRow[]
    {
        new("m_effectVariant", "PEffectVariant", 4, 4, 0x2, 0),
        new("m_tweakableShaderParameterDefinitions", "PArray<PShaderParameterDefinition>", 8, 8, 0x0, 0),
    }),
    new("PParameterBufferBase", "PBase", 4, 16, 0, 0, 0, 0x22, 0, new MemberRow[]
    {
        new("m_parameterBufferSize", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSamplerState", "PSamplerStateD3D11", 36, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PSamplerStateBase", "PBase", 32, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_minFilter", "PUInt8", 0, 1, 0x10, 0),
        new("m_magFilter", "PUInt8", 1, 1, 0x10, 0),
        new("m_wrapS", "PUInt8", 2, 1, 0x10, 0),
        new("m_wrapT", "PUInt8", 3, 1, 0x10, 0),
        new("m_wrapR", "PUInt8", 4, 1, 0x10, 0),
        new("m_lodBias", "float", 8, 4, 0x10, 0),
        new("m_maxAnisotropy", "float", 12, 4, 0x10, 0),
        new("m_borderColor", "PUInt32", 16, 4, 0x10, 0),
        new("m_baseLevel", "PUInt32", 20, 4, 0x10, 0),
        new("m_maxLevel", "PUInt32", 24, 4, 0x10, 0),
        new("m_flags", "PUInt32", 28, 4, 0x10, 0),
    }),
    new("PSamplerStateD3D11", "PSamplerStateBase", 36, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PSceneRenderPass", "PBase", 40, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_passType", "PSceneRenderPassType", 0, 4, 0x2, 0),
        new("m_shaders", "PArray<PShader>", 4, 8, 0x0, 0),
        new("m_entryPoints", "PArray<PShaderPassInfo>", 12, 8, 0x0, 0),
        new("m_variantsFoldingTable", "PArray<PContextVariantFoldingTable>", 20, 8, 0x0, 0),
        new("m_platforms", "PArray<PString>", 28, 8, 0x0, 0),
        new("m_platformsAreInclude", "bool", 36, 1, 0x0, 0),
    }),
    new("PShader", "PBase", 32, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_passes", "PArray<PShaderPass>", 4, 8, 0x0, 0),
        new("m_parameterDefinitionsForPasses", "PArray<PShaderParameterDefinition>", 12, 8, 0x0, 0),
        new("m_streamDefinitionsForPasses", "PArray<PShaderStreamDefinition>", 20, 8, 0x0, 0),
        new("m_parameterBufferSize", "PUInt32", 28, 4, 0x0, 0),
        new("m_parameterBufferFrequenciesRequired", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PShaderComputeProgram", "PShaderComputeProgramD3D11", 80, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderComputeProgramD3D11", "PShaderProgramD3D11", 80, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderFragmentProgram", "PShaderFragmentProgramD3D11", 68, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderFragmentProgramD3D11", "PShaderProgramD3D11", 68, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderGeometryProgram", "PShaderGeometryProgramD3D11", 68, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderGeometryProgramD3D11", "PShaderProgramD3D11", 68, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderParameterCaptureBufferLocation", "PBase", 2, 2, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_offset", "PUInt16", 0, 2, 0x0, 0),
    }),
    new("PShaderParameterCaptureBufferLocationSize", "PShaderParameterCaptureBufferLocation", 4, 2, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_size", "PUInt16", 2, 2, 0x0, 0),
    }),
    new("PShaderParameterCaptureBufferLocationTypeConstantBuffer", "PShaderParameterCaptureBufferLocation", 16, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_constantBufferLocation", "PUInt32", 4, 4, 0x0, 0),
        new("m_size", "PUInt32", 8, 4, 0x0, 0),
        new("m_type", "PUInt8", 12, 1, 0x0, 0),
    }),
    new("PShaderParameterCaptureBufferSampler", "PShaderParameterCaptureBufferTextureBase", 16, 4, 0, 0, 0, 0x20, 0, new MemberRow[]
    {
        new("m_unusedPointer", "PTextureCommonBase", 12, 4, 0x12, 0),
    }),
    new("PShaderParameterCaptureBufferTexture2D", "PShaderParameterCaptureBufferTextureBase", 16, 4, 0, 0, 0, 0x20, 0, new MemberRow[]
    {
        new("m_texture", "PTexture2D", 12, 4, 0x12, 0),
    }),
    new("PShaderParameterCaptureBufferTextureBase", "PBase", 12, 4, 0, 0, 0, 0x28, 0, new MemberRow[]
    {
        new("m_parameterType", "PUInt32", 0, 4, 0x10, 0),
        new("m_samplerState", "PSamplerState", 8, 4, 0x12, 0),
        new("m_textureBufferIndex", "PUInt32", 4, 4, 0x10, 0),
    }),
    new("PShaderParameterDefinition", "PBase", 16, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_name", "PString", 4, 4, 0x0, 0),
        new("m_parameterType", "PUInt8", 2, 1, 0x0, 0),
        new("m_dataType", "PUInt8", 3, 1, 0x0, 0),
        new("m_arrayElementCount", "PUInt16", 0, 2, 0x0, 0),
        new("m_bufferLoc", "PShaderParameterCaptureBufferLocationSize", 8, 4, 0x0, 0),
        new("m_constantBufferLocation", "PUInt32", 12, 4, 0x0, 0),
    }),
    new("PShaderPass", "PShaderPassD3D11", 596, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PShaderPassBase", "PBase", 24, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_vertexProgram", "PShaderVertexProgram", 0, 4, 0x12, 0),
        new("m_fragmentProgram", "PShaderFragmentProgram", 4, 4, 0x12, 0),
        new("m_geometryProgram", "PShaderGeometryProgram", 8, 4, 0x12, 0),
        new("m_computeProgram", "PShaderComputeProgram", 12, 4, 0x12, 0),
        new("m_streamLocations", "PArray<PShaderParameterCaptureBufferLocation>", 16, 8, 0x10, 0),
    }),
    new("PShaderPassD3D11", "PShaderPassBase", 596, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_state", "PShaderPassStateD3D11", 24, 380, 0x10, 0),
        new("m_vertexParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 404, 24, 0x10, 0),
        new("m_fragmentParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 428, 24, 0x10, 0),
        new("m_geometryParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 452, 24, 0x10, 0),
        new("m_computeParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 476, 24, 0x10, 0),
        new("m_vertexTexParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 500, 24, 0x10, 0),
        new("m_fragmentTexParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 524, 24, 0x10, 0),
        new("m_geometryTexParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 548, 24, 0x10, 0),
        new("m_computeTexParameterLocation", "PShaderPassParameterLocationTypesConstantBuffer", 572, 24, 0x10, 0),
    }),
    new("PShaderPassInfo", "PBase", 32, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_vertexEntryPoint", "PString", 0, 4, 0x0, 0),
        new("m_vertexProfile", "PUInt32", 16, 4, 0x0, 0),
        new("m_fragmentEntryPoint", "PString", 4, 4, 0x0, 0),
        new("m_fragmentProfile", "PUInt32", 20, 4, 0x0, 0),
        new("m_geometryEntryPoint", "PString", 8, 4, 0x0, 0),
        new("m_geometryProfile", "PUInt32", 24, 4, 0x0, 0),
        new("m_computeEntryPoint", "PString", 12, 4, 0x0, 0),
        new("m_computeProfile", "PUInt32", 28, 4, 0x0, 0),
    }),
    new("PShaderPassParameterLocationTypesBase", "PBase", 16, 2, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_parameterStart", "PUInt16", 0, 2, 0x10, 4),
        new("m_parameterCount", "PUInt16", 8, 2, 0x10, 4),
    }),
    new("PShaderPassParameterLocationTypesConstantBuffer", "PShaderPassParameterLocationTypesBase", 24, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_parameterLocations", "PArray<PShaderParameterCaptureBufferLocationTypeConstantBuffer>", 16, 8, 0x10, 0),
    }),
    new("PShaderPassStateBase", "PBase", 4, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_importantState", "PUInt32", 0, 4, 0x10, 0),
    }),
    new("PShaderPassStateD3D11", "PShaderPassStateBase", 380, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_rasterDesc", "CD3D11_RASTERIZER_DESC", 4, 40, 0x10, 0),
        new("m_depthDesc", "CD3D11_DEPTH_STENCIL_DESC", 44, 52, 0x10, 0),
        new("m_blendDesc", "CD3D11_BLEND_DESC", 96, 264, 0x10, 0),
        new("m_stencilRef", "PUInt8", 376, 1, 0x10, 0),
    }),
    new("PShaderProgramBase", "PBase", 1, 1, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PShaderProgramD3D11", "PShaderProgramBase", 64, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_compiledCode", "PArray<PUInt8>", 12, 8, 0x10, 0),
        new("m_constantBufferSize", "PUInt32", 20, 4, 0x10, 0),
        new("m_globalConstantBufferIndex", "PUInt32", 24, 4, 0x10, 0),
        new("m_shaderProfile", "PUInt32", 60, 4, 0x10, 0),
    }),
    new("PShaderStreamDefinition", "PBase", 16, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_name", "PString", 4, 4, 0x10, 0),
        new("m_dataType", "PUInt8", 14, 1, 0x10, 0),
        new("m_renderType", "PRenderDataType", 0, 4, 0x12, 0),
        new("m_bufferLoc", "PShaderParameterCaptureBufferLocationSize", 8, 4, 0x10, 0),
        new("m_nameHash", "PUInt16", 12, 2, 0x10, 0),
        new("m_index", "PUInt8", 15, 1, 0x10, 0),
    }),
    new("PShaderVertexProgram", "PShaderVertexProgramD3D11", 80, 4, 0, 0, 0, 0x8, 0, Array.Empty<MemberRow>()),
    new("PShaderVertexProgramD3D11", "PShaderProgramD3D11", 80, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_inputLayout", "PStreamInputLayoutD3D11", 64, 12, 0x10, 0),
    }),
    new("PSharray<PDataBlockBufferD3D11>", "PBase", 24, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_u", "PDataBlockBufferD3D11", 4, 20, 0x80000040, 0),
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSharray<PIndexDataBlockBufferD3D11>", "PBase", 16, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_u", "PIndexDataBlockBufferD3D11", 4, 12, 0x80000040, 0),
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSharray<PMaterial *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_u", "PMaterial", 4, 4, 0x80000042, 0),
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSharray<PMeshInstance *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_u", "PMeshInstance", 4, 4, 0x80000042, 0),
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSharray<PMeshInstanceSegmentStreamBinding *>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_u", "PMeshInstanceSegmentStreamBinding", 4, 4, 0x80000042, 0),
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSharray<PUInt32>", "PBase", 8, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_u", "PUInt32", 4, 4, 0x80000040, 0),
        new("m_count", "PUInt32", 0, 4, 0x0, 0),
    }),
    new("PSkeletonJointBounds", "PBase", 32, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_min", "float", 0, 4, 0x8, 3),
        new("m_size", "float", 16, 4, 0x8, 3),
        new("m_hierarchyMatrixIndex", "PUInt32", 12, 4, 0x8, 0),
        new("m_pad", "PUInt32", 28, 4, 0x8, 0),
    }),
    new("PSkinBoneRemap", "PBase", 4, 2, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_hierarchyMatrixIndex", "PUInt16", 0, 2, 0x10, 0),
        new("m_skeletonMatrixIndex", "PUInt16", 2, 2, 0x10, 0),
    }),
    new("PStreamInputDescD3D11", "PBase", 20, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_semantic", "PString", 0, 4, 0x10, 0),
        new("m_renderType", "PRenderDataType", 4, 4, 0x12, 0),
        new("m_semanticIndex", "PUInt32", 8, 4, 0x10, 0),
        new("m_d3dFormat", "PUInt32", 12, 4, 0x10, 0),
        new("m_inputSlot", "PUInt32", 16, 4, 0x10, 0),
    }),
    new("PStreamInputLayoutD3D11", "PBase", 12, 4, 0, 0, 0, 0x8, 0, new MemberRow[]
    {
        new("m_streams", "PArray<PStreamInputDescD3D11>", 0, 8, 0x10, 0),
    }),
    new("PString", "PBase", 4, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_buffer", "PChar", 0, 4, 0x80000010, 0),
    }),
    new("PTexture2D", "PTexture2DD3D11", 116, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PTexture2DBase", "PTextureCommonBase", 36, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_width", "PUInt32", 28, 4, 0x10, 0),
        new("m_height", "PUInt32", 32, 4, 0x10, 0),
    }),
    new("PTexture2DD3D11", "PTexture2DBase", 116, 4, 0, 0, 0, 0x0, 0, Array.Empty<MemberRow>()),
    new("PTextureCommonBase", "PBase", 28, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_format", "PTextureFormatBase", 0, 4, 0x12, 0),
        new("m_memoryType", "PUInt8", 5, 1, 0x10, 0),
        new("m_mipmapCount", "PUInt32", 12, 4, 0x10, 0),
        new("m_maxMipLevel", "PUInt32", 16, 4, 0x10, 0),
        new("m_textureFlags", "PInt32", 20, 4, 0x10, 0),
    }),
    new("PVertexStream", "PBase", 12, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_type", "PUInt8", 8, 1, 0x10, 0),
        new("m_offset", "PUInt32", 0, 4, 0x10, 0),
        new("m_renderDataType", "PRenderDataType", 4, 4, 0x12, 0),
        new("m_streamSet", "PUInt8", 9, 1, 0x10, 0),
    }),
    new("PWorldMatrix", "PBase", 48, 4, 0, 0, 0, 0x0, 0, new MemberRow[]
    {
        new("m_matrix", "PMatrix4x3", 0, 48, 0x10, 0),
    }),
    new("Vector3", "-", 16, 4, 0, 0, 0, 0x20, 0, new MemberRow[]
    {
        new("m_elements", "float", 0, 4, 0x0, 4),
    }),
    new("Vector4", "-", 16, 4, 0, 0, 0, 0x20, 0, new MemberRow[]
    {
        new("m_elements", "float", 0, 4, 0x0, 4),
    }),
};

    private static readonly Dictionary<string, ClassRow> AssetProcessorByName =
        AssetProcessorRows.ToDictionary(value => value.Name, StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> AssetProcessorCanonicalTypes = new[]
    {
        "PUInt32",
        "PUInt8",
        "PSceneRenderPassType",
        "PUInt16",
        "PContextSwitch",
        "PLightType",
        "PShadowCasterType",
        "PInt32",
        "float",
        "PLODMetricType",
        "PLODBlendType",
        "bool",
        "PRenderDataType",
        "PChar",
        "PTextureFormatBase",
    };

    public static readonly IReadOnlyList<string> AssetProcessorCanonicalClasses = new[]
    {
        "PAssetReference",
        "PAssetReferenceImport",
        "PBase",
        "PClassDescriptor",
        "PClusterHeader",
        "PClusterHeaderD3D11",
        "PClusterHeaderBase",
        "PDataBlockD3D11",
        "PDataBlockBase",
        "PArray<PVertexStream>",
        "PInstanceListHeader",
        "PMaterial",
        "PEffectVariant",
        "PArray<PMaterialSwitch>",
        "PArray<PSceneRenderPass *>",
        "PArray<PSceneRenderPass>",
        "PArray<PShaderParameterDefinition>",
        "PEffect",
        "PArray<PContextSwitch *>",
        "PArray<PEffectVariant *>",
        "PArray<PLightType *>",
        "PArray<PNodeContext>",
        "PArray<PShadowCasterType *>",
        "PMaterialSwitch",
        "PMesh",
        "PArray<PInt32>",
        "PArray<PMatrix4>",
        "PArray<PMeshSegment>",
        "PArray<PSkeletonJointBounds>",
        "PArray<PString>",
        "PMaterialSet",
        "PMatrix4",
        "PMeshInstance",
        "PArray<PMeshInstanceSegmentContext>",
        "PArray<PUInt32>",
        "PDynamicMeshInstance",
        "PDynamicMesh",
        "PArray<PDynamicSegmentDesc>",
        "PDynamicSegmentDesc",
        "PLODLevel",
        "PLODGroup",
        "PArray<PLODLevel>",
        "PMeshInstanceBounds",
        "PMeshInstanceSegmentContext",
        "PMeshSegment",
        "PMeshSegmentContext",
        "PMeshSegmentD3D11",
        "PArray<PDataBlockD3D11>",
        "PIndexDataBlockD3D11",
        "PIndexDataBlock",
        "PIndexDataBlockBase",
        "PMeshSegmentBase",
        "PArray<PSkinBoneRemap>",
        "PNode",
        "PNodeContext",
        "PParameterBuffer",
        "PParameterBufferBase",
        "PSamplerStateD3D11",
        "PSamplerState",
        "PSamplerStateBase",
        "PSceneRenderPass",
        "PArray<PContextVariantFoldingTable>",
        "PArray<PShader>",
        "PArray<PShaderPassInfo>",
        "PContextVariantFoldingTable",
        "PShader",
        "PArray<PShaderPass>",
        "PArray<PShaderStreamDefinition>",
        "PShaderParameterCaptureBufferSampler",
        "PShaderParameterCaptureBufferTexture2D",
        "PShaderParameterCaptureBufferTextureBase",
        "PShaderParameterDefinition",
        "PShaderParameterCaptureBufferLocationSize",
        "PShaderParameterCaptureBufferLocation",
        "PShaderPass",
        "PShaderPassD3D11",
        "PShaderPassBase",
        "PArray<PShaderParameterCaptureBufferLocation>",
        "PShaderComputeProgram",
        "PShaderComputeProgramD3D11",
        "PShaderFragmentProgram",
        "PShaderFragmentProgramD3D11",
        "PShaderGeometryProgram",
        "PShaderGeometryProgramD3D11",
        "PShaderPassInfo",
        "PShaderPassParameterLocationTypesConstantBuffer",
        "PArray<PShaderParameterCaptureBufferLocationTypeConstantBuffer>",
        "PShaderParameterCaptureBufferLocationTypeConstantBuffer",
        "PShaderPassParameterLocationTypesBase",
        "PShaderPassStateD3D11",
        "CD3D11_BLEND_DESC",
        "CD3D11_DEPTH_STENCIL_DESC",
        "CD3D11_RASTERIZER_DESC",
        "D3D11_DEPTH_STENCILOP_DESC",
        "D3D11_RENDER_TARGET_BLEND_DESC",
        "PShaderPassStateBase",
        "PShaderProgramD3D11",
        "PArray<PUInt8>",
        "PShaderProgramBase",
        "PShaderStreamDefinition",
        "PShaderVertexProgram",
        "PShaderVertexProgramD3D11",
        "PSharray<PDataBlockBufferD3D11>",
        "PDataBlockBufferD3D11",
        "PSharray<PIndexDataBlockBufferD3D11>",
        "PIndexDataBlockBufferD3D11",
        "PSharray<PMaterial *>",
        "PSharray<PMeshInstance *>",
        "PSharray<PMeshInstanceSegmentStreamBinding *>",
        "PMeshInstanceSegmentStreamBinding",
        "PSharray<PUInt32>",
        "PSkeletonJointBounds",
        "PSkinBoneRemap",
        "PStreamInputLayoutD3D11",
        "PArray<PStreamInputDescD3D11>",
        "PStreamInputDescD3D11",
        "PString",
        "PTexture2D",
        "PTexture2DD3D11",
        "PTexture2DBase",
        "PTextureCommonBase",
        "PVertexStream",
        "PWorldMatrix",
        "PMatrix4x3",
        "Vector3",
        "Vector4",
    };
}


