VertexShader GroundVSArray[4]=
{
	compile vs_1_1 VS(false,false),
	compile vs_1_1 VS(false,true),
	compile vs_1_1 VS(true,false),
	compile vs_1_1 VS(true,true),
};

PixelShader GroundPSArray[8]=
{
	compile ps_1_1 PS(false,false,false),
	compile ps_1_1 PS(false,false,true),
	compile ps_1_1 PS(false,true,false),
	compile ps_1_1 PS(false,true,true),
	compile ps_1_1 PS(true,false,false),
	compile ps_1_1 PS(true,false,true),
	compile ps_1_1 PS(true,true,false),
	compile ps_1_1 PS(true,true,true),
};

