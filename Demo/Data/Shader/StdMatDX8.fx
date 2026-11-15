

int Version = 102;


float4x4 UVWorldViewProj;
float4x4 WorldViewProj;
float4x4 World;
float4x4 View;
float4x4 DiffUVXForm;

float3	EyePosObjSpace;
float3	EyePos;

float4	SelectionColor;
float4	SelfColor;

/*
 *	Shadow Parameters
 */
float3	LightPos;				
float3	LightPosObjSpace;
float	R9700ShadowMapBias;
float4	ShadowDensity;

#include "Lighting.fx"
#include "Skinning.fx"

texture DiffTex;
texture SelfTex;
texture SpecTex;
texture ReflectionTex;
texture ShadowTex;
texture ShadowMaskTex;

#define SHADOWTECH_NONE		0
#define SHADOWTECH_R9700	1
#define SHADOWTECH_GF3		2

texture BlendTex;
float	BlendTexCoeff;

texture HeatTex;
texture GlowTex;

float4 TranslucyCoeff;

sampler DiffSampler = sampler_state
{
	Texture		= (DiffTex);
//	MinFilter	= Linear;
//	MipFilter	= Linear;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};

sampler SpecSampler = sampler_state
{
	Texture		= (SpecTex);
//	MinFilter	= Linear;
//	MipFilter	= Point;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};

sampler SelfSampler = sampler_state
{
	Texture		= (SelfTex);
//	MinFilter	= Linear;
//	MipFilter	= Point;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};

sampler ReflectionSampler = sampler_state
{
	Texture		= (ReflectionTex);
//	MinFilter	= Linear;
//	MipFilter	= Point;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};

sampler ShadowSampler = sampler_state
{
	Texture		= (ShadowTex);
	
//	MinFilter	= Linear;
//	MipFilter	= Point;
//	MagFilter	= Linear;
	
	AddressU	= Clamp;
	AddressV	= Clamp;
	AddressW	= Clamp;	
};

sampler ShadowMaskSampler = sampler_state
{
	Texture = (ShadowMaskTex);
//	Texture = (DiffTex);
	
//	MinFilter	= Linear;
//	MipFilter	= Linear;
//	MagFilter	= Linear;
//	
//	AddressU	= Clamp;
//	AddressV	= Clamp;
//	AddressW	= Clamp;
};

sampler BlendSampler = sampler_state
{
	Texture		= (BlendTex);
//	MinFilter	= Linear;
//	MipFilter	= Linear;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};

sampler HeatSampler = sampler_state
{
	Texture		= (HeatTex);
//	MinFilter	= Linear;
//	MipFilter	= Point;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};

sampler GlowSampler = sampler_state
{
	Texture		= (GlowTex);
//	MinFilter	= Linear;
//	MipFilter	= Point;
//	MagFilter	= Linear;
//	AddressU	= Wrap;
//	AddressV	= Wrap;
//	AddressW	= Wrap;
};




struct VSIn
{
	float4 Pos	: POSITION;
	float3 Norm : NORMAL;
	float2 Tex	: TEXCOORD0;
    float4 BlendWeight  : BLENDWEIGHT;
    int4 BlendIndices : BLENDINDICES;
};

struct VSOut
{
	float4 Pos		: POSITION;
	float4 Diff		: COLOR0;
	float4 Spec		: COLOR1;
	float4 Tex[4]	: TEXCOORD0;
};

struct PSIn
{
	float4 Diff		: COLOR0;	
	float4 Spec		: COLOR1;
	float4 Tex[4]	: TEXCOORD0;
};

VSOut VS(VSIn In, 
	uniform int NbOmni, uniform int NbSpot, uniform int NbBone, 
	uniform bool bEnvmapTexgen, uniform bool bSkinNormal,
	uniform bool bShadowCast, uniform bool bShadowReceive, uniform int ShadowTech,
	uniform bool bDiffUVXform
	)
{
	VSOut Out;

	float4 Pos;
	float3 Norm;
		
	SSkinningIn SkIn;		    
	SkIn.Pos				= In.Pos;
	SkIn.Norm				= In.Norm;
	SkIn.BlendIndexArray	= (int[4])(D3DCOLORtoUBYTE4(In.BlendIndices));
	SkIn.BlendWeightsArray	= (float[4])In.BlendWeight;
	
	SSkinningOut SkOut = ComputeSkinning(SkIn, NbBone, bSkinNormal);
	    
	Pos		= SkOut.Pos;
	Norm	= SkOut.Norm;
	
	
	    	
	Out.Pos			= mul(Pos, WorldViewProj);
	float3 WorldPos = mul(Pos, World);


	/*
	 *	lighting
	 */		
	SLightIn LightIn;
	LightIn.Pos			= Pos;
	LightIn.Norm		= Norm;
	LightIn.WorldPos	= WorldPos;
	
	SLightOut LightOut;

	Out.Diff	= 0;
	Out.Spec	= 0;
	
	for (int iLight=0; iLight<NbOmni; iLight++)
	{
		LightOut = ComputeLighting(LightArray[iLight], LightIn);
		Out.Diff += LightOut.Diff;
		Out.Spec += LightOut.Spec;
	}
	Out.Diff = saturate(Out.Diff);
	Out.Spec = saturate(Out.Spec);
		
	Out.Diff.a = 1;
	
	Out.Spec.rgb	*= MaterialSpecColor;
	Out.Spec.a		= 0;

	/*
	 *	texcoords
	 *
	 *  0 = diffuse and opacity
	 *	1 = selfillum and specular mask
	 *	2 = envmap or cubemap
	 *	3 = 'unused'
	 *
	 */
	Out.Tex[0] = Out.Tex[1] = Out.Tex[2] = Out.Tex[3] = float4(0,0,0,1);

	Out.Tex[0].xy = In.Tex;	// Diffuse and opacity
	Out.Tex[1].xy = In.Tex;	// SelfIllum and specmask
	Out.Tex[2].xy = In.Tex;	// SelfIllum and specmask
		
	if (bDiffUVXform)
	{
		Out.Tex[0].xy = mul(float3(In.Tex,1), (float3x3)DiffUVXForm);
	}

	if (bEnvmapTexgen)
	{
		float3 WorldNorm = normalize(mul(Norm, (float3x3)View));
		Out.Tex[2].xy = ((float2)(WorldNorm)) * 0.5 + 0.5;
	}


	Out.Tex[3].xy = In.Tex;
	
	
	
	/*
	 *	shadow
	 */

	if (bShadowCast)
	{
		if (ShadowTech == SHADOWTECH_R9700)
		{
			Out.Tex[3].xyz = (WorldPos - EyePos) * R9700ShadowMapBias;
		}
	}
	
	if (bShadowReceive)
	{
		if (ShadowTech == SHADOWTECH_R9700)
		{
			Out.Tex[2]		= mul(Pos, UVWorldViewProj);
			Out.Tex[3].xyz	= WorldPos - LightPos;
		}
		
		if (ShadowTech == SHADOWTECH_GF3)
		{
			float4	ShadowTexCoord = mul(Pos, UVWorldViewProj);
			Out.Tex[2]	= ShadowTexCoord;
			Out.Tex[3]	= ShadowTexCoord;
		}
	}
			
	return Out;
}

float4 PS(PSIn In, 
	uniform bool bSelfTex, uniform bool bSelfColor,
	uniform bool bSpecMask, uniform bool bReflection,
	uniform bool bShadowCast, uniform bool bShadowReceive, uniform int ShadowTech,
	uniform bool bHeat, uniform bool bGlow, uniform bool bTranslucy) : COLOR0 
{

	if (bShadowCast)
	{
		if (ShadowTech == SHADOWTECH_R9700)
		{
			return dot((float3)In.Tex[3], (float3)In.Tex[3]);	
		}
		if (ShadowTech == SHADOWTECH_GF3)
		{
			return float4(1,1,1,1);
		}
	}
	
	float4 Diff	= tex2D(DiffSampler, In.Tex[0]);	
		
	if (bSelfTex)
	{
		In.Diff += tex2D(SelfSampler, In.Tex[1]) * 0.25f;
	}
	
	if (bSelfColor)
	{		
		In.Diff += SelfColor * 0.25f;
	}
		
	if (bReflection)
	{
		float4 Reflection = tex2D(ReflectionSampler,	In.Tex[2]);
		In.Spec += Reflection;
	}

	if (bSpecMask)
	{
		In.Spec *= tex2D(SpecSampler, In.Tex[1]);
	}

	if (bShadowReceive)
	{
		if (ShadowTech == SHADOWTECH_R9700)
		{
			float CurrentDistance	= dot((float3)In.Tex[3], (float3)In.Tex[3]);
			float ShadowDistance	= tex2Dproj(ShadowSampler,		In.Tex[2]);
			float4 MaskColor		= tex2Dproj(ShadowMaskSampler,	In.Tex[2]);
			float4 Shadow			= ShadowDistance > CurrentDistance ? 1 : 0;
			
			float4 ShadowMul = saturate(Shadow + MaskColor + ShadowDensity);
			In.Diff *= ShadowMul;
			In.Spec *= ShadowMul;
		}
		
		if (ShadowTech == SHADOWTECH_GF3)
		{
			// Projection flag need to be set outside of the .fx 
			float4 Shadow		= tex2D(ShadowSampler,		In.Tex[2]);
			float4 MaskColor	= tex2D(ShadowMaskSampler,	In.Tex[3]);
	
			float4 ShadowMul = saturate(Shadow + MaskColor + ShadowDensity);
			In.Diff *= ShadowMul;
			In.Spec *= ShadowMul;
		}		
	}
	
	float4 Color;	
	Color		= (Diff * (In.Diff + AmbientLight) * 4) + In.Spec;			
	Color.a		= Diff.a;
	
	if (bTranslucy)
	{
		Color.a	= Color.a * TranslucyCoeff.a;
	}
	
	if (bHeat)
	{
		float4 Heat = tex2D(HeatSampler, In.Tex[0]);
		return Heat.aaaa;
	}
	
	if (bGlow)
	{
		float4 Glow = tex2D(GlowSampler, In.Tex[0]);
		return Glow;
	}

	
	return Color;
}

float4 main(PSIn In) : COLOR0
{
	bool bSelfIllumTex		= false;
	bool bSelfIllumColor	= false;
	bool bSpecMask		= false;
	bool bReflection	= false;
	bool bShadowCast	= false;
	bool bShadowReceive = false;
	int ShadowTech		= SHADOWTECH_NONE;	
	bool bHeat			= false;
	bool bGlow			= false;
	bool bTranslucy		= false;

	return PS(In,	
		bSelfIllumTex, bSelfIllumColor, 
		bSpecMask, bReflection, 
		bShadowCast, bShadowReceive, ShadowTech,
		bHeat, bGlow, bTranslucy);
}

VSOut mainVS(VSIn In) 
{

	int		NbOmni = 0;
	int		NbSpot = 0;
	int		NbBone = 4;
	bool	bEnvmapTexgen	= false;
	bool	bSkinNormal		= false;
	bool	bShadowCast		= false;
	bool	bShadowReceive	= false;
	int		ShadowTech		= SHADOWTECH_NONE;
	bool	bDiffUVXForm	= false;
	
	return VS(In, 
				NbOmni, NbSpot, NbBone, 
				bEnvmapTexgen, bSkinNormal, 
				bShadowCast, bShadowReceive, ShadowTech,
				bDiffUVXForm
				);
}


int CurrentVertexShader = 0;
int CurrentPixelShader	= 0;

#include "DX8ShaderArray.fx"

technique t0
{
	pass VertexLighting
	{
		VertexShader	= (VSArray[CurrentVertexShader]);
		PixelShader		= (PSArray[CurrentPixelShader]);

		FogEnable = false;
	}
	
}