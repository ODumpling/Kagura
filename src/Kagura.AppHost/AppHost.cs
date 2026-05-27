var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Kagura_Api>("kagura-api");

builder.AddNpmApp("kagura-web", "../../web/kagura-web", "dev")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("VITE_API", api.GetEndpoint("http"))
    .WithHttpEndpoint(port: 5173, isProxied: false)
    .WithExternalHttpEndpoints();

builder.Build().Run();
