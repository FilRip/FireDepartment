float4x4	WorldViewProj;
float4x4	World;

float3		EyePosObjSpace;
float3		LightPosObjSpace;
float4		Ambient;
float4		LightColor;
float3		NbBurnTiles;

texture DiffTex;
texture DirtTex;
texture LightTex;
texture BurnTex;
texture ReflCube;


sampler DiffSampler = sampler_state
{
	Texture		= (DiffTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

sampler DirtSampler = sampler_state
{
	Texture		= (DirtTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

sampler LightSampler = sampler_state
{
	Texture		= (LightTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

sampler BurnSampler = sampler_state
{
	Texture		= (BurnTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

samplerCUBE ReflCubeSampler = sampler_state
{
	Texture = (ReflCube);
	MinFilter = Linear;
	MipFilter = Linear;
	MagFilter = Linear;
	AddressU = Clamp;
	AddressV = Clamp;
	AddressW = Clamp;
};


///////////////////////////////////////////////////////////////////////////////
float4x4	ShadowProj;
float3		ShadowCasterPos;
float4		ShadowDensity;
float4		ShadowAmbientColor;
texture		ShadowTex;

sampler ShadowSampler = sampler_state
{
	Texture		= (ShadowTex);
	
	MinFilter	= Linear;
	MagFilter	= Linear;
	MipFilter	= Point;
	
	AddressU	= Clamp;
	AddressV	= Clamp;
	AddressW	= Clamp;	
};

struct VSShadowOut
{
	float4 Pos		: POSITION;
	float4 tShadow	: TEXCOORD0;
	float3 Distance : TEXCOORD1;
	float2 tLightmap: TEXCOORD2;
};

struct PSShadowIn
{
	float4 tShadow	: TEXCOORD0;
	float3 Distance : TEXCOORD1;
	float2 tLightmap: TEXCOORD2;
};

struct VSShadowOut_NV
{
	float4 Pos		: POSITION;
	float4 tShadow	: TEXCOORD0;
	float2 tLightmap: TEXCOORD2;
};

struct PSShadowIn_NV
{
	float4 tShadow	: TEXCOORD0;	
	float2 tLightmap: TEXCOORD2;	
};

///////////////////////////////////////////////////////////////////////////////
struct VSIn
{
	float4	Pos		: POSITION;
	float3	Norm	: NORMAL;
	float4	v0		: COLOR0;
	float2	tDiff	: TEXCOORD0;
	float2	tDirt	: TEXCOORD1;
};

struct VSOut_PASS_0
{
	float4 Pos		: POSITION;
	float2 tDiff	: TEXCOORD0;
	float2 tDirt	: TEXCOORD1;
	float2 tLight	: TEXCOORD2;
	float2 tBurn	: TEXCOORD3;
	float4 v0		: COLOR0;
};

struct VSOut_PASS_1
{
	float4 Pos		: POSITION;
	float2 tDiff	: TEXCOORD0;
	float3 tRefl    : TEXCOORD1;
	float4 v0		: COLOR0;
	float4 v1		: COLOR1;
};

struct PSIn_PASS_0
{
	float2 tDiff	: TEXCOORD0;
	float2 tDirt	: TEXCOORD1;
	float2 tLight	: TEXCOORD2;
	float2 tBurn	: TEXCOORD3;
	float4 v0		: COLOR0;
};

struct PSIn_PASS_1
{
	float2 tDiff	: TEXCOORD0;
	float3 tRefl    : TEXCOORD1;
	float4 v0		: COLOR0;
	float4 v1		: COLOR1;
};

struct PSOut
{
	float4 Col : COLOR0;
};


//-----------------------------------------------------------
// 
// PASS 0
//
//-----------------------------------------------------------
VSOut_PASS_0 VS(VSIn In, uniform bool bNormalMap, uniform bool bLightMap)
{
	VSOut_PASS_0 Out;

	float4 Pos = In.Pos;

	Out.Pos		= mul(Pos, WorldViewProj);
	Out.tDiff	= In.tDiff;
	Out.tDirt	= In.tDirt;
	Out.tLight  = In.tDirt;
	Out.tBurn	= In.tDirt * NbBurnTiles;
	Out.v0		= In.v0;

	return Out;
}

///////////////////////////////////////////////////////////////////////////////
PSOut PS(PSIn_PASS_0 In, 
		 uniform bool bNormalMap,
		 uniform bool bDirtMap,
		 uniform bool bLightMap
		 )
{
	PSOut Out;

	float4 Diff = tex2D(DiffSampler, In.tDiff);
	float4 Burn = tex2D(BurnSampler, In.tBurn);
	
	float4 Cur = Diff;

	if (bDirtMap)
	{			
		float4 Dirt	= tex2D(DirtSampler, In.tDirt);
		Cur = lerp(Dirt, Cur, 0.5);
	}

	Cur = lerp(Cur, Burn, In.v0.a);

	if (bLightMap)
	{
		float4 ndl	= tex2D(LightSampler, In.tLight);
		Out.Col		= ((ndl) * Cur * 2);
	}
	else
	{
		float4 ndl	= In.v0;
		Out.Col		= ((ndl) * Cur * 2);		
	}
	return Out;
}

//-----------------------------------------------------------
// 
// PASS 1
//
//-----------------------------------------------------------
VSOut_PASS_1 VS_PASS_1(VSIn In)
{
	VSOut_PASS_1 Out;

	float4 Pos = In.Pos;

	Out.Pos		= mul(Pos, WorldViewProj);
	Out.tDiff	= In.tDiff;
	Out.v0		= In.v0;
	
	float3 EyeVector	= normalize(EyePosObjSpace - In.Pos);
	float3 LightVector	= normalize(LightPosObjSpace - In.Pos);
	float3 HalfVector	= normalize((EyeVector + LightVector) * 0.5);
	
	Out.v1		  = saturate( dot(HalfVector, In.Norm) );
	Out.tRefl.xyz = HalfVector;
//	Out.tRefl.xyz = reflect(EyeVector, In.Norm);
	return Out;
}

///////////////////////////////////////////////////////////////////////////////
PSOut PS_PASS_1(PSIn_PASS_1 In )
{
	PSOut Out;

	float4 Diff	= tex2D(DiffSampler, In.tDiff);
	float3 Refl = texCUBE(ReflCubeSampler, In.tRefl);
	
	Out.Col.rgb	= (Diff.aaaa * (1-In.v0) * In.v1 * Refl);
	Out.Col.a	= Diff.aaaa;

	return Out;
}

///////////////////////////////////////////////////////////////////////////////
VSShadowOut VSShadow(VSIn In)
{
	VSShadowOut Out;
	Out.Pos		= mul(In.Pos, WorldViewProj);
	Out.tShadow = mul(In.Pos, ShadowProj);
	
	float3 WorldPos = mul(In.Pos, World);
	Out.Distance = WorldPos - ShadowCasterPos;
	
	Out.tLightmap = In.tDirt;

	return Out;
}

PSOut PSShadow(PSShadowIn In)
{
	PSOut Out;
	
	float CurrentDistance	= dot((float3)In.Distance, (float3)In.Distance);
	float ShadowDistance	= tex2Dproj(ShadowSampler,	In.tShadow);	
	float4 light			= tex2D(LightSampler, In.tLightmap);
	float4 Shadow			= (1.0 - (light - ShadowAmbientColor));
	Out.Col					= ShadowDistance > CurrentDistance ? 1 : Shadow;
	return Out;
}


///////////////////////////////////////////////////////////////////////////////
VSShadowOut_NV VSShadow_NV(VSIn In)
{
	VSShadowOut_NV Out;
	Out.Pos		= mul(In.Pos, WorldViewProj);
	Out.tShadow = mul(In.Pos, ShadowProj);
	Out.tLightmap = In.tDirt;
	return Out;
}

PSOut PSShadow_NV(PSShadowIn_NV In)
{
	PSOut Out;
	
	float4 Shadow = tex2D(ShadowSampler, In.tShadow);
	float4 light  = tex2D(LightSampler, In.tLightmap);
	float4 ShadowColor = (1.0 - (light - ShadowAmbientColor));
	Out.Col = saturate(Shadow + ShadowColor);

	return Out;
}

///////////////////////////////////////////////////////////////////////////////

#include "FD2GroundShaderArray.fx"

int CurrentVertexShader = 0;
int CurrentPixelShader	= 0;

technique t0
{
	pass p0
	{
		VertexShader	= (GroundVSArray[CurrentVertexShader]);
		PixelShader		= (GroundPSArray[CurrentPixelShader]);
	}

	pass p1
	{
		VertexShader	= compile vs_1_1 VS_PASS_1();
		PixelShader		= compile ps_1_1 PS_PASS_1();
	}

	pass p2
	{
		VertexShader	= compile vs_2_0 VSShadow();
		PixelShader		= compile ps_2_0 PSShadow();
	}

	pass p3
	{
		VertexShader	= compile vs_1_1 VSShadow_NV();
		PixelShader		= compile ps_1_1 PSShadow_NV();
	}
};

///////////////////////////////////////////////////////////////////////////////
technique t1
{
	// diffuse dirt
	pass p0	
	{
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
	// lightmap
	pass p1
	{
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
}
