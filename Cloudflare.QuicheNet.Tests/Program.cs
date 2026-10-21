using Cloudflare.QuicheNet.Tests;

NativoQuicheTests.RegisterUnidad();
PlanificadorDeCitasTests.Register();
RegressionTests.Register();
if (args.Contains("--native")) NativeTests.Register();
return await Harness.RunAsync();
