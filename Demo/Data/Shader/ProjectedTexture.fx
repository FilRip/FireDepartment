// Projected Texture Effect File

texture ProjTex			: PROJECTEDTEXTURE;
texture DiffTex			: DIFFUSETEXTURE;
float4	DiffColor		: DIFFUSECOLOR;
float4  LightMultiplier : LIGHTMULTIPLIER;
float4  LightDir		: LIGHTDIRECTION;

float4x4 WorldViewProj	: WORLDVIEWPROJ;
float4x4 World			: WORLD;
float4x4 UVMatrix		: UVMATRIX;

struct VSInput
{
	float4 Pos		: POSITION;
	float3 Norm		: NORMAL;
	float2 T0		: TEXCOORD;
};

struct VSOutput
{
	float4 Pos		: POSITION;
	float4 Diff		: COLOR0;
	float4 T0		: TEXCOORD0;
	float4 T1		: TEXCOORD1;
};	

//-----------------------------------------------------------------------------
VSOutput TexProj(VSInput _IN)
{
	VSOutput OUT;

//	OUT.Pos				= mul(_IN.Pos, WorldViewProj);
	OUT.Pos				= mul(WorldViewProj, _IN.Pos);

	OUT.T0				= float4(_IN.T0,0,1);
//	OUT.T1				= mul(_IN.Pos, UVMatrix);
	OUT.T1				= mul(UVMatrix, _IN.Pos);
	
//	float3 WorldNorm	= mul(_IN.Norm, (float3x3)World);
	float3 WorldNorm	= mul((float3x3)World, _IN.Norm);
	float ndl			= dot(WorldNorm, (float3)LightDir);
	float l				= saturate(ndl);

	OUT.Diff			= LightMultiplier * l;

	return OUT;
}

//-----------------------------------------------------------------------------
VSOutput VS_DiffuseTexture(VSInput _IN)
{
	VSOutput OUT = TexProj(_IN);
	return OUT;
}

//-----------------------------------------------------------------------------
VSOutput VS_DiffuseColor(VSInput _IN)
{
	VSOutput OUT	= TexProj(_IN);
	OUT.T0			= OUT.T1;			// driver bug workaround, not needed in software processing mode

	OUT.Diff		*= DiffColor;
	return OUT;
}

//-----------------------------------------------------------------------------
technique tec0
{
    pass p0
    {
        
		VertexShader	= compile vs_1_1 VS_DiffuseTexture();

		Texture[0]		= <DiffTex>;
		Texture[1]		= <ProjTex>;

    }
}

//-----------------------------------------------------------------------------
technique tec1
{
    pass p0
    {
        
        SpecularEnable	= False;
		Lighting		= False;
		CullMode		= CW;

		VertexShader	= compile vs_1_1 VS_DiffuseColor();

//		TextureFactor				= <DiffColor>;
		Texture[0]					= <DiffTex>;
		Texture[1]					= <ProjTex>;
		TextureTransformFlags[1]	= Projected;

		TexCoordIndex[0] = 0;
		TexCoordIndex[1] = 1;
		TexCoordIndex[2] = 2;
		TexCoordIndex[3] = 3;

		// diffuse texture stage 0
		ColorOp[0]		= SelectArg1;
		ColorArg1[0]	= Diffuse;
		ColorArg2[0]	= Diffuse;

		AlphaOp[0]		= SelectArg1;
		AlphaArg1[0]	= TFactor;

        
        // projected texture stage 1
        ColorOp[1]		= Modulate4x;
        ColorArg1[1]	= Texture;
        ColorArg2[1]	= Current;

        AlphaOp[1]		= SelectArg1;
        AlphaArg1[1]	= Current;


		MinFilter[0]	= Linear;
		MagFilter[0]	= Linear;

		AddressU[1]		= Clamp;
		AddressV[1]		= Clamp;
		BorderColor[1]  = 0x0;
		MinFilter[1]	= Linear;
		MagFilter[1]	= Linear;
        
        // Disable texture stage 1
        ColorOp[2]		= Disable;
        AlphaOp[2]		= Disable;

		AlphaBlendEnable	= True;
		SrcBlend			= One;
		DestBlend			= One;

		ZWriteEnable		= False;
    }
}





















