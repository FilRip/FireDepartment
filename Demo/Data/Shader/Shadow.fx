/// hello

float4x4 WorldViewProj;
float4x4 World;
float4x4 UVWorldViewProj;

float3	LightPos;
texture ShadowTex;
float	ShadowDensity;

//texture MaskTex;


///////////////////////////////////////////////////////////////////////////////
sampler ShadowSampler = sampler_state
{
	Texture		= (ShadowTex);
	
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	
	AddressU	= Clamp;
	AddressV	= Clamp;
	AddressW	= Clamp;	
};

//sampler MaskSampler = sampler_state
//{
//	Texture = (MaskTex);
//	
//	MinFilter	= Linear;
//	MipFilter	= Linear;
//	MagFilter	= Linear;
//	
//	AddressU	= Clamp;
//	AddressV	= Clamp;
//	AddressW	= Clamp;	
//};

///////////////////////////////////////////////////////////////////////////////
struct VSIn
{
	float4 Pos : POSITION;
};

struct PSIn
{	
	float4 ShadowCoord	: TEXCOORD0;
	float3 LightVec		: TEXCOORD1;
};

struct VSOut
{
	float4 Pos : POSITION;
	
	PSIn m;
};


struct PSOut
{
	float4 Color : COLOR0;
};

///////////////////////////////////////////////////////////////////////////////
VSOut VS(VSIn In)
{
	VSOut Out;
	
	Out.Pos				= mul(WorldViewProj, In.Pos);
	Out.m.ShadowCoord	= mul(UVWorldViewProj, In.Pos);

	float3 WorldPos		= mul(World, In.Pos);
	Out.m.LightVec		= WorldPos - LightPos;

	return Out;
}

PSOut PS(PSIn In)
{
	PSOut Out;
	
	float CurrentDistance	= dot(In.LightVec, In.LightVec);
	float ShadowDistance	= tex2Dproj(ShadowSampler, In.ShadowCoord);

	float4	Shadow		= ShadowDistance > CurrentDistance ? 1 : 0;	
//	float4	MaskColor	= tex2Dproj(MaskSampler, In.ShadowCoord);

	Out.Color = saturate(Shadow + ShadowDensity /*+ MaskColor*/);
	
	return Out;
}

///////////////////////////////////////////////////////////////////////////////
struct VSDepthIn
{
	float4 Pos	: POSITION;
};

struct VSDepthOut
{
	float4 Pos			: POSITION;
	float4 ShadowCoord	: TEXCOORD0;
//	float4 MaskCoord	: TEXCOORD1;
};

struct PSDepthIn
{
	float4 ShadowCoord	: TEXCOORD0;
//	float4 MaskCoord	: TEXCOORD1;
};



VSDepthOut VSDepth(VSDepthIn In)
{
	VSDepthOut Out;

	Out.Pos				= mul(WorldViewProj, In.Pos);
	Out.ShadowCoord		= mul(UVWorldViewProj, In.Pos);
//	Out.MaskCoord		= Out.ShadowCoord;

	return Out;
}

PSOut PSDepth(PSDepthIn In)
{
	PSOut Out;

	float4 Shadow		= tex2D(ShadowSampler,	In.ShadowCoord);
//	float4 MaskColor	= tex2D(MaskSampler,	In.MaskCoord);
	
	Out.Color = saturate(Shadow + ShadowDensity /*+ MaskColor*/);

	return Out;
}


///////////////////////////////////////////////////////////////////////////////

technique t0
{
	pass p0
	{
		VertexShader	= compile vs_2_0 VS();
		PixelShader		= compile ps_2_0 PS();
	}
}

technique t1
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VSDepth();
		PixelShader		= compile ps_1_1 PSDepth();
	}
}