// MoloMax — coloured lines and flat overlay triangles (grid, wireframe, gizmo, selection tint).
cbuffer LineVars : register(b0)
{
    row_major float4x4 ViewProj;
    float4 DepthBias;   // x = clip-space z pull towards the camera (keeps wires on top of their faces)
    float4 Tint;        // multiplies the vertex colour (cached wires are white, tinted per object)
};

struct VS_INPUT
{
    float3 Position : POSITION;
    float4 Colour   : COLOR0;
};

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float4 Colour   : COLOR0;
};

VS_OUTPUT VSMain(VS_INPUT input)
{
    VS_OUTPUT o;
    o.Position = mul(float4(input.Position, 1.0), ViewProj);
    o.Position.z -= DepthBias.x * o.Position.w;
    o.Colour = input.Colour * Tint;
    return o;
}

float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    return input.Colour;
}
