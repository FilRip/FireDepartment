///////////////////////////////////////////////////////////////////////////////
// available defines
//
// #define _SPECULAR_MASK
// #define _SELF_ILLUM
// 
//


float4x4 WorldViewProj;
float4x4 World;
float4x4 NormalMapUVXForm;
float4x4 DiffuseMapUVXForm;
float3 LightPosObjSpace;
float3 LightDirObjSpace;
float3 EyePosObjSpace;
float4 TopColor;
float4 TopColorSpec;
float4 BottomColor;
float BumpScale;
float3 EyePos;


texture DiffTex;
texture NormTex;
texture NormCube;

///////////////////////////////////////////////////////////////////////////////
sampler DiffSampler = sampler_state
{
	Texture = (DiffTex);
	MinFilter = Linear;
	MipFilter = Linear;
	MagFilter = Linear;
	AddressU = Wrap;
	AddressV = Wrap;
	AddressW = Wrap;
};

sampler NormSampler = sampler_state
{
	Texture = (NormTex);
	MinFilter = Linear;
	MipFilter = Linear;
	MagFilter = Linear;
	AddressU = Wrap;
	AddressV = Wrap;
	AddressW = Wrap;
};

samplerCUBE NormCubeSampler = sampler_state
{
	Texture = (NormCube);
	MinFilter = Linear;
	MipFilter = Linear;
	MagFilter = Linear;
	AddressU = Clamp;
	AddressV = Clamp;
	AddressW = Clamp;
};

///////////////////////////////////////////////////////////////////////////////
struct VSAmbientOut
{
	float4 Pos		: POSITION;
	float2 Tex		: TEXCOORD0;
};

struct VSOut
{
	float4 Pos		: POSITION;
	float2 Tex		: TEXCOORD0;
	float2 NormTex	: TEXCOORD1;
	float3 LightVec : TEXCOORD2;
	float3 HalfVec	: TEXCOORD3;
};

struct VSIn
{
	float4 Pos	: POSITION;
	float3 Norm : NORMAL;
	float2 Tex	: TEXCOORD0;
	float3 Tan	: TANGENT;
	float3 Bin	: BINORMAL;
};


struct PSOut
{
	float4 Col : COLOR0;
};

struct PSAmbientIn
{
	float2 Tex		: TEXCOORD0;	
};

struct PSIn
{
	float2 Tex		: TEXCOORD0;
	float2 NormTex	: TEXCOORD1;
	float3 LightVec : TEXCOORD2;
	float3 HalfVec	: TEXCOORD3;
};





///////////////////////////////////////////////////////////////////////////////

VSAmbientOut VSAmbient(VSIn In)
{
	VSAmbientOut Out;

	Out.Pos				= mul(WorldViewProj, In.Pos);

#ifdef _DIFFUSEMAP_XFORM	
	Out.Tex             = (float2)mul(DiffuseMapUVXForm, float4(In.Tex,0,1));
#else
	Out.Tex				= In.Tex;
#endif
	
	return Out;
}

VSOut VS(VSIn In)
{
	VSOut Out;

	Out.Pos				= mul(WorldViewProj, In.Pos);

#ifdef _DIFFUSEMAP_XFORM	
	Out.Tex             = (float2)mul(DiffuseMapUVXForm, float4(In.Tex,0,1));
#else
	Out.Tex				= In.Tex;
#endif


#ifdef _NORMALMAP_XFORM
	Out.NormTex			= (float2)mul(NormalMapUVXForm, float4(In.Tex,0,1));
#else
	Out.NormTex			= In.Tex;
#endif

	float3 EyeVector	= normalize(EyePosObjSpace - In.Pos);
	float3 LightVec		= normalize(LightPosObjSpace - In.Pos);

	float3 HalfVector	= (EyeVector + LightVec) * 0.5;

	float3x3 Basis;
	Basis[0] = In.Tan;
	Basis[1] = In.Bin;
//	Basis[2] = In.Norm;	

//	Basis[0] = In.Tan;
//	Basis[1] = In.Bin;
	Basis[2] = cross(In.Bin, In.Tan);

	Out.LightVec		= mul(Basis, LightVec);
	Out.HalfVec			= mul(Basis, HalfVector);

	return Out;
}


///////////////////////////////////////////////////////////////////////////////
PSOut PSAmbient(PSAmbientIn In)
{
	PSOut Out;

	float4 DiffTex = tex2D(DiffSampler, In.Tex);

	Out.Col		= DiffTex * BottomColor;	
	
	return Out;
}


PSOut PS(PSIn In)
{
	PSOut Out;

	float4 DiffTex  = tex2D(DiffSampler, In.Tex);
	float4 Norm		= tex2D(NormSampler, In.NormTex);
	float3 LightVec = texCUBE(NormCubeSampler, In.LightVec) * 2 - 1;
	float3 HalfVec  = texCUBE(NormCubeSampler, In.HalfVec) * 2 - 1;

	float4 ndh = saturate(dot(Norm * 2 - 1, HalfVec)) * Norm.a;
	ndh *= (ndh * 2 - 1) * (ndh * 2 - 1);
	ndh *= TopColorSpec; 
	
	float4 ndl;
	ndl.rgb	= saturate(dot(Norm * 2 - 1, LightVec)) * DiffTex * TopColor;
	ndl.a	= DiffTex.a;

	Out.Col = ndl + ndh;

	return Out;
}



///////////////////////////////////////////////////////////////////////////////
// Cubemap Bump Reflection
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
//	float4 Diff : COLOR0;
};

VSReflOut VSRefl(VSReflIn In)
{
	VSReflOut Out;

	Out.Pos = mul(WorldViewProj, In.Pos);
	float3 WorldPos = mul(World, In.Pos);
	
	float3x3 Basis;
	Basis[0] = In.Tan * BumpScale;
	Basis[1] = In.Bin * BumpScale;
	Basis[2] = In.Norm;

	float3x3 World33 = (float3x3)World;

	Out.Tan0.xyz = (mul(Basis, World33[0]));
	Out.Tan1.xyz = (mul(Basis, World33[1]));
	Out.Tan2.xyz = (mul(Basis, World33[2]));

	Out.Tan0.w = EyePos.x - WorldPos.x;
	Out.Tan1.w = EyePos.y - WorldPos.y;
	Out.Tan2.w = EyePos.z - WorldPos.z;

	Out.Tex = In.Tex;



	return Out;
}



///////////////////////////////////////////////////////////////////////////////
technique t0
{
	
	// ambient
	pass p0
	{
		VertexShader = compile vs_1_1 VSAmbient();
		PixelShader = compile ps_1_1 PSAmbient();
	}

	// omni
	pass p1
	{
		VertexShader = compile vs_1_1 VS();
		PixelShader = compile ps_1_1 PS();
	}

#ifdef _CUBE_REFLECTION
	// refl
	pass p2
	{
		VertexShader = compile vs_1_1 VSRefl();
		PixelShader = asm
		{
			ps.1.1

			tex				t0
			texm3x3pad		t1, t0
			texm3x3pad		t2, t0
			texm3x3vspec	t3, t0

			mul r0, t3, c0
			mul r0, r0, t0.aaaa
		};
	}
#endif
}
