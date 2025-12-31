using ClientInsightAPI.Services;
using ClientInsightAPI.Services.NewsProviders;
using Microsoft.OpenApi.Models;
using Npgsql;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Elmah.Io.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

//
// ===========================
// Service registration (DI)
// ===========================
//
builder.Services.AddControllers();

//
// ===========================
// Swagger / OpenAPI
// ===========================
//
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ClientInsight API",
        Version = "v1",
        Description = "Upload client JSON, import/replace data, and trigger news scans per company."
    });
});

//
// ===========================
// Services
// ===========================
//
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<CompanyReadService>();
builder.Services.AddScoped<ScanJobService>();
builder.Services.AddScoped<ArticleScanService>();
builder.Services.AddScoped<ArticleWriteService>();
builder.Services.AddScoped<ClientScanStateService>();
builder.Services.AddScoped<CompanyFeedService>();


//
// ===========================
// Providers
// ===========================
//
builder.Services.AddSingleton<INewsProvider, NoopNewsProvider>();
builder.Services.AddHttpClient<INewsProvider, GdeltDocProvider>(c =>
{
    c.BaseAddress = new Uri("https://api.gdeltproject.org");
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ClientInsightAPI/1.0");
});

//
// ===========================
// Connection String
// ===========================
//
builder.Services.AddSingleton(_ =>
{
    var cs = builder.Configuration.GetConnectionString("Db");
    return new NpgsqlDataSourceBuilder(cs).Build();
});

//
// ===========================
// Scan Pipeline
// ===========================
//
builder.Services.AddSingleton(Channel.CreateUnbounded<ScanWorkItem>());
builder.Services.AddSingleton<ScanQueue>();
builder.Services.AddHostedService<ScanWorker>();
builder.Services.AddHostedService<ScanRecoveryWorker>();


//
// ===========================
// Elmah
// ===========================
//
builder.Services.Configure<ElmahIoOptions>(builder.Configuration.GetSection("ElmahIo"));
builder.Services.AddElmahIo(); // registers dependencies :contentReference[oaicite:2]{index=2}


var app = builder.Build();

//
// ===========================
// Middleware pipeline
// ===========================
//
if (app.Environment.IsDevelopment())
{
    // Serves the OpenAPI JSON at /swagger/v1/swagger.json
    app.UseSwagger();

    // Serves the Swagger UI at /swagger
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ClientInsight API v1");
        c.RoutePrefix = "swagger"; 
    });
}

app.UseElmahIo();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
