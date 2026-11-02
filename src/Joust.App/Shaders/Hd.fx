// The HD look on the GPU: the same passes as Joust.Render.HdRenderer, which stays the reference.
// Compiled by "dotnet mgfxc Hd.fx Hd.mgfxo /Profile:OpenGL" (the app's build does it when this
// file changes). Index textures hold a palette index in red as index/255, sampled at texel centres.

// ---------------------------------------------------------------- Scale2x on palette indices

sampler2D Src : register(s0);       // the SpriteBatch texture: indices at half the target size
float2 SrcSize;

float Fetch(float2 p)              // clamped like the CPU's Math.Max/Min at the edges
{
    p = clamp(p, float2(0, 0), SrcSize - 1);
    return tex2D(Src, (p + 0.5) / SrcSize).r;
}

float4 Scale2x(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float2 o = floor(uv * SrcSize * 2);
    float2 s = floor(o / 2);
    float2 q = o - s * 2;
    float p = Fetch(s);
    float a = Fetch(s + float2(0, -1)), b = Fetch(s + float2(1, 0));
    float c = Fetch(s + float2(-1, 0)), d = Fetch(s + float2(0, 1));
    float r = p;
    if (q.x == 0 && q.y == 0) r = (c == a && c != d && a != b) ? a : p;
    else if (q.x == 1 && q.y == 0) r = (a == b && a != c && b != d) ? b : p;
    else if (q.x == 0 && q.y == 1) r = (d == c && d != b && c != a) ? c : p;
    else r = (b == d && b != a && d != c) ? d : p;
    return float4(r, 0, 0, 1);
}

// ---------------------------------------------------------------- light (at the field's 292x240)

texture LightPaletteTex;            // the palette again, for the light passes
sampler2D LightPalette = sampler_state { Texture = <LightPaletteTex>; MinFilter = Point; MagFilter = Point; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };

float4 LightColour(float index) { return tex2D(LightPalette, float2((index * 255 + 0.5) / 16, 0.5)); }

// Bright pixels feed the bloom (stored at half strength, so 8 bits hold it).
float4 GlowSource(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float index = tex2D(Src, uv).r;
    if (index == 0) return float4(0, 0, 0, 1);
    float3 c = LightColour(index).rgb;
    float bright = max(0, dot(c, float3(0.3, 0.59, 0.11)) - 0.35) * 1.6;
    return float4(c * bright / 2, 1);
}

// Lava and fire feed the much wider heat glow.
float4 HeatSource(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float index = tex2D(Src, uv).r;
    return LightColour(index).a > 0.5 ? float4(1, 0.38, 0.08, 1) : float4(0, 0, 0, 1);
}

// A box blur along one axis, with the edges clamped as the CPU's BoxBlur does.
float4 Box(float2 uv, float2 axis, int radius)
{
    float3 sum = 0;
    for (int k = -radius; k <= radius; k++)
        sum += tex2D(Src, uv + axis * k / SrcSize).rgb;
    return float4(sum / (2 * radius + 1), 1);
}

float4 BoxH3(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Box(uv, float2(1, 0), 3); }
float4 BoxV3(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Box(uv, float2(0, 1), 3); }
float4 BoxH14(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Box(uv, float2(1, 0), 14); }
float4 BoxV14(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Box(uv, float2(0, 1), 14); }

// ---------------------------------------------------------------- compose

// Textures are set by name: MonoGame binds each to the slot the compiled shader uses. The
// SpriteBatch draws HighTex too, so whichever slot it takes holds the same texture.
texture HighTex;                    // 1168x960 indices (after Scale4x)
sampler2D High = sampler_state { Texture = <HighTex>; MinFilter = Point; MagFilter = Point; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };
texture PaletteTex;                 // 16x1: the colour, and in alpha 1 for lava/fire red
sampler2D Palette = sampler_state { Texture = <PaletteTex>; MinFilter = Point; MagFilter = Point; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };
texture GlowTex;                    // 292x240 bloom light / 2, bilinear like the CPU's Sample
sampler2D Glow = sampler_state { Texture = <GlowTex>; MinFilter = Linear; MagFilter = Linear; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };
texture HeatTex;                    // 292x240 lava light
sampler2D Heat = sampler_state { Texture = <HeatTex>; MinFilter = Linear; MagFilter = Linear; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };
texture BackdropTex;                // the cavern, WideWidth x 960, colour x 6
sampler2D Backdrop = sampler_state { Texture = <BackdropTex>; MinFilter = Point; MagFilter = Point; MipFilter = None; AddressU = Clamp; AddressV = Clamp; };

float2 FieldSize;                   // 1168, 960
float2 LowSize;                     // 292, 240
float BackdropWidth;                // WideWidth
float OutputWidth;                  // FieldSize.x, or BackdropWidth for 16:9
float Time, Flicker, Arena;
float BloomAmount, HeatAmount, BevelAmount;
float UseBackdrop, UseShimmer, UseCrt;
float LavaTop;                      // 196

float IndexAt(float2 p)             // 0 (black) outside the field, as the CPU's At()
{
    if (p.x < 0 || p.y < 0 || p.x >= FieldSize.x || p.y >= FieldSize.y) return 0;
    return tex2D(High, (p + 0.5) / FieldSize).r;
}

float4 PaletteAt(float index) { return tex2D(Palette, float2((index * 255 + 0.5) / 16, 0.5)); }

float3 BackdropAt(float2 p) { return tex2D(Backdrop, (p + 0.5) / float2(BackdropWidth, FieldSize.y)).rgb / 6; }

float Channel(float v)              // a soft shoulder instead of a hard clip
{
    if (v > 0.8) v = 0.8 + 0.2 * (1 - exp(-(v - 0.8) * 5));
    return saturate(v);
}

float Scan(float y)
{
    float row = fmod(y, 4);
    return row == 3 ? 0.62 : row == 0 ? 0.9 : 1;
}

float4 Compose(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float X = floor(uv.x * OutputWidth), y = floor(uv.y * FieldSize.y);
    float ox = (OutputWidth - FieldSize.x) / 2;
    float x = X - ox;
    float3 rgb;

    if (x < 0 || x >= FieldSize.x)
    {
        // The 16:9 side panels: plain cavern, darker towards the screen edge.
        float side = ox;
        float s = x < 0 ? X : OutputWidth - 1 - X;
        rgb = BackdropAt(float2(X, y)) * (0.35 + 0.65 * (s / side)) * Arena;
        if (UseCrt > 0.5) rgb *= Scan(y);
        return float4(Channel(rgb.r), Channel(rgb.g), Channel(rgb.b), 1);
    }

    float index = IndexAt(float2(x, y));
    float lowY = floor(y / 4);
    bool lavaRow = UseShimmer > 0.5 && lowY >= LavaTop;
    if (lavaRow && PaletteAt(index).a > 0.5)
    {
        float shift = floor(sin(y * 0.11 + Time * 0.16) * 2.2 + 0.5);    // heat haze
        float other = IndexAt(float2(clamp(x + shift, 0, FieldSize.x - 1), y));
        if (PaletteAt(other).a > 0.5) index = other;
    }

    float v = y / FieldSize.y;
    float lavaLight = pow(saturate((v - 0.55) / 0.45), 2.2) * Flicker;
    if (index == 0)
    {
        if (UseBackdrop > 0.5)
        {
            float3 base = BackdropAt(float2((BackdropWidth - FieldSize.x) / 2 + x, y));
            rgb.r = (base.r * (1 + lavaLight * 2.2) + lavaLight * 0.05) * Arena;
            rgb.g = (base.g * (1 + lavaLight * 0.9) + lavaLight * 0.012) * Arena;
            rgb.b = base.b * Arena;
        }
        else rgb = float3(0, 0, 0);
    }
    else
    {
        rgb = PaletteAt(index).rgb;
        if (BevelAmount > 0)
        {
            float light = 1;
            if (IndexAt(float2(x - 2, y - 2)) == 0) light += 0.28 * BevelAmount;        // lit from the top left
            else if (IndexAt(float2(x + 2, y + 2)) == 0) light -= 0.32 * BevelAmount;   // shadowed bottom right
            if (IndexAt(float2(x + 1, y)) == 0 || IndexAt(float2(x, y + 1)) == 0) light -= 0.08 * BevelAmount;
            rgb *= light;
        }
    }

    float2 lowUv = (float2(x, y) + 0.5) / 4 / LowSize;
    float3 glow = tex2D(Glow, lowUv).rgb * 2;
    float3 heat = tex2D(Heat, lowUv).rgb;
    rgb += glow * BloomAmount + heat * HeatAmount * Flicker * (index == 0 ? 1 : 0.35);

    if (UseCrt > 0.5)
    {
        float2 vxy = (float2(x, y) - FieldSize / 2) / (FieldSize / 2);
        rgb *= Scan(y) * (1 - 0.22 * dot(vxy, vxy));
    }
    return float4(Channel(rgb.r), Channel(rgb.g), Channel(rgb.b), 1);
}

technique Scale2xPass
{
    pass P0 { PixelShader = compile ps_3_0 Scale2x(); }
}

technique ComposePass
{
    pass P0 { PixelShader = compile ps_3_0 Compose(); }
}

technique GlowSourcePass { pass P0 { PixelShader = compile ps_3_0 GlowSource(); } }
technique HeatSourcePass { pass P0 { PixelShader = compile ps_3_0 HeatSource(); } }
technique BoxH3Pass { pass P0 { PixelShader = compile ps_3_0 BoxH3(); } }
technique BoxV3Pass { pass P0 { PixelShader = compile ps_3_0 BoxV3(); } }
technique BoxH14Pass { pass P0 { PixelShader = compile ps_3_0 BoxH14(); } }
technique BoxV14Pass { pass P0 { PixelShader = compile ps_3_0 BoxV14(); } }
