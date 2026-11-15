float4x4	WorldViewProj;
float4x4	World;
float4		OneMinusAmbientLight;
float3		EyePos;
float3		EyePosObjSpace;
float4		SelectionColor;

#include "Lighting.fx"

texture DiffTex;
texture BurnTex;
texture ReflTex;
texture IllumTex;

#define SHADOWTECH_NONE		0
#define SHADOWTECH_R9700	1
#define SHADOWTECH_GF3		2

float4 GhostColor;

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

samplerCUBE ReflSampler = sampler_state
{
	Texture		= (ReflTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

sampler IllumSampler = sampler_state
{
	Texture		= (IllumTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};


///////////////////////////////////////////////////////////////////////////////
float4x4	ShadowProj;
float3		ShadowCasterPos;
float4		ShadowDensity;
texture		ShadowTex;
float		R9700ShadowMapBias;
float		GF3ShadowMapZBias;


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

//struct VSShadowOut
//{
//	float4 Pos		: POSITION;
//	float4 tShadow	: TEXCOORD0;
//	float3 Distance : TEXCOORD1;
//};
//
//struct PSShadowIn
//{
//	float4 tShadow	: TEXCOORD0;
//	float3 Distance : TEXCOORD1;
//};
//
//struct VSShadowMapOut
//{
//	float4 Pos		: POSITION;	
//	float3 Distance : TEXCOORD0;
//	float2 tDiff	: TEXCOORD1;
//};
//
//struct PSShadowMapIn
//{	
//	float3 Distance : TEXCOORD0;
//	float2 tDiff	: TEXCOORD1;
//};
//
//struct VSShadowOut_NV
//{
//	float4 Pos		: POSITION;
//	float4 tShadow	: TEXCOORD0;	
//};
//
//struct PSShadowIn_NV
//{
//	float4 tShadow	: TEXCOORD0;	
//};
//
//struct VSShadowMapOut_NV
//{
//	float4 Pos		: POSITION;	
//	float2 tDiff	: TEXCOORD0;
//};
//
//struct PSShadowMapIn_NV
//{	
//	float2 tDiff : TEXCOORD0;
//};

///////////////////////////////////////////////////////////////////////////////
struct VSIn
{
	float4	Pos					: POSITION;
	float3	Norm				: NORMAL;
	float4	Color				: COLOR0;
	float2	DiffuseTexcoord		: TEXCOORD0;
	float2	BurnmapTexcoord		: TEXCOORD1;
};

struct VSOut
{
	float4	Pos					: POSITION;
	float4	Color				: COLOR0;
	float4	Spec				: COLOR1;
	float4	DiffuseTexcoord		: TEXCOORD0;
	float3	BurnTexcoord		: TEXCOORD1;
	float2	IllumTexcoord		: TEXCOORD2;
	float3	ReflTexcoord		: TEXCOORD3;
};

struct PSIn
{
	float4	Color				: COLOR0;
	float4	Spec				: COLOR1;
	float4	DiffuseTexcoord		: TEXCOORD0;
	float3	BurnTexcoord		: TEXCOORD1;
	float2	IllumTexcoord		: TEXCOORD2;
	float3	ReflTexcoord		: TEXCOORD3;
};

//struct PSOut
//{
//	float4 Col : COLOR0;
//};


///////////////////////////////////////////////////////////////////////////////
VSOut VS(VSIn In, 
	uniform int NbOmni,
	uniform bool bShadowCast, 
	uniform bool bShadowReceive, 
	uniform int ShadowTech,
	uniform bool bGhost)
{
	VSOut Out;

	// common
	float4 Pos			= In.Pos;
	float3 Norm			= In.Norm;
	Out.Pos				= mul(Pos, WorldViewProj);
	Out.Color			= In.Color;
	Out.DiffuseTexcoord	= float4(In.DiffuseTexcoord,0,1);
	Out.BurnTexcoord	= float3(In.BurnmapTexcoord,0);
	Out.IllumTexcoord	= In.DiffuseTexcoord;
	Out.Spec			= 0;

	//------------------------
	// Reflection 
	//------------------------
	float3 WorldPos		= mul(Pos, World);
	float3 WorldNorm	= normalize(mul(Norm, World));
	float3 EyeVector	= normalize(WorldPos - EyePos);

	Out.ReflTexcoord  = reflect(EyeVector, WorldNorm);

	//------------------------------------
	// shadows cast
	//------------------------------------
	if (bShadowCast)
	{
		Out.DiffuseTexcoord	= float4(In.DiffuseTexcoord,0,0);

		if (ShadowTech == SHADOWTECH_R9700)
		{
			float3 WorldPos	= mul(In.Pos, World);
			Out.BurnTexcoord= (WorldPos - ShadowCasterPos) * R9700ShadowMapBias; // distance
		}
		return Out;
	}
	
	//------------------------------------
	// shadows receive
	//------------------------------------
	if (bShadowReceive)
	{
		Out.DiffuseTexcoord	= mul(In.Pos, ShadowProj); // tShadow
		float3 WorldPos	= mul(In.Pos, World);

		if (ShadowTech == SHADOWTECH_R9700)
		{
			Out.BurnTexcoord	= (WorldPos - ShadowCasterPos); // distance
		}
		
		if (ShadowTech == SHADOWTECH_GF3)
		{
			float3 ShadowVec		= normalize(WorldPos - ShadowCasterPos);
			float3 WorldNorm		= normalize(mul(In.Norm, (float3x3)World));
			float InvDot			= 1 - abs(dot(ShadowVec, WorldNorm));	
			Out.DiffuseTexcoord.z	+= GF3ShadowMapZBias * saturate(InvDot);
		}
		return Out;
	}


	//------------------------
	// lighting
	//------------------------
	SLightIn LightIn;
	SLightOut LightOut;

	LightIn.Pos			= Pos;
	LightIn.Norm		= Norm;
	LightIn.WorldPos	= WorldPos;

	Out.Color.rgb		= AmbientLight.rgb;
	for (int iLight=0; iLight<NbOmni; iLight++)
	{
		LightOut = ComputeLighting(LightArray[iLight], LightIn);
		// don't write down alpha channel because already used by burn factor
		Out.Color.rgb += LightOut.Diff.rgb;
	}		
	Out.Color.rgb = max(Out.Color.rgb, SelectionColor.rgb);
	Out.Color.rgb = saturate(Out.Color.rgb);

	float3 EyeVec		= normalize(EyePosObjSpace - Pos);
	float  FresnelTerm	= saturate(dot(Norm, EyeVec));
	FresnelTerm *= FresnelTerm;
	FresnelTerm  = 1-FresnelTerm;
	Out.Spec	 = FresnelTerm;

	//----------------------------
	// Ghost
	//----------------------------
	if (bGhost)
	{
//		float3 EyeVec	= normalize(EyePosObjSpace - Pos);
//		float nde1	= 1 - saturate(dot(Norm, EyeVec)) * 0.5;
//		float nde2	= nde1 * nde1;
//		float nde4	= nde2 * nde2;
//		float nde16 = nde4 * nde4;
//		float4 nde	= float4(nde1, nde2, nde4, nde16);
//		float4 ndec = float4(0.5,1.0,2.0,4.0);
//		Out.Color	= dot(nde, ndec) * GhostColor;
		Out.Color = GhostColor;
	}

	return Out;
}

float4 PS(PSIn In,
		uniform bool bShadowCast,	uniform bool bShadowReceive, 
		uniform int ShadowTech,		uniform bool bGhost, 
		uniform bool bHilite, uniform bool bGrayed) : COLOR0 
{
	//------------------------------------
	// ghost
	//------------------------------------
	if (bGhost)
	{
		float4 Diffuse	= tex2D(DiffSampler, In.DiffuseTexcoord);
		return In.Color + float4(Diffuse.rgb*0.5, 1);
	}

	//----------------------------------------
	// Shadow cast
	//----------------------------------------
	if (bShadowCast)
	{
		if (ShadowTech == SHADOWTECH_R9700)
		{
			return dot((float3)In.BurnTexcoord, (float3)In.BurnTexcoord);	
		}
		if (ShadowTech == SHADOWTECH_GF3)
		{
			return float4(1,1,1,1);
		}
	}

	//------------------------------------------------
	// Shadow receive
	//------------------------------------------------
	if (bShadowReceive)
	{
		if (ShadowTech == SHADOWTECH_R9700)
		{
			float CurrentDistance	= dot((float3)In.BurnTexcoord, (float3)In.BurnTexcoord);
			float ShadowDistance	= tex2Dproj(ShadowSampler, In.DiffuseTexcoord);
			return ShadowDistance > CurrentDistance ? 1 : ShadowDensity;
		}
		
		if (ShadowTech == SHADOWTECH_GF3)
		{
			// Projection flag need to be set outside of the .fx 
			float4 Shadow = tex2D(ShadowSampler, In.DiffuseTexcoord);
			return saturate(Shadow + ShadowDensity);
		}		
	}

	//----------------------------------
	// Default path
	//----------------------------------
	float4 Diffuse	= tex2D(DiffSampler,		In.DiffuseTexcoord);
	float4 Burn		= tex2D(BurnSampler,		In.BurnTexcoord);
	float4 Illum	= tex2D(IllumSampler,		In.IllumTexcoord);
	float4 Refl		= texCUBE(ReflSampler,		In.ReflTexcoord);
	
	// self light => 
	float3 l = Illum.rgb * OneMinusAmbientLight.rgb * (1-In.Color.aaaa); 
	float4 r  = Refl * Illum.aaaa * In.Spec;
	float4 d = (Diffuse + r); 
		   d *= In.Color;
		   d += float4(l,1);
	       d  = lerp(d, Burn, In.Color.aaaa);
		   	
	if (bHilite)
	{
		d = d + SelectionColor;
	}

	if (bGrayed)
	{
		d = dot(d,float3(0.5,0.3,0.2));
	}

	d.a = Diffuse.a;
	return d;
}

//-------------------------------------------------
float4 main(PSIn In) : COLOR0
{
	bool bShadowCast		= false;
	bool bShadowReceive		= false;
	int	ShadowTech			= SHADOWTECH_NONE;	
	bool bGhost				= false;
	bool bHilite			= true;
	bool	bGrayed = false;
	
	return PS(In, bShadowCast, bShadowReceive, ShadowTech, bGhost, bHilite, bGrayed);
}

//-------------------------------------------------
VSOut mainVS(VSIn In) 
{

	int		NbOmni = 1;	
	bool	bShadowCast		= false;
	bool	bShadowReceive	= true;
	int		ShadowTech		= SHADOWTECH_R9700;
	bool	bGhost			= false;
	
	return VS(In, NbOmni, bShadowCast, bShadowReceive, ShadowTech, bGhost);
}

//-------------------------------------------------
int CurrentVertexShader = 0;
int CurrentPixelShader	= 0;

#include "FD2VehicleShaderArray.fx"

technique t0
{
	pass p0
	{
		VertexShader	= (VehicleVSArray[CurrentVertexShader]);
		PixelShader		= (VehiclePSArray[CurrentPixelShader]);	
	}

	// Ghost
	pass p1
	{
		VertexShader	= (VehicleVSArray[CurrentVertexShader]);
		PixelShader		= (VehiclePSArray[CurrentPixelShader]);	
	}

	// ATI shadows
	pass p2
	{
		VertexShader	= (VehicleVSArray[CurrentVertexShader]);
		PixelShader		= (VehiclePSArray[CurrentPixelShader]);	
	}

	// ATI shadows Map
	pass p3
	{
		VertexShader	= (VehicleVSArray[CurrentVertexShader]);
		PixelShader		= (VehiclePSArray[CurrentPixelShader]);	
	}

	// NVIDIA shadows
	pass p4
	{
		VertexShader	= (VehicleVSArray[CurrentVertexShader]);
		PixelShader		= (VehiclePSArray[CurrentPixelShader]);	
	}

	// NVIDIA shadows Map
	pass p5
	{
		VertexShader	= (VehicleVSArray[CurrentVertexShader]);
		PixelShader		= (VehiclePSArray[CurrentPixelShader]);	
	}
}

// GF2
technique t1
{
	pass p0	// diffuse
	{	
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
	pass p1	// cubemap
	{
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
}

