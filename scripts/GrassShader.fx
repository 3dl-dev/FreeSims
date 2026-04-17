// Minimal GrassShader stub — renders terrain tiles as vertex-colored lerp
// between LightGreen and DarkGreen based on grass state. Blades pass is a
// pass-through (draws nothing visible). Built with mgfxc 3.8.2 /Profile:OpenGL.

float4x4 View;
float4x4 Projection;
float4x4 World;

float4 LightGreen;
float4 DarkGreen;
float4 LightBrown;
float4 DarkBrown;
float4 DiffuseColor;

float2 ScreenSize;
float2 ScreenOffset;
float GrassProb;

bool depthOutMode;
texture depthMap;

sampler depthSampler = sampler_state {
    texture = <depthMap>;
    AddressU = CLAMP; AddressV = CLAMP;
    MIPFILTER = POINT; MINFILTER = POINT; MAGFILTER = POINT;
};

struct VSIN {
    float3 Position : POSITION0;
    float4 Color    : COLOR0;
    float3 GrassInfo: TEXCOORD0;
};

struct VSOUT {
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float3 GrassInfo: TEXCOORD0;
};

VSOUT VS_Base(VSIN input)
{
    VSOUT o;
    float4 wp = mul(float4(input.Position, 1.0), World);
    float4 vp = mul(wp, View);
    o.Position = mul(vp, Projection);
    o.Color = input.Color;
    o.GrassInfo = input.GrassInfo;
    return o;
}

float4 PS_Base(VSOUT input) : COLOR0
{
    float live = saturate(input.GrassInfo.x);
    // live 0..0.5 => green range; >0.5 => brown. Match TerrainComponent's intent.
    float brownMix = saturate((live - 0.0) * 2.0);
    float4 greenC = lerp(DarkGreen, LightGreen, input.Color.r);
    float4 brownC = lerp(DarkBrown, LightBrown, input.Color.r);
    float4 c = lerp(greenC, brownC, brownMix);
    c.rgb *= DiffuseColor.rgb;
    c.a = 1.0;
    if (depthOutMode) {
        // depth pass — write a constant depth-ish value so stencil pipe works.
        return float4(0.5, 0.5, 0.5, 1.0);
    }
    return c;
}

VSOUT VS_Blade(VSIN input)
{
    VSOUT o;
    // Degenerate triangles (no visible blades in stub).
    o.Position = float4(2.0, 2.0, 2.0, 1.0);
    o.Color = input.Color;
    o.GrassInfo = input.GrassInfo;
    return o;
}

float4 PS_Blade(VSOUT input) : COLOR0
{
    return float4(0, 0, 0, 0);
}

technique DrawBase
{
    pass P0
    {
        VertexShader = compile vs_3_0 VS_Base();
        PixelShader  = compile ps_3_0 PS_Base();
    }
}

technique DrawBlades
{
    pass P0
    {
        VertexShader = compile vs_3_0 VS_Blade();
        PixelShader  = compile ps_3_0 PS_Blade();
    }
}
