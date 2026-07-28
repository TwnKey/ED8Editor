using System.Numerics;
using System.Runtime.InteropServices;
using ED8Editor.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ED8Editor.Rendering;

public sealed record ViewportCamera(Matrix4x4 View, Matrix4x4 Projection);

public sealed class D3D11Viewport : IDisposable
{
    private const int MaximumSkinBones = 256;
    private const string ShaderSource = """
        cbuffer PerDraw : register(b0)
        {
            row_major float4x4 WorldViewProjection;
            row_major float4x4 WorldInverseTranspose;
            float4 SelectionColor;
            float4 MaterialColor;
            float4 MaterialEmission;
            float4 LightDirectionAndAlphaThreshold;
            float4 AmbientColorAndAlphaTest;
            float4 DirectColorAndHasNormal;
            float4 EffectSettings;
            float4 MultiUvSettings;
            float4 MultiUvColor;
            float4 MultiUvTransform;
            float4 ViewportSize;
        };
        struct VSPosition { float3 Position : POSITION; };
        struct VSPositionNormal { float3 Position : POSITION; float4 Normal : NORMAL; };
        struct VSTextured { float3 Position : POSITION; float2 TexCoord : TEXCOORD0; };
        struct VSTexturedNormal { float3 Position : POSITION; float4 Normal : NORMAL; float2 TexCoord : TEXCOORD0; };
        struct VSTexturedColor { float3 Position : POSITION; float2 TexCoord : TEXCOORD0; float4 Color : COLOR0; };
        struct VSTexturedNormalColor { float3 Position : POSITION; float4 Normal : NORMAL; float2 TexCoord : TEXCOORD0; float4 Color : COLOR0; };
        struct VSTexturedNormalColorMultiUv { float3 Position : POSITION; float4 Normal : NORMAL; float2 TexCoord : TEXCOORD0; float2 TexCoord2 : TEXCOORD1; float4 Color : COLOR0; };
        struct VSDebug { float4 Position : POSITION; float4 Color : COLOR0; };
        struct VSEffect
        {
            float4 Position : POSITION;
            float4 Color : COLOR0;
            float4 Add : COLOR1;
            float2 TexCoord : TEXCOORD0;
        };
        #if SKINNED
        cbuffer Skinning : register(b1)
        {
            row_major float4x4 SkinMatrices[256];
        };
        struct VSSkinned
        {
            float3 Position : POSITION;
        #if HAS_NORMAL
            float4 Normal : NORMAL;
        #endif
        #if HAS_TEXTURE
            float2 TexCoord : TEXCOORD0;
        #endif
        #if HAS_MULTI_UV
            float2 TexCoord2 : TEXCOORD1;
        #endif
        #if HAS_COLOR
            float4 Color : COLOR0;
        #endif
            uint4 JointIndices : BLENDINDICES;
            float4 JointWeights : BLENDWEIGHT;
        };
        #endif
        struct PSInput { float4 Position : SV_Position; float3 Normal : NORMAL; float2 TexCoord : TEXCOORD0; float2 TexCoord2 : TEXCOORD1; float4 Color : COLOR0; };
        struct PSColoredInput { float4 Position : SV_Position; float4 Color : COLOR0; };
        struct MaterialOutput
        {
            float4 Scene : SV_Target0;
            float4 Glow : SV_Target1;
        };
        PSInput VSPositionMain(VSPosition input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = float3(0.0f, 1.0f, 0.0f);
            output.TexCoord = float2(0.0f, 0.0f);
            output.TexCoord2 = output.TexCoord;
            output.Color = float4(1.0f, 1.0f, 1.0f, 1.0f);
            return output;
        }
        PSInput VSPositionNormalMain(VSPositionNormal input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = normalize(mul(float4(input.Normal.xyz, 0.0f), WorldInverseTranspose).xyz);
            output.TexCoord = float2(0.0f, 0.0f);
            output.TexCoord2 = output.TexCoord;
            output.Color = float4(1.0f, 1.0f, 1.0f, 1.0f);
            return output;
        }
        PSInput VSTexturedMain(VSTextured input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = float3(0.0f, 1.0f, 0.0f);
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord;
            output.Color = float4(1.0f, 1.0f, 1.0f, 1.0f);
            return output;
        }
        PSInput VSTexturedNormalMain(VSTexturedNormal input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = normalize(mul(float4(input.Normal.xyz, 0.0f), WorldInverseTranspose).xyz);
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord;
            output.Color = float4(1.0f, 1.0f, 1.0f, 1.0f);
            return output;
        }
        PSInput VSTexturedColorMain(VSTexturedColor input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = float3(0.0f, 1.0f, 0.0f);
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord;
            output.Color = saturate(input.Color);
            return output;
        }
        PSInput VSTexturedNormalColorMain(VSTexturedNormalColor input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = normalize(mul(float4(input.Normal.xyz, 0.0f), WorldInverseTranspose).xyz);
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord;
            output.Color = saturate(input.Color);
            return output;
        }
        PSInput VSTexturedNormalColorMultiUvMain(VSTexturedNormalColorMultiUv input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Normal = normalize(mul(float4(input.Normal.xyz, 0.0f), WorldInverseTranspose).xyz);
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord2;
            output.Color = saturate(input.Color);
            return output;
        }
        #if SKINNED
        PSInput VSSkinnedMain(VSSkinned input)
        {
            float4 position = float4(0.0f, 0.0f, 0.0f, 0.0f);
            float3 normal = float3(0.0f, 0.0f, 0.0f);
            [unroll]
            for (uint influence = 0; influence < 4; influence++)
            {
                float weight = input.JointWeights[influence];
                position += mul(float4(input.Position, 1.0f), SkinMatrices[input.JointIndices[influence]]) * weight;
        #if HAS_NORMAL
                normal += mul(float4(input.Normal.xyz, 0.0f), SkinMatrices[input.JointIndices[influence]]).xyz * weight;
        #endif
            }
            PSInput output;
            output.Position = mul(position, WorldViewProjection);
        #if HAS_NORMAL
            output.Normal = normalize(mul(float4(normal, 0.0f), WorldInverseTranspose).xyz);
        #else
            output.Normal = float3(0.0f, 1.0f, 0.0f);
        #endif
        #if HAS_TEXTURE
            output.TexCoord = input.TexCoord;
        #else
            output.TexCoord = float2(0.0f, 0.0f);
        #endif
        #if HAS_MULTI_UV
            output.TexCoord2 = input.TexCoord2;
        #else
            output.TexCoord2 = output.TexCoord;
        #endif
        #if HAS_COLOR
            output.Color = saturate(input.Color);
        #else
            output.Color = float4(1.0f, 1.0f, 1.0f, 1.0f);
        #endif
            return output;
        }
        #endif
        PSColoredInput VSDebugMain(VSDebug input)
        {
            PSColoredInput output;
            output.Position = input.Position;
            output.Color = input.Color;
            return output;
        }
        Texture2D DiffuseTexture : register(t0);
        Texture2D DiffuseTexture2 : register(t1);
        SamplerState DiffuseSampler : register(s0);
        float4 ApplySelection(float4 color)
        {
            return lerp(color, float4(SelectionColor.rgb, color.a), SelectionColor.a);
        }
        float4 ApplyMaterial(PSInput input, float4 color)
        {
            color *= MaterialColor;
            color.a *= input.Color.a;
            if (AmbientColorAndAlphaTest.w > 0.5f)
            {
                clip(color.a - LightDirectionAndAlphaThreshold.w * input.Color.a);
            }
            if (DirectColorAndHasNormal.w > 0.5f)
            {
                float diffuse = saturate(dot(normalize(input.Normal), LightDirectionAndAlphaThreshold.xyz));
                color.rgb *= AmbientColorAndAlphaTest.rgb + DirectColorAndHasNormal.rgb * diffuse;
            }
            color.rgb *= input.Color.rgb;
            color.rgb += MaterialEmission.rgb;
            float sourceAlpha = color.a;
            if (EffectSettings.z < 0.5f)
            {
                color = ApplySelection(color);
            }
            if (EffectSettings.w > 0.5f)
            {
                // The authored ed8.fx returns resultColor directly for ALPHA_BLENDING_ENABLED.
            }
            else if (EffectSettings.x > 0.5f && EffectSettings.z < 0.5f)
            {
                float glowValue = max(dot(color.rgb, float3(1.0f, 1.0f, 1.0f)) - 1.0f, 0.0f);
                color.a = glowValue * EffectSettings.y * sourceAlpha;
            }
            else if (EffectSettings.z < 0.5f)
            {
                color.a = 0.0f;
            }
            return color;
        }
        MaterialOutput CreateMaterialOutput(float4 color)
        {
            MaterialOutput output;
            output.Scene = color;
            output.Glow = color;
            return output;
        }
        MaterialOutput PSSolidMain(PSInput input)
        {
            return CreateMaterialOutput(ApplyMaterial(input, float4(1.0f, 1.0f, 1.0f, 1.0f)));
        }
        MaterialOutput PSTexturedMain(PSInput input)
        {
            float4 color = DiffuseTexture.Sample(DiffuseSampler, input.TexCoord);
            if (MultiUvSettings.x > 0.5f)
            {
                float2 multiUv = input.TexCoord2 * MultiUvTransform.zw + MultiUvTransform.xy;
                float4 color2 = DiffuseTexture2.Sample(DiffuseSampler, multiUv) * MultiUvColor;
                float multiUvAlpha = input.Color.a * color2.a;
                if (MultiUvSettings.x < 1.5f)
                {
                    color.rgb = lerp(color.rgb, color2.rgb, multiUvAlpha);
                }
                else if (MultiUvSettings.x < 2.5f)
                {
                    color.rgb += color2.rgb * multiUvAlpha;
                }
                else if (MultiUvSettings.x < 3.5f)
                {
                    color.rgb += (color2.rgb - 1.0f) * color.rgb * multiUvAlpha;
                }
            }
            return CreateMaterialOutput(ApplyMaterial(input, color));
        }
        float4 PSColoredMain(PSColoredInput input) : SV_Target { return input.Color; }
        struct PSEffectInput
        {
            float4 Position : SV_Position;
            float4 Color : COLOR0;
            float4 Add : COLOR1;
            float2 TexCoord : TEXCOORD0;
        };
        PSEffectInput VSEffectMain(VSEffect input)
        {
            PSEffectInput output;
            output.Position = input.Position;
            output.Color = input.Color;
            output.Add = input.Add;
            output.TexCoord = input.TexCoord;
            return output;
        }
        // An effect segment samples its texture, multiplies it by its colour
        // track and adds its glow track on top. The engine works in premultiplied
        // alpha: the multiply term is occluded by its own alpha, while the added
        // glow is gated by the texture alone, so a segment whose colour has gone
        // fully transparent still shows its glow.
        float4 PSEffectMain(PSEffectInput input) : SV_Target
        {
            float4 texel = DiffuseTexture.Sample(DiffuseSampler, input.TexCoord);
            float3 multiplied = texel.rgb * texel.a * input.Color.rgb * input.Color.a;
            float3 added = input.Add.rgb * texel.a;
            return float4(multiplied + added, texel.a * input.Color.a);
        }
        """;

    private readonly D3D11GraphicsDevice graphics;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11VertexShader positionVertexShader;
    private readonly ID3D11VertexShader positionNormalVertexShader;
    private readonly ID3D11VertexShader texturedVertexShader;
    private readonly ID3D11VertexShader texturedNormalVertexShader;
    private readonly ID3D11VertexShader texturedColorVertexShader;
    private readonly ID3D11VertexShader texturedNormalColorVertexShader;
    private readonly ID3D11VertexShader texturedNormalColorMultiUvVertexShader;
    private readonly ID3D11VertexShader coloredVertexShader;
    private readonly ID3D11PixelShader solidPixelShader;
    private readonly ID3D11PixelShader texturedPixelShader;
    private readonly ID3D11PixelShader coloredPixelShader;
    private SceneEnvironmentVariant environmentVariant = SceneEnvironmentVariant.Daylight;
    private readonly byte[] positionVertexBytecode;
    private readonly byte[] positionNormalVertexBytecode;
    private readonly byte[] texturedVertexBytecode;
    private readonly byte[] texturedNormalVertexBytecode;
    private readonly byte[] texturedColorVertexBytecode;
    private readonly byte[] texturedNormalColorVertexBytecode;
    private readonly byte[] texturedNormalColorMultiUvVertexBytecode;
    private readonly ID3D11InputLayout coloredInputLayout;
    private readonly ID3D11VertexShader effectVertexShader;
    private readonly ID3D11PixelShader effectPixelShader;
    private readonly ID3D11InputLayout effectInputLayout;
    private readonly ID3D11BlendState effectAlphaBlendState;
    private readonly ID3D11BlendState effectAdditiveBlendState;
    private readonly ID3D11BlendState effectSubtractiveBlendState;
    private readonly ID3D11DepthStencilState effectDepthState;
    private ID3D11Buffer? effectQuadBuffer;
    private int effectQuadVertexCapacity;
    private IReadOnlyList<D3D11EffectQuad> effectQuads = Array.Empty<D3D11EffectQuad>();
    private readonly ID3D11Buffer perDrawBuffer;
    private readonly ID3D11Buffer skinningBuffer;
    private readonly ID3D11SamplerState sampler;
    private readonly D3D11BloomPipeline bloomPipeline;
    private readonly Dictionary<CpuRenderPassState, ID3D11BlendState> blendStates = new();
    private readonly Dictionary<CpuRasterizerState, ID3D11RasterizerState> rasterizerStates = new();
    private readonly ID3D11RasterizerState rasterizer;
    private readonly Dictionary<string, ID3D11InputLayout> inputLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<int, SkinnedVertexProgram> skinnedPrograms = new();
    private readonly Matrix4x4[] skinningConstants = new Matrix4x4[MaximumSkinBones];
    private ID3D11RenderTargetView? renderTarget;
    private ID3D11Texture2D? depthTexture;
    private ID3D11DepthStencilView? depthView;
    private ID3D11Buffer? debugLineBuffer;
    private ID3D11Buffer? debugTriangleBuffer;
    private readonly ID3D11BlendState overlayBlendState;
    private IReadOnlyList<D3D11DebugLine> debugLines = Array.Empty<D3D11DebugLine>();
    private IReadOnlyList<D3D11DebugTriangle> debugTriangles = Array.Empty<D3D11DebugTriangle>();
    private int debugLineVertexCapacity;
    private int debugLineVertexCount;
    private int debugTriangleVertexCapacity;
    private int width;
    private int height;
    private Vector4 clearColor = new(0.035f, 0.045f, 0.065f, 1f);
    private ViewportLighting lighting = ViewportLighting.Neutral;

    public D3D11Viewport(D3D11GraphicsDevice graphics, IntPtr windowHandle, int width, int height)
    {
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));

        using var dxgiDevice = graphics.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };
        swapChain = factory.CreateSwapChainForHwnd(graphics.Device, windowHandle, description);
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        positionVertexBytecode = Compile("VSPositionMain", "vs_5_0");
        positionNormalVertexBytecode = Compile("VSPositionNormalMain", "vs_5_0");
        texturedVertexBytecode = Compile("VSTexturedMain", "vs_5_0");
        texturedNormalVertexBytecode = Compile("VSTexturedNormalMain", "vs_5_0");
        texturedColorVertexBytecode = Compile("VSTexturedColorMain", "vs_5_0");
        texturedNormalColorVertexBytecode = Compile("VSTexturedNormalColorMain", "vs_5_0");
        texturedNormalColorMultiUvVertexBytecode = Compile("VSTexturedNormalColorMultiUvMain", "vs_5_0");
        var coloredVertexBytecode = Compile("VSDebugMain", "vs_5_0");
        positionVertexShader = graphics.Device.CreateVertexShader(positionVertexBytecode);
        positionNormalVertexShader = graphics.Device.CreateVertexShader(positionNormalVertexBytecode);
        texturedVertexShader = graphics.Device.CreateVertexShader(texturedVertexBytecode);
        texturedNormalVertexShader = graphics.Device.CreateVertexShader(texturedNormalVertexBytecode);
        texturedColorVertexShader = graphics.Device.CreateVertexShader(texturedColorVertexBytecode);
        texturedNormalColorVertexShader = graphics.Device.CreateVertexShader(texturedNormalColorVertexBytecode);
        texturedNormalColorMultiUvVertexShader = graphics.Device.CreateVertexShader(texturedNormalColorMultiUvVertexBytecode);
        coloredVertexShader = graphics.Device.CreateVertexShader(coloredVertexBytecode);
        solidPixelShader = graphics.Device.CreatePixelShader(Compile("PSSolidMain", "ps_5_0"));
        texturedPixelShader = graphics.Device.CreatePixelShader(Compile("PSTexturedMain", "ps_5_0"));
        coloredPixelShader = graphics.Device.CreatePixelShader(Compile("PSColoredMain", "ps_5_0"));
        coloredInputLayout = graphics.Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
        }, coloredVertexBytecode);
        var effectVertexBytecode = Compile("VSEffectMain", "vs_5_0");
        effectVertexShader = graphics.Device.CreateVertexShader(effectVertexBytecode);
        effectPixelShader = graphics.Device.CreatePixelShader(Compile("PSEffectMain", "ps_5_0"));
        effectInputLayout = graphics.Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
            new InputElementDescription("COLOR", 1, Format.R32G32B32A32_Float, 32, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 48, 0),
        }, effectVertexBytecode);
        // The three ways an effect segment is blended, from its own blend byte:
        // premultiplied alpha over the scene, additive, and subtractive.
        effectAlphaBlendState = CreateEffectBlendState(
            graphics, Blend.One, Blend.InverseSourceAlpha, BlendOperation.Add);
        effectAdditiveBlendState = CreateEffectBlendState(
            graphics, Blend.One, Blend.One, BlendOperation.Add);
        effectSubtractiveBlendState = CreateEffectBlendState(
            graphics, Blend.One, Blend.One, BlendOperation.ReverseSubtract);
        // Effects read the depth buffer so the scene occludes them, but they do
        // not write to it: they never occlude each other.
        effectDepthState = graphics.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
        });
        perDrawBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            Marshal.SizeOf<PerDrawConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
        skinningBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            MaximumSkinBones * Marshal.SizeOf<Matrix4x4>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
        Array.Fill(skinningConstants, Matrix4x4.Identity);
        // Compile the common textured/normal skinning path during renderer initialization so
        // malformed animation shaders fail deterministically before the first animated draw.
        GetSkinnedProgram(1 | 2);
        sampler = graphics.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        rasterizer = graphics.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });
        overlayBlendState = graphics.Device.CreateBlendState(new BlendDescription
        {
            RenderTarget =
            {
                [0] = new RenderTargetBlendDescription
                {
                    BlendEnable = true,
                    SourceBlend = Blend.SourceAlpha,
                    DestinationBlend = Blend.InverseSourceAlpha,
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.InverseSourceAlpha,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                },
            },
        });
        bloomPipeline = new D3D11BloomPipeline(graphics);
        Resize(width, height);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (this.width == width && this.height == height && renderTarget is not null) return;

        ReleaseTargets();
        swapChain.ResizeBuffers(0, width, height, Format.Unknown, SwapChainFlags.None).CheckError();
        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = graphics.Device.CreateRenderTargetView(backBuffer);
        var depthDescription = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        };
        depthTexture = graphics.Device.CreateTexture2D(depthDescription);
        depthView = graphics.Device.CreateDepthStencilView(depthTexture);
        bloomPipeline.Resize(width, height);
        this.width = width;
        this.height = height;
    }

    public void SetDebugLines(IReadOnlyList<D3D11DebugLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (var index = 0; index < lines.Count; index++)
        {
            if (!float.IsFinite(lines[index].Thickness) || lines[index].Thickness <= 0f)
                throw new ArgumentOutOfRangeException(nameof(lines), "Debug line thickness must be finite and positive.");
        }
        debugLines = lines.ToArray();
    }

    private static ID3D11BlendState CreateEffectBlendState(
        D3D11GraphicsDevice graphics,
        Blend source,
        Blend destination,
        BlendOperation operation)
        => graphics.Device.CreateBlendState(new BlendDescription
        {
            RenderTarget =
            {
                [0] = new RenderTargetBlendDescription
                {
                    BlendEnable = true,
                    SourceBlend = source,
                    DestinationBlend = destination,
                    BlendOperation = operation,
                    SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.InverseSourceAlpha,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                },
            },
        });

    /// <summary>The textured quads of the effects currently playing.</summary>
    public void SetEffectQuads(IReadOnlyList<D3D11EffectQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        effectQuads = quads.ToArray();
    }

    public void SetDebugTriangles(IReadOnlyList<D3D11DebugTriangle> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        debugTriangles = triangles.ToArray();
    }

    public void SetClearColor(Vector4 color)
    {
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y)
            || !float.IsFinite(color.Z) || !float.IsFinite(color.W))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }
        clearColor = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
    }

    public void SetLighting(ViewportLighting value)
        => lighting = value ?? throw new ArgumentNullException(nameof(value));

    public void SetEnvironmentVariant(SceneEnvironmentVariant value)
        => environmentVariant = value;

    public void Render(IReadOnlyList<D3D11SceneInstance> instances, ViewportCamera camera, bool verticalSync = true)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (renderTarget is null || depthView is null
            || bloomPipeline.SceneRenderTarget is null
            || bloomPipeline.GlowSourceRenderTarget is null) return;

        var context = graphics.Context;
        context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        context.OMSetDepthStencilState(null);
        context.OMSetRenderTargets(bloomPipeline.SceneRenderTarget, depthView);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.RSSetState(rasterizer);
        context.ClearRenderTargetView(
            bloomPipeline.SceneRenderTarget,
            new Color4(clearColor.X, clearColor.Y, clearColor.Z, 0f));
        context.ClearRenderTargetView(
            bloomPipeline.GlowSourceRenderTarget,
            new Color4(0f, 0f, 0f, 0f));
        context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);
        context.VSSetConstantBuffer(0, perDrawBuffer);
        context.PSSetConstantBuffer(0, perDrawBuffer);
        context.PSSetSampler(0, sampler);

        DrawScenePhase(instances, camera, CpuMaterialRenderPhase.Opaque);
        context.OMSetRenderTargets(
            new[] { bloomPipeline.SceneRenderTarget, bloomPipeline.GlowSourceRenderTarget },
            depthView);
        DrawScenePhase(instances, camera, CpuMaterialRenderPhase.EffectTransparent);

        context.ClearRenderTargetView(
            renderTarget,
            new Color4(clearColor.X, clearColor.Y, clearColor.Z, 1f));
        bloomPipeline.Composite(renderTarget);
        context.OMSetRenderTargets(renderTarget, depthView);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.RSSetState(rasterizer);
        context.VSSetConstantBuffer(0, perDrawBuffer);
        context.PSSetConstantBuffer(0, perDrawBuffer);
        context.PSSetSampler(0, sampler);
        DrawScenePhase(instances, camera, CpuMaterialRenderPhase.Transparent);
        DrawEffectQuads(camera);
        context.OMSetBlendState(overlayBlendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        DrawDebugTriangles(camera);
        context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        DrawDebugLines(camera);

        swapChain.Present(verticalSync ? 1 : 0, PresentFlags.None).CheckError();
    }

    public byte[] CaptureBackBufferBgra()
    {
        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var source = backBuffer.Description;
        var stagingDescription = new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = graphics.Device.CreateTexture2D(stagingDescription);
        graphics.Context.CopyResource(staging, backBuffer);
        var mapped = graphics.Context.Map(
            staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            const int bytesPerPixel = 4;
            var rowBytes = checked(source.Width * bytesPerPixel);
            var pixels = new byte[checked(rowBytes * source.Height)];
            for (var row = 0; row < source.Height; row++)
            {
                Marshal.Copy(
                    mapped.DataPointer + row * mapped.RowPitch,
                    pixels,
                    row * rowBytes,
                    rowBytes);
            }
            return pixels;
        }
        finally
        {
            graphics.Context.Unmap(staging, 0);
        }
    }

    private void DrawScenePhase(
        IReadOnlyList<D3D11SceneInstance> instances,
        ViewportCamera camera,
        CpuMaterialRenderPhase phase)
    {
        foreach (var instance in instances)
        {
            foreach (var mesh in instance.Model.Meshes)
            {
                if (!SceneEnvironmentVariantSelector.IsVisible(mesh.Name, environmentVariant)) continue;
                var meshTransform = instance.SceneNodeTransforms is { } animatedNodes
                    && (uint)mesh.SceneNodeIndex < animatedNodes.Count
                        ? animatedNodes[mesh.SceneNodeIndex]
                        : mesh.LocalTransform;
                var world = meshTransform * instance.Transform;
                var matrix = world * camera.View * camera.Projection;
                foreach (var primitive in mesh.Primitives)
                {
                    var materialPhase = primitive.MaterialIndex >= 0
                        && primitive.MaterialIndex < instance.Model.Materials.Count
                            ? instance.Model.Materials[primitive.MaterialIndex].Source.RenderPhase
                            : CpuMaterialRenderPhase.Opaque;
                    if (materialPhase != phase) continue;
                    DrawPrimitive(
                        instance.Model, primitive, world, matrix,
                        instance.IsSelected, instance.IsPreview,
                        instance.MaterialDiffuse, instance.MaterialEmission,
                        instance.SkinMatrices,
                        instance.TexturesByGameMaterialId);
                }
            }
        }
    }

    public void Dispose()
    {
        graphics.Context.ClearState();
        ReleaseTargets();
        bloomPipeline.Dispose();
        foreach (var layout in inputLayouts.Values) layout.Dispose();
        debugLineBuffer?.Dispose();
        debugTriangleBuffer?.Dispose();
        effectQuadBuffer?.Dispose();
        effectInputLayout.Dispose();
        effectVertexShader.Dispose();
        effectPixelShader.Dispose();
        effectAlphaBlendState.Dispose();
        effectAdditiveBlendState.Dispose();
        effectSubtractiveBlendState.Dispose();
        effectDepthState.Dispose();
        overlayBlendState.Dispose();
        coloredInputLayout.Dispose();
        rasterizer.Dispose();
        sampler.Dispose();
        foreach (var state in blendStates.Values) state.Dispose();
        foreach (var state in rasterizerStates.Values) state.Dispose();
        perDrawBuffer.Dispose();
        skinningBuffer.Dispose();
        foreach (var program in skinnedPrograms.Values) program.Shader.Dispose();
        texturedPixelShader.Dispose();
        coloredPixelShader.Dispose();
        solidPixelShader.Dispose();
        texturedNormalVertexShader.Dispose();
        texturedNormalColorVertexShader.Dispose();
        texturedNormalColorMultiUvVertexShader.Dispose();
        texturedColorVertexShader.Dispose();
        texturedVertexShader.Dispose();
        positionNormalVertexShader.Dispose();
        coloredVertexShader.Dispose();
        positionVertexShader.Dispose();
        swapChain.Dispose();
    }

    private unsafe void DrawDebugLines(ViewportCamera camera)
    {
        if (debugLines.Count == 0) return;
        var vertices = BuildDebugLineTriangles(camera);
        debugLineVertexCount = vertices.Length;
        if (debugLineVertexCount == 0) return;
        EnsureDebugLineBuffer(debugLineVertexCount);
        var mapped = graphics.Context.Map(debugLineBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        fixed (DebugLineVertex* source = vertices)
        {
            Buffer.MemoryCopy(
                source,
                (void*)mapped.DataPointer,
                (long)debugLineVertexCapacity * Marshal.SizeOf<DebugLineVertex>(),
                (long)debugLineVertexCount * Marshal.SizeOf<DebugLineVertex>());
        }
        graphics.Context.Unmap(debugLineBuffer!, 0);
        var context = graphics.Context;
        context.RSSetState(rasterizer);
        context.IASetInputLayout(coloredInputLayout);
        context.IASetVertexBuffer(0, debugLineBuffer!, Marshal.SizeOf<DebugLineVertex>(), 0);
        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        context.VSSetShader(coloredVertexShader);
        context.PSSetShader(coloredPixelShader);
        context.PSSetShaderResource(0, null!);
        context.Draw(debugLineVertexCount, 0);
    }

    /// <summary>
    /// Draws the effect quads, back to front within each priority the segments
    /// carry, one draw per texture and blend mode. They read the scene depth so
    /// the map occludes them, but they never write to it.
    /// </summary>
    private unsafe void DrawEffectQuads(ViewportCamera camera)
    {
        if (effectQuads.Count == 0) return;
        var matrix = camera.View * camera.Projection;
        Matrix4x4.Invert(camera.View, out var inverseView);
        var eye = inverseView.Translation;
        var ordered = effectQuads
            .OrderBy(quad => quad.Priority)
            .ThenByDescending(quad => Vector3.DistanceSquared(eye, quad.Center))
            .ToArray();

        var vertices = new List<EffectVertex>(ordered.Length * 6);
        var batches = new List<EffectBatch>();
        foreach (var quad in ordered)
        {
            if (quad.Texture is null) continue;
            var corners = new[] { quad.CornerA, quad.CornerB, quad.CornerC, quad.CornerD };
            var uvs = new[]
            {
                new Vector2(quad.UvMinimum.X, quad.UvMinimum.Y),
                new Vector2(quad.UvMaximum.X, quad.UvMinimum.Y),
                new Vector2(quad.UvMaximum.X, quad.UvMaximum.Y),
                new Vector2(quad.UvMinimum.X, quad.UvMaximum.Y),
            };
            var projected = new Vector4[4];
            var visible = true;
            for (var index = 0; index < 4; index++)
            {
                projected[index] = Vector4.Transform(new Vector4(corners[index], 1f), matrix);
                if (projected[index].W <= 0f) visible = false;
            }
            if (!visible) continue;
            var start = vertices.Count;
            foreach (var index in new[] { 0, 1, 2, 0, 2, 3 })
            {
                vertices.Add(new EffectVertex(projected[index], quad.Color, quad.Add, uvs[index]));
            }
            // Quads that follow each other with the same texture and blend mode
            // are one draw.
            if (batches.Count > 0
                && batches[^1].Texture == quad.Texture
                && batches[^1].Blend == quad.Blend
                && batches[^1].Start + batches[^1].Count == start)
            {
                batches[^1] = batches[^1] with { Count = batches[^1].Count + 6 };
                continue;
            }
            batches.Add(new EffectBatch(start, 6, quad.Texture, quad.Blend));
        }
        if (vertices.Count == 0) return;

        EnsureEffectQuadBuffer(vertices.Count);
        var mapped = graphics.Context.Map(
            effectQuadBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var data = CollectionsMarshal.AsSpan(vertices);
        fixed (EffectVertex* source = data)
        {
            Buffer.MemoryCopy(source, (void*)mapped.DataPointer,
                (long)effectQuadVertexCapacity * Marshal.SizeOf<EffectVertex>(),
                (long)data.Length * Marshal.SizeOf<EffectVertex>());
        }
        graphics.Context.Unmap(effectQuadBuffer!, 0);

        var context = graphics.Context;
        context.RSSetState(rasterizer);
        context.OMSetDepthStencilState(effectDepthState);
        context.IASetInputLayout(effectInputLayout);
        context.IASetVertexBuffer(0, effectQuadBuffer!, Marshal.SizeOf<EffectVertex>(), 0);
        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        context.VSSetShader(effectVertexShader);
        context.PSSetShader(effectPixelShader);
        context.PSSetSampler(0, sampler);
        foreach (var batch in batches)
        {
            context.OMSetBlendState(
                batch.Blend switch
                {
                    EffBlendMode.Additive => effectAdditiveBlendState,
                    EffBlendMode.Subtractive => effectSubtractiveBlendState,
                    _ => effectAlphaBlendState,
                },
                new Color4(0f, 0f, 0f, 0f),
                uint.MaxValue);
            context.PSSetShaderResource(0, batch.Texture);
            context.Draw(batch.Count, batch.Start);
        }
        context.PSSetShaderResource(0, null!);
        context.OMSetDepthStencilState(null);
    }

    private void EnsureEffectQuadBuffer(int requiredVertexCount)
    {
        if (effectQuadBuffer is not null && effectQuadVertexCapacity >= requiredVertexCount) return;
        effectQuadBuffer?.Dispose();
        effectQuadVertexCapacity = Math.Max(requiredVertexCount, 256);
        effectQuadBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            effectQuadVertexCapacity * Marshal.SizeOf<EffectVertex>(),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private readonly record struct EffectBatch(
        int Start,
        int Count,
        ID3D11ShaderResourceView Texture,
        EffBlendMode Blend);

    private unsafe void DrawDebugTriangles(ViewportCamera camera)
    {
        if (debugTriangles.Count == 0) return;
        var matrix = camera.View * camera.Projection;
        var vertices = new List<DebugLineVertex>(debugTriangles.Count * 3);
        foreach (var triangle in debugTriangles)
        {
            var a = Vector4.Transform(new Vector4(triangle.A, 1f), matrix);
            var b = Vector4.Transform(new Vector4(triangle.B, 1f), matrix);
            var c = Vector4.Transform(new Vector4(triangle.C, 1f), matrix);
            if (a.W <= 0f || b.W <= 0f || c.W <= 0f) continue;
            vertices.Add(new DebugLineVertex(a, triangle.Color));
            vertices.Add(new DebugLineVertex(b, triangle.Color));
            vertices.Add(new DebugLineVertex(c, triangle.Color));
        }
        if (vertices.Count == 0) return;
        EnsureDebugTriangleBuffer(vertices.Count);
        var mapped = graphics.Context.Map(debugTriangleBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var data = CollectionsMarshal.AsSpan(vertices);
        fixed (DebugLineVertex* source = data)
        {
            Buffer.MemoryCopy(source, (void*)mapped.DataPointer,
                (long)debugTriangleVertexCapacity * Marshal.SizeOf<DebugLineVertex>(),
                (long)data.Length * Marshal.SizeOf<DebugLineVertex>());
        }
        graphics.Context.Unmap(debugTriangleBuffer!, 0);
        var context = graphics.Context;
        context.RSSetState(rasterizer);
        context.IASetInputLayout(coloredInputLayout);
        context.IASetVertexBuffer(0, debugTriangleBuffer!, Marshal.SizeOf<DebugLineVertex>(), 0);
        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        context.VSSetShader(coloredVertexShader);
        context.PSSetShader(coloredPixelShader);
        context.PSSetShaderResource(0, null!);
        context.Draw(data.Length, 0);
    }

    private DebugLineVertex[] BuildDebugLineTriangles(ViewportCamera camera)
    {
        var matrix = camera.View * camera.Projection;
        var vertices = new List<DebugLineVertex>(checked(debugLines.Count * 6));
        foreach (var line in debugLines)
        {
            var first = Vector4.Transform(new Vector4(line.Start, 1f), matrix);
            var second = Vector4.Transform(new Vector4(line.End, 1f), matrix);
            if (first.W <= 0f || second.W <= 0f) continue;
            var firstNdc = new Vector2(first.X, first.Y) / first.W;
            var secondNdc = new Vector2(second.X, second.Y) / second.W;
            var screenDirection = (secondNdc - firstNdc) * new Vector2(width, height);
            if (screenDirection.LengthSquared() <= 0.0001f) continue;
            var perpendicular = Vector2.Normalize(new Vector2(-screenDirection.Y, screenDirection.X));
            var offsetNdc = perpendicular * new Vector2(line.Thickness / width, line.Thickness / height);
            var firstPositive = OffsetClip(first, offsetNdc);
            var firstNegative = OffsetClip(first, -offsetNdc);
            var secondPositive = OffsetClip(second, offsetNdc);
            var secondNegative = OffsetClip(second, -offsetNdc);
            vertices.Add(new DebugLineVertex(firstPositive, line.Color));
            vertices.Add(new DebugLineVertex(firstNegative, line.Color));
            vertices.Add(new DebugLineVertex(secondPositive, line.Color));
            vertices.Add(new DebugLineVertex(secondPositive, line.Color));
            vertices.Add(new DebugLineVertex(firstNegative, line.Color));
            vertices.Add(new DebugLineVertex(secondNegative, line.Color));
        }
        return vertices.ToArray();

        static Vector4 OffsetClip(Vector4 position, Vector2 offsetNdc)
            => position with
            {
                X = position.X + offsetNdc.X * position.W,
                Y = position.Y + offsetNdc.Y * position.W,
            };
    }

    private void EnsureDebugLineBuffer(int requiredVertexCount)
    {
        if (debugLineBuffer is not null && debugLineVertexCapacity >= requiredVertexCount) return;
        debugLineBuffer?.Dispose();
        debugLineVertexCapacity = Math.Max(requiredVertexCount, Math.Max(256, debugLineVertexCapacity * 2));
        debugLineBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            checked(debugLineVertexCapacity * Marshal.SizeOf<DebugLineVertex>()),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private void EnsureDebugTriangleBuffer(int requiredVertexCount)
    {
        if (debugTriangleBuffer is not null && debugTriangleVertexCapacity >= requiredVertexCount) return;
        debugTriangleBuffer?.Dispose();
        debugTriangleVertexCapacity = Math.Max(requiredVertexCount, Math.Max(256, debugTriangleVertexCapacity * 2));
        debugTriangleBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            checked(debugTriangleVertexCapacity * Marshal.SizeOf<DebugLineVertex>()),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private void DrawPrimitive(
        D3D11ModelResources model,
        D3D11PrimitiveResources primitive,
        Matrix4x4 world,
        Matrix4x4 worldViewProjection,
        bool selected,
        bool preview,
        Vector4 materialDiffuse,
        Vector3 materialEmission,
        IReadOnlyList<Matrix4x4>? skinMatrices,
        IReadOnlyDictionary<int, D3D11MaterialTextureOverride>? texturesByGameMaterialId)
    {
        var positionBuffer = FindBuffer(primitive, VertexSemantic.Position);
        if (positionBuffer is null || !TryMapTopology(primitive.Topology, out var topology)) return;
        var positionAttribute = positionBuffer.Attributes.First(value => value.Semantic == VertexSemantic.Position);
        if (!TryMapFormat(positionAttribute.SourceFormat, out var positionFormat)) return;

        var textureView = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < model.Materials.Count
            && model.Materials[primitive.MaterialIndex].Source.BaseColorTextureIndex is { } textureIndex
            && (uint)textureIndex < model.Textures.Count
                ? model.Textures[textureIndex].ShaderResourceView
                : null;
        var textureBuffer = FindBuffer(primitive, VertexSemantic.TextureCoordinate);
        var textureAttribute = textureBuffer?.Attributes.First(value => value.Semantic == VertexSemantic.TextureCoordinate);
        var textureFormat = Format.Unknown;
        var textured = textureView is not null && textureBuffer is not null && textureAttribute is not null
            && TryMapFormat(textureAttribute.SourceFormat, out textureFormat);
        var normalBuffer = FindBuffer(primitive, VertexSemantic.Normal);
        var normalAttribute = normalBuffer?.Attributes.First(value => value.Semantic == VertexSemantic.Normal);
        var normalFormat = Format.Unknown;
        var hasNormal = normalBuffer is not null && normalAttribute is not null
            && TryMapFormat(normalAttribute.SourceFormat, out normalFormat);
        var material = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < model.Materials.Count
            ? model.Materials[primitive.MaterialIndex]
            : null;
        D3D11MaterialTextureOverride? textureOverride = null;
        if (material is not null
            && TryReadGameMaterialId(material.Source, out var gameMaterialId))
        {
            texturesByGameMaterialId?.TryGetValue(gameMaterialId, out textureOverride);
        }
        textureView = textureOverride?.DiffuseTexture ?? textureView;
        var materialSettings = ViewportMaterialSettings.FromMaterial(material?.Source);
        var multiUvTextureView = material is not null
            && material.TextureBindings.TryGetValue("DiffuseMap2Sampler", out var boundMultiUvTexture)
                ? boundMultiUvTexture
                : null;
        multiUvTextureView = textureOverride?.DiffuseTexture2 ?? multiUvTextureView;
        var multiUvBuffer = FindBuffer(primitive, VertexSemantic.TextureCoordinate, 1);
        var multiUvAttribute = multiUvBuffer?.Attributes.First(value =>
            value.Semantic == VertexSemantic.TextureCoordinate && value.SemanticIndex == 1);
        var multiUvFormat = Format.Unknown;
        var facialMultiUvOverride = textureOverride?.DiffuseTexture2 is not null;
        var hasMultiUv = (materialSettings.MultiUvBlendMode != ViewportMultiUvBlendMode.Disabled
                || facialMultiUvOverride)
            && multiUvTextureView is not null
            && multiUvBuffer is not null
            && multiUvAttribute is not null
            && TryMapFormat(multiUvAttribute.SourceFormat, out multiUvFormat);
        var colorBuffer = materialSettings.VertexColorEnabled
            ? FindBuffer(primitive, VertexSemantic.Color)
            : null;
        var colorAttribute = colorBuffer?.Attributes.First(value => value.Semantic == VertexSemantic.Color);
        var colorFormat = Format.Unknown;
        var hasVertexColor = colorBuffer is not null && colorAttribute is not null
            && TryMapFormat(colorAttribute.SourceFormat, out colorFormat);
        var jointIndexBuffer = FindBuffer(primitive, VertexSemantic.JointIndices);
        var jointIndexAttribute = jointIndexBuffer?.Attributes.First(value => value.Semantic == VertexSemantic.JointIndices);
        var jointIndexFormat = Format.Unknown;
        var jointWeightBuffer = FindBuffer(primitive, VertexSemantic.JointWeights);
        var jointWeightAttribute = jointWeightBuffer?.Attributes.First(value => value.Semantic == VertexSemantic.JointWeights);
        var jointWeightFormat = Format.Unknown;
        var skinned = skinMatrices is not null && primitive.SkinBones is { Count: > 0 }
            && jointIndexBuffer is not null && jointIndexAttribute is not null
            && jointWeightBuffer is not null && jointWeightAttribute is not null
            && TryMapFormat(jointIndexAttribute.SourceFormat, out jointIndexFormat)
            && TryMapFormat(jointWeightAttribute.SourceFormat, out jointWeightFormat);
        var normalMatrix = Matrix4x4.Identity;
        if (hasNormal && Matrix4x4.Invert(world, out var inverseWorld))
        {
            normalMatrix = Matrix4x4.Transpose(inverseWorld);
        }
        UpdateConstants(
            worldViewProjection, normalMatrix, material, hasNormal, selected, preview,
            materialDiffuse, materialEmission, facialMultiUvOverride);

        var context = graphics.Context;
        context.OMSetBlendState(
            material?.Source.RenderPassState is { } passState ? GetBlendState(passState) : null,
            new Color4(0f, 0f, 0f, 0f),
            uint.MaxValue);
        context.RSSetState(material?.Source.RenderPassState?.RasterizerState is { } rasterizerState
            ? GetRasterizerState(rasterizerState)
            : rasterizer);
        context.PSSetShaderResource(1, hasMultiUv ? multiUvTextureView! : null!);
        if (skinned)
        {
            UpdateSkinConstants(primitive.SkinBones!, skinMatrices!);
            var features = (hasNormal ? 1 : 0) | (textured ? 2 : 0)
                | (hasVertexColor ? 4 : 0) | (hasMultiUv ? 8 : 0);
            var program = GetSkinnedProgram(features);
            var buffers = new List<D3D11VertexBufferResource> { positionBuffer };
            var elements = new List<InputElementDescription>
            {
                new("POSITION", 0, positionFormat, positionAttribute.Offset, 0),
            };
            if (hasNormal) AddSkinnedInput("NORMAL", 0, normalFormat, normalAttribute!.Offset, normalBuffer!, buffers, elements);
            if (textured) AddSkinnedInput("TEXCOORD", 0, textureFormat, textureAttribute!.Offset, textureBuffer!, buffers, elements);
            if (hasMultiUv) AddSkinnedInput("TEXCOORD", 1, multiUvFormat, multiUvAttribute!.Offset, multiUvBuffer!, buffers, elements);
            if (hasVertexColor) AddSkinnedInput("COLOR", 0, colorFormat, colorAttribute!.Offset, colorBuffer!, buffers, elements);
            AddSkinnedInput("BLENDINDICES", 0, jointIndexFormat, jointIndexAttribute!.Offset, jointIndexBuffer!, buffers, elements);
            AddSkinnedInput("BLENDWEIGHT", 0, jointWeightFormat, jointWeightAttribute!.Offset, jointWeightBuffer!, buffers, elements);
            var layoutKey = "SK:" + features + ":" + string.Join(";", elements.Select(value =>
                $"{value.SemanticName}{value.SemanticIndex}:{value.Format}:{value.AlignedByteOffset}:{value.Slot}"));
            if (!inputLayouts.TryGetValue(layoutKey, out var layout))
            {
                layout = graphics.Device.CreateInputLayout(elements.ToArray(), program.Bytecode);
                inputLayouts.Add(layoutKey, layout);
            }
            context.IASetInputLayout(layout);
            context.IASetVertexBuffers(0, buffers.Select(value => value.Buffer).ToArray(),
                buffers.Select(value => value.Stride).ToArray(), new int[buffers.Count]);
            context.VSSetShader(program.Shader);
            context.VSSetConstantBuffer(1, skinningBuffer);
            context.PSSetShader(textured ? texturedPixelShader : solidPixelShader);
            context.PSSetShaderResource(0, textured ? textureView! : null!);
        }
        else if (textured && hasNormal && hasVertexColor && hasMultiUv)
        {
            context.IASetInputLayout(GetTexturedNormalColorMultiUvLayout(
                positionFormat, positionAttribute.Offset,
                normalFormat, normalAttribute!.Offset,
                textureFormat, textureAttribute!.Offset,
                multiUvFormat, multiUvAttribute!.Offset,
                colorFormat, colorAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, normalBuffer!.Buffer, textureBuffer!.Buffer, multiUvBuffer!.Buffer, colorBuffer!.Buffer },
                new[] { positionBuffer.Stride, normalBuffer.Stride, textureBuffer.Stride, multiUvBuffer.Stride, colorBuffer.Stride },
                new[] { 0, 0, 0, 0, 0 });
            context.VSSetShader(texturedNormalColorMultiUvVertexShader);
            context.PSSetShader(texturedPixelShader);
            context.PSSetShaderResource(0, textureView!);
        }
        else if (textured && hasNormal && hasVertexColor)
        {
            context.IASetInputLayout(GetTexturedNormalColorLayout(
                positionFormat, positionAttribute.Offset,
                normalFormat, normalAttribute!.Offset,
                textureFormat, textureAttribute!.Offset,
                colorFormat, colorAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, normalBuffer!.Buffer, textureBuffer!.Buffer, colorBuffer!.Buffer },
                new[] { positionBuffer.Stride, normalBuffer.Stride, textureBuffer.Stride, colorBuffer.Stride },
                new[] { 0, 0, 0, 0 });
            context.VSSetShader(texturedNormalColorVertexShader);
            context.PSSetShader(texturedPixelShader);
            context.PSSetShaderResource(0, textureView!);
        }
        else if (textured && hasVertexColor)
        {
            context.IASetInputLayout(GetTexturedColorLayout(
                positionFormat, positionAttribute.Offset,
                textureFormat, textureAttribute!.Offset,
                colorFormat, colorAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, textureBuffer!.Buffer, colorBuffer!.Buffer },
                new[] { positionBuffer.Stride, textureBuffer.Stride, colorBuffer.Stride },
                new[] { 0, 0, 0 });
            context.VSSetShader(texturedColorVertexShader);
            context.PSSetShader(texturedPixelShader);
            context.PSSetShaderResource(0, textureView!);
        }
        else if (textured && hasNormal)
        {
            context.IASetInputLayout(GetTexturedNormalLayout(
                positionFormat, positionAttribute.Offset,
                normalFormat, normalAttribute!.Offset,
                textureFormat, textureAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, normalBuffer!.Buffer, textureBuffer!.Buffer },
                new[] { positionBuffer.Stride, normalBuffer.Stride, textureBuffer.Stride },
                new[] { 0, 0, 0 });
            context.VSSetShader(texturedNormalVertexShader);
            context.PSSetShader(texturedPixelShader);
            context.PSSetShaderResource(0, textureView!);
        }
        else if (textured)
        {
            context.IASetInputLayout(GetTexturedLayout(positionFormat, positionAttribute.Offset, textureFormat, textureAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, textureBuffer!.Buffer },
                new[] { positionBuffer.Stride, textureBuffer.Stride },
                new[] { 0, 0 });
            context.VSSetShader(texturedVertexShader);
            context.PSSetShader(texturedPixelShader);
            context.PSSetShaderResource(0, textureView!);
        }
        else if (hasNormal)
        {
            context.IASetInputLayout(GetPositionNormalLayout(
                positionFormat, positionAttribute.Offset, normalFormat, normalAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, normalBuffer!.Buffer },
                new[] { positionBuffer.Stride, normalBuffer.Stride },
                new[] { 0, 0 });
            context.VSSetShader(positionNormalVertexShader);
            context.PSSetShader(solidPixelShader);
            context.PSSetShaderResource(0, null!);
        }
        else
        {
            context.IASetInputLayout(GetPositionLayout(positionFormat, positionAttribute.Offset));
            context.IASetVertexBuffer(0, positionBuffer.Buffer, positionBuffer.Stride, 0);
            context.VSSetShader(positionVertexShader);
            context.PSSetShader(solidPixelShader);
            context.PSSetShaderResource(0, null!);
        }

        context.IASetPrimitiveTopology(topology);
        context.IASetIndexBuffer(primitive.IndexBuffer, primitive.IndexElementSize == 2 ? Format.R16_UInt : Format.R32_UInt, 0);
        context.DrawIndexed(primitive.IndexCount, 0, 0);
    }

    private static bool TryReadGameMaterialId(CpuMaterial material, out int id)
    {
        id = 0;
        if (!material.SourceParameters.TryGetValue("GameMaterialID", out var values)
            || values.Length == 0
            || !float.IsFinite(values[0]))
        {
            return false;
        }
        var rounded = MathF.Round(values[0]);
        if (MathF.Abs(values[0] - rounded) > 0.001f) return false;
        id = checked((int)rounded);
        return true;
    }

    private static void AddSkinnedInput(
        string semanticName,
        int semanticIndex,
        Format format,
        int offset,
        D3D11VertexBufferResource buffer,
        List<D3D11VertexBufferResource> buffers,
        List<InputElementDescription> elements)
    {
        var slot = buffers.Count;
        buffers.Add(buffer);
        elements.Add(new InputElementDescription(semanticName, semanticIndex, format, offset, slot));
    }

    private SkinnedVertexProgram GetSkinnedProgram(int features)
    {
        if (skinnedPrograms.TryGetValue(features, out var cached)) return cached;
        var macros = new List<ShaderMacro> { new("SKINNED", "1") };
        if ((features & 1) != 0) macros.Add(new ShaderMacro("HAS_NORMAL", "1"));
        if ((features & 2) != 0) macros.Add(new ShaderMacro("HAS_TEXTURE", "1"));
        if ((features & 4) != 0) macros.Add(new ShaderMacro("HAS_COLOR", "1"));
        if ((features & 8) != 0) macros.Add(new ShaderMacro("HAS_MULTI_UV", "1"));
        var bytecode = Compile("VSSkinnedMain", "vs_5_0", macros.ToArray());
        cached = new SkinnedVertexProgram(graphics.Device.CreateVertexShader(bytecode), bytecode);
        skinnedPrograms.Add(features, cached);
        return cached;
    }

    private unsafe void UpdateSkinConstants(
        IReadOnlyList<CpuSkinBoneRemap> remaps,
        IReadOnlyList<Matrix4x4> skinMatrices)
    {
        if (remaps.Count > MaximumSkinBones)
            throw new InvalidDataException($"Primitive uses {remaps.Count} skin bones; maximum is {MaximumSkinBones}.");
        for (var index = 0; index < remaps.Count; index++)
        {
            var skeletonIndex = remaps[index].SkeletonMatrixIndex;
            if ((uint)skeletonIndex >= skinMatrices.Count)
                throw new InvalidDataException($"Primitive skin bone {index} maps to missing skeleton matrix {skeletonIndex}.");
            skinningConstants[index] = skinMatrices[skeletonIndex];
        }
        var mapped = graphics.Context.Map(skinningBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        fixed (Matrix4x4* source = skinningConstants)
        {
            Buffer.MemoryCopy(source, (void*)mapped.DataPointer,
                MaximumSkinBones * Marshal.SizeOf<Matrix4x4>(),
                MaximumSkinBones * Marshal.SizeOf<Matrix4x4>());
        }
        graphics.Context.Unmap(skinningBuffer, 0);
    }

    private ID3D11BlendState GetBlendState(CpuRenderPassState state)
    {
        if (blendStates.TryGetValue(state, out var cached)) return cached;
        var description = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
            RenderTarget =
            {
                [0] = new RenderTargetBlendDescription
                {
                    BlendEnable = state.BlendEnabled,
                    SourceBlend = (Blend)state.SourceBlend,
                    DestinationBlend = (Blend)state.DestinationBlend,
                    BlendOperation = (BlendOperation)state.BlendOperation,
                    SourceBlendAlpha = (Blend)state.SourceBlendAlpha,
                    DestinationBlendAlpha = (Blend)state.DestinationBlendAlpha,
                    BlendOperationAlpha = (BlendOperation)state.BlendOperationAlpha,
                    RenderTargetWriteMask = (ColorWriteEnable)state.RenderTargetWriteMask,
                },
            },
        };
        cached = graphics.Device.CreateBlendState(description);
        blendStates.Add(state, cached);
        return cached;
    }

    private ID3D11RasterizerState GetRasterizerState(CpuRasterizerState state)
    {
        if (rasterizerStates.TryGetValue(state, out var cached)) return cached;
        cached = graphics.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = (FillMode)state.FillMode,
            CullMode = (CullMode)state.CullMode,
            FrontCounterClockwise = state.FrontCounterClockwise,
            DepthBias = state.DepthBias,
            DepthBiasClamp = state.DepthBiasClamp,
            SlopeScaledDepthBias = state.SlopeScaledDepthBias,
            DepthClipEnable = state.DepthClipEnabled,
            ScissorEnable = state.ScissorEnabled,
            MultisampleEnable = state.MultisampleEnabled,
            AntialiasedLineEnable = state.AntialiasedLineEnabled,
        });
        rasterizerStates.Add(state, cached);
        return cached;
    }

    private unsafe void UpdateConstants(
        Matrix4x4 matrix,
        Matrix4x4 normalMatrix,
        D3D11MaterialResources? material,
        bool hasNormal,
        bool selected,
        bool preview,
        Vector4 materialDiffuse,
        Vector3 materialEmission,
        bool facialMultiUvOverride)
    {
        var materialSettings = ViewportMaterialSettings.FromMaterial(material?.Source);
        var mapped = graphics.Context.Map(perDrawBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(PerDrawConstants*)mapped.DataPointer = new PerDrawConstants
        {
            WorldViewProjection = matrix,
            WorldInverseTranspose = normalMatrix,
            SelectionColor = preview
                ? new Vector4(0.1f, 1f, 0.35f, 0.62f)
                : selected ? new Vector4(1f, 0.32f, 0.04f, 0.68f) : Vector4.Zero,
            MaterialColor = materialSettings.BaseColor * materialDiffuse,
            MaterialEmission = new Vector4(materialEmission, 0f),
            LightDirectionAndAlphaThreshold = new Vector4(
                lighting.DirectionToLight,
                materialSettings.AlphaThreshold ?? 0f),
            AmbientColorAndAlphaTest = new Vector4(
                lighting.AmbientColor,
                materialSettings.AlphaTestingEnabled && materialSettings.AlphaThreshold.HasValue ? 1f : 0f),
            DirectColorAndHasNormal = new Vector4(
                lighting.DirectColor,
                hasNormal && materialSettings.LightingEnabled ? 1f : 0f),
            EffectSettings = new Vector4(
                materialSettings.GlareHighPassEnabled ? 1f : 0f,
                materialSettings.GlareIntensity,
                material?.Source.RenderPassState?.BlendEnabled == true ? 1f : 0f,
                materialSettings.AlphaBlendingEnabled ? 1f : 0f),
            MultiUvSettings = new Vector4(
                (float)(facialMultiUvOverride
                    ? ViewportMultiUvBlendMode.Alpha
                    : materialSettings.MultiUvBlendMode),
                0f, 0f, 0f),
            MultiUvColor = materialSettings.MultiUvColor,
            MultiUvTransform = materialSettings.MultiUvTransform,
            ViewportSize = new Vector4(width, height, 1f / width, 1f / height),
        };
        graphics.Context.Unmap(perDrawBuffer, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerDrawConstants
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 WorldInverseTranspose;
        public Vector4 SelectionColor;
        public Vector4 MaterialColor;
        public Vector4 MaterialEmission;
        public Vector4 LightDirectionAndAlphaThreshold;
        public Vector4 AmbientColorAndAlphaTest;
        public Vector4 DirectColorAndHasNormal;
        public Vector4 EffectSettings;
        public Vector4 MultiUvSettings;
        public Vector4 MultiUvColor;
        public Vector4 MultiUvTransform;
        public Vector4 ViewportSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DebugLineVertex(Vector4 Position, Vector4 Color);

    /// <summary>
    /// A vertex of an effect quad: already in clip space, with the segment's
    /// colour track, its glow track and the texture coordinate its crop selects.
    /// </summary>
    private readonly record struct EffectVertex(
        Vector4 Position,
        Vector4 Color,
        Vector4 Add,
        Vector2 TexCoord);

    private sealed record SkinnedVertexProgram(ID3D11VertexShader Shader, byte[] Bytecode);

    private ID3D11InputLayout GetPositionLayout(Format format, int offset)
    {
        var key = $"P:{format}:{offset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(
                new[] { new InputElementDescription("POSITION", 0, format, offset, 0) },
                positionVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetPositionNormalLayout(
        Format positionFormat, int positionOffset, Format normalFormat, int normalOffset)
    {
        var key = $"PN:{positionFormat}:{positionOffset}:{normalFormat}:{normalOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("NORMAL", 0, normalFormat, normalOffset, 1),
            }, positionNormalVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetTexturedLayout(Format positionFormat, int positionOffset, Format textureFormat, int textureOffset)
    {
        var key = $"T:{positionFormat}:{positionOffset}:{textureFormat}:{textureOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("TEXCOORD", 0, textureFormat, textureOffset, 1),
            }, texturedVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetTexturedNormalLayout(
        Format positionFormat, int positionOffset,
        Format normalFormat, int normalOffset,
        Format textureFormat, int textureOffset)
    {
        var key = $"TN:{positionFormat}:{positionOffset}:{normalFormat}:{normalOffset}:{textureFormat}:{textureOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("NORMAL", 0, normalFormat, normalOffset, 1),
                new InputElementDescription("TEXCOORD", 0, textureFormat, textureOffset, 2),
            }, texturedNormalVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetTexturedColorLayout(
        Format positionFormat, int positionOffset,
        Format textureFormat, int textureOffset,
        Format colorFormat, int colorOffset)
    {
        var key = $"TC:{positionFormat}:{positionOffset}:{textureFormat}:{textureOffset}:{colorFormat}:{colorOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("TEXCOORD", 0, textureFormat, textureOffset, 1),
                new InputElementDescription("COLOR", 0, colorFormat, colorOffset, 2),
            }, texturedColorVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetTexturedNormalColorLayout(
        Format positionFormat, int positionOffset,
        Format normalFormat, int normalOffset,
        Format textureFormat, int textureOffset,
        Format colorFormat, int colorOffset)
    {
        var key = $"TNC:{positionFormat}:{positionOffset}:{normalFormat}:{normalOffset}:{textureFormat}:{textureOffset}:{colorFormat}:{colorOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("NORMAL", 0, normalFormat, normalOffset, 1),
                new InputElementDescription("TEXCOORD", 0, textureFormat, textureOffset, 2),
                new InputElementDescription("COLOR", 0, colorFormat, colorOffset, 3),
            }, texturedNormalColorVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetTexturedNormalColorMultiUvLayout(
        Format positionFormat, int positionOffset,
        Format normalFormat, int normalOffset,
        Format textureFormat, int textureOffset,
        Format multiUvFormat, int multiUvOffset,
        Format colorFormat, int colorOffset)
    {
        var key = $"TNMC:{positionFormat}:{positionOffset}:{normalFormat}:{normalOffset}:{textureFormat}:{textureOffset}:{multiUvFormat}:{multiUvOffset}:{colorFormat}:{colorOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("NORMAL", 0, normalFormat, normalOffset, 1),
                new InputElementDescription("TEXCOORD", 0, textureFormat, textureOffset, 2),
                new InputElementDescription("TEXCOORD", 1, multiUvFormat, multiUvOffset, 3),
                new InputElementDescription("COLOR", 0, colorFormat, colorOffset, 4),
            }, texturedNormalColorMultiUvVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private void ReleaseTargets()
    {
        graphics.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        depthView?.Dispose();
        depthView = null;
        depthTexture?.Dispose();
        depthTexture = null;
        renderTarget?.Dispose();
        renderTarget = null;
    }

    private static D3D11VertexBufferResource? FindBuffer(
        D3D11PrimitiveResources primitive,
        VertexSemantic semantic,
        int semanticIndex = 0)
        => primitive.VertexBuffers.FirstOrDefault(value => value.Attributes.Any(attribute =>
            attribute.Semantic == semantic && attribute.SemanticIndex == semanticIndex));

    public static bool SupportsSkinningInputs(D3D11PrimitiveResources primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        if (primitive.SkinBones is not { Count: > 0 }) return false;
        var indices = FindBuffer(primitive, VertexSemantic.JointIndices);
        var indexAttribute = indices?.Attributes.FirstOrDefault(value =>
            value.Semantic == VertexSemantic.JointIndices);
        var weights = FindBuffer(primitive, VertexSemantic.JointWeights);
        var weightAttribute = weights?.Attributes.FirstOrDefault(value =>
            value.Semantic == VertexSemantic.JointWeights);
        return indexAttribute is not null
            && weightAttribute is not null
            && TryMapFormat(indexAttribute.SourceFormat, out _)
            && TryMapFormat(weightAttribute.SourceFormat, out _);
    }

    private static bool TryMapFormat(string source, out Format format)
    {
        format = source switch
        {
            "Float32x2" => Format.R32G32_Float,
            "Float32x3" => Format.R32G32B32_Float,
            "Float32x4" => Format.R32G32B32A32_Float,
            "Float16x2" => Format.R16G16_Float,
            "Float16x4" => Format.R16G16B16A16_Float,
            "UInt32x1" => Format.R32_UInt,
            "UInt32x2" => Format.R32G32_UInt,
            "UInt32x3" => Format.R32G32B32_UInt,
            "UInt32x4" => Format.R32G32B32A32_UInt,
            "UInt16x1" => Format.R16_UInt,
            "UInt16x2" => Format.R16G16_UInt,
            "UInt16x4" => Format.R16G16B16A16_UInt,
            "UInt8x1" => Format.R8_UInt,
            "UInt8x2" => Format.R8G8_UInt,
            "UInt8x4" => Format.R8G8B8A8_UInt,
            "UNorm16x2" => Format.R16G16_UNorm,
            "UNorm16x4" => Format.R16G16B16A16_UNorm,
            "UNorm8x2" => Format.R8G8_UNorm,
            "UNorm8x4" => Format.R8G8B8A8_UNorm,
            "SNorm16x2" => Format.R16G16_SNorm,
            "SNorm16x4" => Format.R16G16B16A16_SNorm,
            "SNorm8x2" => Format.R8G8_SNorm,
            "SNorm8x4" => Format.R8G8B8A8_SNorm,
            _ => Format.Unknown,
        };
        return format != Format.Unknown;
    }

    private static bool TryMapTopology(Core.PrimitiveTopology source, out Vortice.Direct3D.PrimitiveTopology topology)
    {
        topology = source switch
        {
            Core.PrimitiveTopology.Points => Vortice.Direct3D.PrimitiveTopology.PointList,
            Core.PrimitiveTopology.Lines => Vortice.Direct3D.PrimitiveTopology.LineList,
            Core.PrimitiveTopology.Triangles => Vortice.Direct3D.PrimitiveTopology.TriangleList,
            Core.PrimitiveTopology.TriangleStrip => Vortice.Direct3D.PrimitiveTopology.TriangleStrip,
            _ => Vortice.Direct3D.PrimitiveTopology.Undefined,
        };
        return topology != Vortice.Direct3D.PrimitiveTopology.Undefined;
    }

    private static byte[] Compile(string entryPoint, string profile, ShaderMacro[]? defines = null)
    {
        var result = Compiler.Compile(
            shaderSource: ShaderSource,
            defines: defines ?? Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint: entryPoint,
            sourceName: "ED8Editor.Viewport.hlsl",
            profile: profile,
            shaderFlags: ShaderFlags.OptimizationLevel3,
            effectFlags: EffectFlags.None,
            out var blob,
            out var errors);
        using (blob)
        using (errors)
        {
            result.CheckError();
            return blob.AsBytes();
        }
    }
}
