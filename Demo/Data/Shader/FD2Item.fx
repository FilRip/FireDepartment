
int Version = 102;

float4x4 UVWorldViewProj;
float4x4 WorldViewProj;
float4x4 WorldView;
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
float	R9700ShadowMapBias;
float4	ShadowDensity;

float4 LightColor[4];
float4 LightPos[4];
float4 AmbientLight;


texture DiffTex;
texture SelfTex;
texture ShadowTex;
texture EnvTex;

#define SHADOWTECH_NONE		0
#define SHADOWTECH_R9700	1
#define SHADOWTECH_GF3		2


sampler DiffSampler = sampler_state {	Texture		= (DiffTex);	};
sampler SelfSampler = sampler_state {	Texture		= (SelfTex);	};
sampler EnvSampler = sampler_state {	Texture		= (EnvTex);	};

sampler ShadowSampler = sampler_state
{
	Texture		= (ShadowTex);
	AddressU	= Clamp;
	AddressV	= Clamp;
	AddressW	= Clamp;	
};



struct VSIn
{
	float4 Pos		: POSITION;
	float3 Norm		: NORMAL;
	float2 tcDiff	: TEXCOORD0;
};

struct VSOut
{
	float4 Pos		: POSITION;
	float4 Diff		: COLOR0;
	float2 tcDiff	: TEXCOORD0;
	float2 tcSelf	: TEXCOORD1;
};

struct PSIn
{
	float4 Diff		: COLOR0;		
	float2 tcDiff	: TEXCOORD0;
	float2 tcSelf	: TEXCOORD1;
};

float4 Lighting(float3 WorldNorm, float3 WorldPos, int LightIndex)
{
	float3 LightVec		= normalize((float3)LightPos[LightIndex] - WorldPos);
	
	float ndl			= dot(LightVec, WorldNorm);
	
	float LightRange	= LightPos[LightIndex].w;
	float Distance		= distance(LightPos[LightIndex], WorldPos);

	float DistOverLightRange = Distance * LightRange;
	float Attn = 1 - DistOverLightRange;

	return saturate(ndl * Attn * LightColor[LightIndex]);
}

VSOut VS(VSIn In, 
	uniform int NbOmni, 
	uniform bool bShadowCast, uniform bool bShadowReceive, uniform int ShadowTech,
	uniform bool bSelfIllum, uniform bool bEnvMap
	)
{
	VSOut Out;

	float4 Pos;
	float3 Norm;
		
	Pos		= In.Pos;
	Norm	= In.Norm;

	float3 WorldPos		= mul(Pos, World);
	float3 WorldNorm	= normalize( mul(Norm, (float3x3)World) );
	
	    	
	Out.Pos			= mul(Pos, WorldViewProj);
	Out.tcDiff		= In.tcDiff;
	Out.tcSelf		= In.tcDiff;
	if (bEnvMap)
	{
		float3 n = normalize(mul(Norm, WorldView));
		Out.tcSelf = n.xy * 0.5f + 0.5f;
	}


	Out.Diff = 0;
 
	if (bSelfIllum==false)
	{
		for (int iOmni=0; iOmni<NbOmni; ++iOmni)  
		{
			if (iOmni==0)
			{
				Out.Diff = Lighting(WorldNorm, WorldPos, iOmni);
			}
			else
			{
				Out.Diff += Lighting(WorldNorm, WorldPos, iOmni);
			}
		}
		Out.Diff += AmbientLight;
	}
	Out.Diff = max(Out.Diff, SelectionColor);
	Out.Diff = saturate(Out.Diff);
	Out.Diff.a = 1;

	return Out;
}

float4 PS(PSIn In, 
	uniform bool bShadowCast, uniform bool bShadowReceive, uniform int ShadowTech,
	uniform bool bSelfIllum, uniform bool bEnvMap, uniform bool bHilite
	) : COLOR0 
{
	float4 DiffTexel = tex2D(DiffSampler, In.tcDiff);

	float4 Lighting = bSelfIllum ? tex2D(SelfSampler, In.tcSelf) : In.Diff;
	
	float4 Output;
	Output.rgb	= DiffTexel * Lighting * 4;
	Output.a	= DiffTexel.a;
	
	if (bEnvMap)
	{
		Output.rgb += tex2D(EnvSampler, In.tcSelf) * DiffTexel.aaaa;		
	}
	if (bHilite)
	{
		//Output = lerp(Output, SelectionColor*4, 0.5f);
		Output += SelectionColor;
	}

	return Output;
}

int CurrentVertexShader = 0;
int CurrentPixelShader	= 0;

#include "FD2ItemShaderArray.fx"

VSOut main(VSIn In)
{
	int		NbOmni = 1; 
	bool	bShadowCast = false;
	bool	bShadowReceive = false;
	int		ShadowTech = 0;
	bool	bSelfIllum = false;
	bool	bEnvMap = false;
	return	VS(In,NbOmni,bShadowCast,bShadowReceive,ShadowTech,bSelfIllum,bEnvMap);
}

technique t0
{
	pass p0
	{
		VertexShader	= (ItemVSArray[CurrentVertexShader]);
		PixelShader		= (ItemPSArray[CurrentPixelShader]);
	}
}

technique t1
{
	pass p0
	{
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
}