float4x4	WorldViewProj : register(c0);


///////////////////////////////////////////////////////////////////////////////
struct VSIn
{
	float4	Pos		: POSITION;
	float3	tcDiff	: TEXCOORD0;
};

struct VSOut
{
	float4	Pos		: POSITION;	
	float2	tcDiff	: TEXCOORD0;
};

struct PSIn
{

};

struct PSOut
{
	float4 Col : COLOR0;
};

///////////////////////////////////////////////////////////////////////////////
VSOut VS(VSIn In)
{
	VSOut Out;

	float4 Pos = In.Pos;

	Out.Pos		= mul(Pos, WorldViewProj);
	Out.tcDiff	= (float2)In.tcDiff;
	
	return Out;
}

PSOut PS(PSIn In)
{
	PSOut Out;

	Out.Col = float4(0,1,0,1);

	return Out;
}

///////////////////////////////////////////////////////////////////////////////
technique t0
{
	pass p0
	{
		VertexShader	= compile vs_1_1 VS();
		PixelShader		= NULL;
	}
}