

float4x4 WorldViewProj;
float4x4 World;
float3 EyePos;
texture NormTex;
texture CubeTex;
float BumpScale;

///////////////////////////////////////////////////////////////////////////////
sampler NormSampler = sampler_state
{
	Texture = (NormTex);
};

samplerCUBE CubeSampler = sampler_state
{
	Texture = (CubeTex);
};

///////////////////////////////////////////////////////////////////////////////
struct VSIn
{
	float4 Pos	: POSITION;
	float3 Norm : NORMAL;	
	float3 Tan	: TANGENT;
	float3 Bin	: BINORMAL;
	float2 Tex	: TEXCOORD0;
};

struct VSOut
{
	float4 Pos	: POSITION;
	float2 Tex	: TEXCOORD0;
	float4 Tan0	: TEXCOORD1;
	float4 Tan1	: TEXCOORD2;
	float4 Tan2	: TEXCOORD3;
};


///////////////////////////////////////////////////////////////////////////////
VSOut VS(VSIn In)
{
	VSOut Out;

	Out.Pos = mul(WorldViewProj, In.Pos);

	
	float3x3 Basis;
	Basis[0] = In.Tan * BumpScale;
	Basis[1] = In.Bin * BumpScale;
	Basis[2] = In.Norm;

	float3x3 World33 = (float3x3)World;

	Out.Tan0.xyz = (mul(Basis, World33[0]));
	Out.Tan1.xyz = (mul(Basis, World33[1]));
	Out.Tan2.xyz = (mul(Basis, World33[2]));

	Out.Tan0.w = EyePos.x;
	Out.Tan1.w = EyePos.y;
	Out.Tan2.w = EyePos.z;

	Out.Tex = In.Tex;

	return Out;
}


///////////////////////////////////////////////////////////////////////////////

technique t0
{
	pass p0
	{
		VertexShader = compile vs_1_1 VS();
		PixelShader = asm
		{
			ps.1.1

			tex				t0
			texm3x3pad		t1, t0
			texm3x3pad		t2, t0
			texm3x3vspec	t3, t0

			;mul_x2 r0, t3, c0
			mov r0, t3
		};
	}
}