///////////////////////////////////////////////////////////////////////////////


float4x4 WorldViewProj;
float4x4 World;

texture DiffTex0;
texture DiffTex1;

float4 Coeff;
float4 Ambient;
float4 SelfIllum;

float4 LightPosObjSpace;
float4 LightColor;

float3 EyePos;
float  R9700ShadowMapBias;



sampler DiffSampler0 = sampler_state
{
	Texture = (DiffTex0);
	MinFilter = Linear;
	MipFilter = Linear;
	MagFilter = Linear;
	AddressU = Wrap;
	AddressV = Wrap;
	AddressW = Wrap;
};

sampler DiffSampler1 = sampler_state
{
	Texture = (DiffTex1);
	MinFilter = Linear;
	MipFilter = Linear;
	MagFilter = Linear;
	AddressU = Wrap;
	AddressV = Wrap;
	AddressW = Wrap;
};



struct VSIn
{
	float4 Pos	: POSITION;
	float3 Norm : NORMAL;
	float2 Tex	: TEXCOORD0;
};

struct VSOut
{
	float4 Pos		: POSITION;
	float4 Color	: COLOR0;
	float2 Tex[2]	: TEXCOORD0;
};

struct PSIn
{
	float4 Color	: COLOR0;
	float2 Tex[2]	: TEXCOORD0;
};

VSOut VS(VSIn In)
{
	VSOut Out;

	Out.Pos		= mul(WorldViewProj, In.Pos);

	Out.Tex[0]	= In.Tex;
	Out.Tex[1]	= In.Tex;

	Out.Color	= Ambient;

	float3 LightVec = normalize(LightPosObjSpace - In.Pos);
	float4 Diff;
	
	Diff.rgb	= saturate( dot(In.Norm, LightVec) ) * LightColor;
	Diff.a		= 1.0f;

	Out.Color += Diff;
	
	Out.Color = max(Out.Color, SelfIllum);


	return Out;
}


float4 PS(PSIn In) : COLOR0
{
	float4 t0 = tex2D(DiffSampler0, In.Tex[0]);
	float4 t1 = tex2D(DiffSampler1, In.Tex[1]);

	return lerp(t0, t1, Coeff) * In.Color * 4;
}

technique t0
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VS();
		PixelShader		= compile ps_1_1 PS();
		
		FogEnable = false;

		AlphaTestEnable = true;
		AlphaRef		= 255;
		AlphaFunc		= GreaterEqual;
	}
}



///////////////////////////////////////////////////////////////////////////////

struct VSGF3ShadowMapIn
{
	float4 Pos	: POSITION;
	float2 Tex	: TEXCOORD0;
};

struct VSGF3ShadowMapOut
{
	float4 Pos		: POSITION;
	float2 Tex[2]	: TEXCOORD0;
};

struct PSGF3ShadowMapIn
{
	float2 Tex[2]	: TEXCOORD0;
};

VSGF3ShadowMapOut VSGF3ShadowMap(VSGF3ShadowMapIn In)
{
	VSGF3ShadowMapOut Out;
	
	Out.Pos = mul(WorldViewProj, In.Pos);
	Out.Tex[0] = In.Tex;
	Out.Tex[1] = In.Tex;
	
	return Out;
}

float4 PSGF3ShadowMap(PSGF3ShadowMapIn In) : COLOR0
{
	float4 t0 = tex2D(DiffSampler0, In.Tex[0]);
	float4 t1 = tex2D(DiffSampler1, In.Tex[1]);
	
	return lerp(t0, t1, Coeff);
}

technique tGF3ShadowMap
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VSGF3ShadowMap();
		PixelShader		= compile ps_1_1 PSGF3ShadowMap();
		
		
		FogEnable = false;

		AlphaTestEnable = true;
		AlphaRef		= 255;
		AlphaFunc		= GreaterEqual;
	}
}



///////////////////////////////////////////////////////////////////////////////

struct VSR9700ShadowMapIn
{
	float4 Pos	: POSITION;
	float2 Tex	: TEXCOORD0;
};

struct VSR9700ShadowMapOut
{
	float4 Pos		: POSITION;
	float2 Tex[2]	: TEXCOORD0;
	float3 LightVec	: TEXCOORD2;
};

struct PSR9700ShadowMapIn
{
	float2 Tex[2]	: TEXCOORD0;
	float3 LightVec	: TEXCOORD2;
};

VSR9700ShadowMapOut VSR9700ShadowMap(VSR9700ShadowMapIn In)
{
	VSR9700ShadowMapOut Out;
	
	Out.Pos		= mul(WorldViewProj, In.Pos);
	Out.Tex[0]	= In.Tex;
	Out.Tex[1]	= In.Tex;

	float3 WorldPos = mul(World, In.Pos);
	Out.LightVec	= (WorldPos - EyePos) * R9700ShadowMapBias;
	
	return Out;
}

float4 PSR9700ShadowMap(PSR9700ShadowMapIn In) : COLOR0
{
	float4 t0 = tex2D(DiffSampler0, In.Tex[0]);
	float4 t1 = tex2D(DiffSampler1, In.Tex[1]);

	
	const float BiasCoeff	= 0.25;
	const float4 Bias		= float4(0, 0, 0, BiasCoeff);
	clip( lerp(t0, t1, Coeff) - Bias );		// discard the rendering if any component is less than zero

	return dot(In.LightVec, In.LightVec);	// output distance to the light
}

technique tR9700ShadowMap
{
	pass p0
	{
		VertexShader	= compile vs_2_0 VSR9700ShadowMap();
		PixelShader		= compile ps_2_0 PSR9700ShadowMap();
		
		FogEnable		= false;
		
		AlphaTestEnable = true;
		AlphaRef		= 255;
		AlphaFunc		= GreaterEqual;
	}
}
