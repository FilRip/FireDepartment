
float4	AmbientLight;
float	MaterialPower;
float4	MaterialSpecColor;

struct SLight
{
	float4 Color;
	float3 PosObjSpace;
	float3 PosWorldSpace;
	float4 Attn;	// x = near    y = 1 / (far - near)
};

SLight LightArray[4];

struct SLightIn
{
	float3 Pos;
	float3 Norm;
	float3 WorldPos;
};

struct SLightOut
{
	float4 Diff;
	float4 Spec;
};

SLightOut ComputeLighting(SLight Light, SLightIn In)
{
	SLightOut Out;
	
	float3 LightVec = normalize(Light.PosObjSpace - In.Pos);
	float3 EyeVec	= normalize(EyePosObjSpace - In.Pos);
	float3 HalfVec  = (LightVec + EyeVec) * 0.5;
	
	float ndl		= dot(LightVec, In.Norm);
	float ndh		= dot(HalfVec, In.Norm);
	float4 Result	= lit(ndl, ndh, MaterialPower);
	
	Out.Diff = Result.y * Light.Color;
	Out.Spec = Result.z * Light.Color;
	
	return Out;
}