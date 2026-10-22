using Npgsql;
using Porchlight.Server;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Porchlight")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Porchlight");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<TownRepository>();

// Town layouts are tens of KB of JSON (a tile map); they shrink a lot.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json"];
});

// The Unity editor and other dev hosts call the API cross-origin. Tighten this before deploying.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

await app.Services.GetRequiredService<TownRepository>().EnsureSchemaAsync(app.Lifetime.ApplicationStopping);

app.UseResponseCompression();
app.UseCors();
UnityWebHost.Map(app, builder.Configuration["Porchlight:WebRoot"]);
TownEndpoints.Map(app);

app.Run();
