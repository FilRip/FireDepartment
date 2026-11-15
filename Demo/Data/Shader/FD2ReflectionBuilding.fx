float4x4	WorldViewProj;
float4x4	World;
float4		Ambient;
float4		BurnOffset;
float3		EyePos;

texture DiffTex;
texture BurnTex;
texture LightmapTex;
texture ReflTex;

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

sampler LightmapSampler = sampler_state
{
	Texture		= (LightmapTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

sampler ReflSampler = sampler_state
{
	Texture = (ReflTex);
	MinFilter	= Linear;
	MipFilter	= Linear;
	MagFilter	= Linear;
	AddressU	= Wrap;
	AddressV	= Wrap;
	AddressW	= Wrap;
};

///////////////////////////////////////////////////////////////////////////////
struct VSIn
{
	float4	Pos					: POSITION;
	float3	Norm				: NORMAL;
	float4	Color				: COLOR0;
	float2	DiffuseTexcoord		: TEXCOORD0;
	float2	LightmapTexcoord	: TEXCOORD1;
	float2	BurnmapTexcoord		: TEXCOORD2;
};

struct VSOut
{
	float4	Pos					: POSITION;
	float4	Color				: COLOR0;
	float2	DiffuseTexcoord		: TEXCOORD0;
	float2	BurnTexcoord		: TEXCOORD1;
	float2	LightmapTexcoord	: TEXCOORD2;
	float3	ReflTexcoord		: TEXCOORD3;
};

struct PSIn
{
	float4	Color				: COLOR0;
	float2	DiffuseTexcoord		: TEXCOORD0;
	float2	BurnTexcoord		: TEXCOORD1;
	float2	LightmapTexcoord	: TEXCOORD2;
	float3	ReflTexcoord		: TEXCOORD3;
};

struct PSOut
{
	float4 Col : COLOR0;
};


///////////////////////////////////////////////////////////////////////////////
float4x4	ShadowProj;
float3		ShadowCasterPos;
float4		ShadowDensity;
float4		ShadowAmbientColor;
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
VSOut VS(VSIn In)
{
	VSOut Out;

	float4 Pos = In.Pos;
	float3 Norm = In.Norm;

	Out.Pos					= mul(Pos, WorldViewProj);
	Out.Color				= In.Color;
	Out.DiffuseTexcoord		= In.DiffuseTexcoord;
	Out.BurnTexcoord		= In.BurnmapTexcoord;
	Out.LightmapTexcoord	= In.LightmapTexcoord;
	
	float3 WorldPos		= mul(Pos, World);
	float3 WorldNorm	= normalize( mul(Norm, World) );
	float3 EyeVector	= normalize(WorldPos - EyePos);

	Out.ReflTexcoord  = reflect(EyeVector, WorldNorm);
	
	return Out;
}

PSOut PS(PSIn In)
{
	PSOut Out;

	float4 Lightmap = tex2D(LightmapSampler,	In.LightmapTexcoord);
	float4 Diffuse	= tex2D(DiffSampler,		In.DiffuseTexcoord);
	float4 Burn		= tex2D(BurnSampler,		In.BurnTexcoord);
	float4 Refl		= texCUBE(ReflSampler,		In.ReflTexcoord);

	//float4 l = (Lightmap) * 2;
	float4 r = Refl * Diffuse.aaaa;
	float4 d = lerp(Diffuse + r, Burn, In.Color);

	Out.Col.rgba = d * (Lightmap) * 2 + float4(0,0,0,1);

	return Out;
}

///////////////////////////////////////////////////////////////////////////////
VSShadowOut VSShadow(VSIn In)
{
	VSShadowOut Out;
	
	Out.Pos			= mul(In.Pos, WorldViewProj);
	Out.tShadow		= mul(In.Pos, ShadowProj);
	float3 WorldPos	= mul(In.Pos, World);
	Out.Distance	= (WorldPos - ShadowCasterPos);
	Out.tLightmap	= In.LightmapTexcoord;
	return Out;
}

PSOut PSShadow(PSShadowIn In)
{
	PSOut Out;
	
	float CurrentDistance	= dot((float3)In.Distance, (float3)In.Distance);
	float ShadowDistance	= tex2Dproj(ShadowSampler,	In.tShadow);	
	float4 light			= tex2D(LightmapSampler, In.tLightmap);
	float4 Shadow			= (1.0 - (light - ShadowAmbientColor));
	Out.Col					= ShadowDistance > CurrentDistance ? 1 : Shadow;
	return Out;
}

///////////////////////////////////////////////////////////////////////////////
VSShadowOut_NV VSShadow_NV(VSIn In)
{
	VSShadowOut_NV Out;
	
	Out.Pos			= mul(In.Pos, WorldViewProj);
	Out.tShadow		= mul(In.Pos, ShadowProj);
	Out.tLightmap	= In.LightmapTexcoord;	
	return Out;
}

PSOut PSShadow_NV(PSShadowIn_NV In)
{
	PSOut Out;
	
	float4 Shadow = tex2D(ShadowSampler,		In.tShadow);
	float4 light  = tex2D(LightmapSampler, In.tLightmap);
	float4 ShadowColor = (1.0 - (light - ShadowAmbientColor));
	Out.Col = Shadow + ShadowColor;
	
	return Out;
}

///////////////////////////////////////////////////////////////////////////////
technique t0
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VS();
		PixelShader		= compile ps_1_1 PS();
	}
	
	// ati shadow
	pass p1
	{
		VertexShader	= compile vs_2_0 VSShadow();
		PixelShader		= compile ps_2_0 PSShadow();
	}	
	
	// nvidia shadow
	pass p2
	{
		VertexShader	= compile vs_1_1 VSShadow_NV();
		PixelShader		= compile ps_1_1 PSShadow_NV();
	}	
}

///////////////////////////////////////////////////////////////////////////////
technique t1
{
	pass p0	// diffuse + lightmap
	{
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
	pass p1	// cubemap reflection
	{
		VertexShader	= NULL;
		PixelShader		= NULL;
	}
}