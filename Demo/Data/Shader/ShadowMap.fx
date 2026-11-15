

float4x4	WorldViewProj;
float4x4	World;

float3		EyePos;
float		Bias;


///////////////////////////////////////////////////////////////////////////////
struct VSInDistance
{
	float4 Pos : POSITION;
};

struct VSOutDistance
{
	float4 Pos : POSITION;
	float3 LightVec : TEXCOORD0;
};

struct PSInDistance
{
	float3 LightVec : TEXCOORD0;
};

struct PSOutDistance
{
	float4 Distance : COLOR0;
};

/////////////////////////////////////////////////////////////////////////////////
VSOutDistance VSDistance(VSInDistance In)
{
	VSOutDistance Out;

	Out.Pos = mul(WorldViewProj, In.Pos);

	float3 WorldPos = mul(World, In.Pos);
	Out.LightVec = (WorldPos - EyePos) * Bias;

	return Out;
}

PSOutDistance PSDistance(PSInDistance In)
{
	PSOutDistance Out;

	Out.Distance = dot(In.LightVec, In.LightVec);
	return Out;
}




///////////////////////////////////////////////////////////////////////////////
struct VSInZBuffer
{
	float4 Pos : POSITION;
};

struct VSOutZBuffer
{
	float4 Pos : POSITION;
};

VSOutZBuffer VSZBuffer(VSInZBuffer In)
{
	VSOutZBuffer Out;

	Out.Pos = mul(WorldViewProj, In.Pos);

	return Out;
}

float4 PSZBuffer() : COLOR0
{
	return float4(1,1,1,1);
}

///////////////////////////////////////////////////////////////////////////////

/*
 *	Radeon rendering pass
 */
technique t0
{
	pass p0
	{
		VertexShader	= compile vs_2_0 VSDistance();
		PixelShader		= compile ps_2_0 PSDistance();
	}	
}

/*
 *	Nvidia rendering pass
 */ 
technique t1
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VSZBuffer();
		PixelShader		= compile ps_1_1 PSZBuffer();
	}	
}