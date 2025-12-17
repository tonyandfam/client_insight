using ClientInsightAPI.Services;
using Microsoft.OpenApi.Models;
using Npgsql;

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
        c.RoutePrefix = "swagger"; // <-- this makes /swagger work again
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
