
float4x4 WorldViewProj;
float4x4 World;

texture	DiffTex;
float3	EyePos;
float   BumpScale;
float   Density;

///////////////////////////////////////////////////////////////////////////////
sampler DiffSampler = sampler_state
{
	Texture = (DiffTex);
	MinFilter = Linear;
	MagFilter = Linear;
	MipFilter = Linear;
	AddressU = Wrap;
	AddressV = Wrap;
	AddressW = Wrap;
};

///////////////////////////////////////////////////////////////////////////////
struct VSReflIn
{
	float4 Pos	: POSITION;
	float3 Norm : NORMAL;	
	float3 Tan	: TANGENT;
	float3 Bin	: BINORMAL;
	float2 Tex	: TEXCOORD0;	
};

struct VSReflOut
{
	float4 Pos	: POSITION;
	float2 Tex	: TEXCOORD0;
	float4 Tan0	: TEXCOORD1;
	float4 Tan1	: TEXCOORD2;
	float4 Tan2	: TEXCOORD3;
	float4 Col	: COLOR0;
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
	AddressU	= Clamp;
	AddressV	= Clamp;
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
VSReflOut VSRefl(VSReflIn In)
{
	VSReflOut Out;

	Out.Pos = mul(In.Pos, WorldViewProj);
	Out.Tex = In.Tex;

	float3x3 Basis;
	Basis[0] = In.Tan; 
	Basis[1] = In.Bin; 
	Basis[2] = In.Norm;

	float3x3 World33 = (float3x3)World;

	Out.Tan0.xyz = (mul(Basis, World33[0]));
	Out.Tan1.xyz = (mul(Basis, World33[1]));
	Out.Tan2.xyz = (mul(Basis, World33[2]));

	float3 WorldPos = mul(In.Pos, World);
	float3 EyeVec	= WorldPos - EyePos;

	Out.Tan0.w = EyeVec.x;
	Out.Tan1.w = EyeVec.y;
	Out.Tan2.w = EyeVec.z;

	float3 WorldNorm	= mul(In.Norm, World);
	float3 EyeVecN		= normalize(EyeVec) * - 1.0;

	float DotEyeNorm	= dot(EyeVecN,WorldNorm);
	float DotEyeNorm2	= DotEyeNorm * DotEyeNorm;

	float AlphaBias		= 1.0 + 0.5;
	Out.Col = AlphaBias - saturate(DotEyeNorm2);
	Out.Col *= Density;
	
	return Out;
}

///////////////////////////////////////////////////////////////////////////////
VSShadowOut VSShadow(VSReflIn In)
{
	VSShadowOut Out;
	Out.Pos		= mul(In.Pos, WorldViewProj);
	Out.tShadow = mul(In.Pos, ShadowProj);
	Out.tLightmap = 0; // In.tDirt;
	
	float3 WorldPos = mul(In.Pos, World);
	Out.Distance = WorldPos - ShadowCasterPos;
	
	return Out;
}

float4 PSShadow(PSShadowIn In) : COLOR0
{
	float4 Out;
	
	float CurrentDistance	= dot((float3)In.Distance, (float3)In.Distance);
	float ShadowDistance	= tex2Dproj(ShadowSampler,	In.tShadow);	
//	float4 light			= tex2D(LightSampler, In.tLightmap);
//	float4 Shadow			= (1.0 - (light - ShadowAmbientColor));
	Out						= ShadowDistance > CurrentDistance ? 1 : 0.5f;
	return Out;
}

///////////////////////////////////////////////////////////////////////////////
technique t0
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VSRefl();

		PixelShader = asm
		{
			ps.1.1

			tex				t0			// nmap
			texm3x3pad		t1, t0		// cubemap
			texm3x3pad		t2, t0		// cubemap
			texm3x3vspec	t3, t0		// cubemap

			mul r0, t3, c0	// c0    : water tint color
			mov r0.a, v0.a  // v0.a  : fresnel term			
		};
	}

	// ATI Shadow
	pass p1
	{
		VertexShader	= compile vs_2_0 VSShadow();
		PixelShader		= compile ps_2_0 PSShadow();
	}	
}

///////////////////////////////////////////////////////////////////////////////
technique t1
{
	pass p0
	{
		VertexShader	= NULL;
		PixelShader		= NULL;			
	}
}