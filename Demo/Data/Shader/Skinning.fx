float4x3 SkinMatrixArray[20];

struct SSkinningIn
{
	float4	Pos;
	float3	Norm;
	float	BlendWeightsArray[4];
	int		BlendIndexArray[4];
};

struct SSkinningOut
{
	float4 Pos;
	float3 Norm;
};

SSkinningOut ComputeSkinning(SSkinningIn In, int NbBone, bool bSkinNormal)
{
	SSkinningOut Out;
	
	if (NbBone == 0)
	{
		/*
		 *	no skinning, output same as input
		 */
		Out.Pos		= In.Pos;
		Out.Norm	= In.Norm;  
	}
	else 
	{
	
		int iBone; 
		float TotalWeight = 0;
		Out.Pos = float4(0,0,0,1);
		for (iBone=1; iBone<NbBone; iBone++)
		{				 
			int		BlendIndex	= In.BlendIndexArray[iBone];
			float	BlendWeight	= In.BlendWeightsArray[iBone];
			TotalWeight += BlendWeight;

			float4x3 SkinMatrix = SkinMatrixArray[BlendIndex];
			float3 SkinnedPos	= mul(In.Pos, SkinMatrix) * BlendWeight;
			
			Out.Pos.xyz += SkinnedPos;
		}
				
		int		BlendIndex	= In.BlendIndexArray[0];
		float	BlendWeight	= 1 - TotalWeight;
		float4x3 SkinMatrix = SkinMatrixArray[BlendIndex];
		float3 SkinnedPos	= mul(In.Pos, SkinMatrix) * BlendWeight;

		Out.Pos.xyz += SkinnedPos;
		
		// Normal only get the main influence
		Out.Norm = bSkinNormal ? normalize( mul(In.Norm, (float3x3)SkinMatrix) ) : In.Norm;
		
	}
	
	
	
	return Out;
}
