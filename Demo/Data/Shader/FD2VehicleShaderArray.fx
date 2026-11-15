VertexShader VehicleVSArray[28]=
{
	compile vs_1_1 VS(0,false,true,2,false), // 0
	compile vs_1_1 VS(0,false,false,0,false), // 1
	compile vs_1_1 VS(0,false,false,0,true), // 2
	compile vs_2_0 VS(0,false,false,1,false), // 3
	compile vs_1_1 VS(0,false,false,2,false), // 4
	compile vs_2_0 VS(0,false,true,1,false), // 5
	compile vs_2_0 VS(0,true,false,1,false), // 6
	compile vs_1_1 VS(0,true,false,2,false), // 7
	compile vs_1_1 VS(1,false,true,2,false), // 8
	compile vs_1_1 VS(1,false,false,0,false), // 9
	compile vs_2_0 VS(1,false,false,1,false), // 10
	compile vs_1_1 VS(1,false,false,2,false), // 11
	compile vs_2_0 VS(1,false,true,1,false), // 12
	compile vs_1_1 VS(2,false,true,2,false), // 13
	compile vs_1_1 VS(2,false,false,0,false), // 14
	compile vs_2_0 VS(2,false,false,1,false), // 15
	compile vs_1_1 VS(2,false,false,2,false), // 16
	compile vs_2_0 VS(2,false,true,1,false), // 17
	compile vs_1_1 VS(3,false,true,2,false), // 18
	compile vs_1_1 VS(3,false,false,0,false), // 19
	compile vs_2_0 VS(3,false,false,1,false), // 20
	compile vs_1_1 VS(3,false,false,2,false), // 21
	compile vs_2_0 VS(3,false,true,1,false), // 22
	compile vs_1_1 VS(4,false,true,2,false), // 23
	compile vs_1_1 VS(4,false,false,0,false), // 24
	compile vs_2_0 VS(4,false,false,1,false), // 25
	compile vs_1_1 VS(4,false,false,2,false), // 26
	compile vs_2_0 VS(4,false,true,1,false), // 27
};

PixelShader VehiclePSArray[10]=
{
	compile ps_1_1 PS(false,true,2,false,false,false), // 0
	compile ps_1_1 PS(false,false,0,false,false,false), // 1
	compile ps_1_1 PS(false,false,0,false,false,true), // 2
	compile ps_1_1 PS(false,false,0,false,true,false), // 3
	compile ps_1_1 PS(false,false,0,true,false,false), // 4
	compile ps_2_0 PS(false,false,1,false,false,false), // 5
	compile ps_1_1 PS(false,false,2,false,false,false), // 6
	compile ps_2_0 PS(false,true,1,false,false,false), // 7
	compile ps_2_0 PS(true,false,1,false,false,false), // 8
	compile ps_1_1 PS(true,false,2,false,false,false), // 9
};

