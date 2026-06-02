using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SentimentAnalysis.Api.Data;
using SentimentAnalysis.Api.Options;
using SentimentAnalysis.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? ResolveDefaultConnectionString(builder.Environment);
Directory.CreateDirectory(Path.GetDirectoryName(connectionString.Replace("Data Source=", string.Empty, StringComparison.OrdinalIgnoreCase)) ?? builder.Environment.ContentRootPath);
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientCors", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5000", "http://localhost:5173", "https://localhost:5001", "https://localhost:7040", "https://localhost:51997", "http://localhost:51998"];
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IPdfTextExtractor, RealPdfTextExtractor>();
builder.Services.AddScoped<IFeedbackParser, FeedbackParser>();
builder.Services.AddHttpClient<ISentimentAnalyzer, OpenAISentimentAnalyzer>();
builder.Services.AddScoped<IJobProcessor, JobProcessor>();
builder.Services.AddHostedService<QueuedJobWorker>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Sentiment Analysis API",
        Version = "v1",
        Description = "Uploads customer-feedback PDFs, tracks analysis jobs, and returns structured sentiment-analysis results."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors("ClientCors");
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Sentiment Analysis API v1");
    options.RoutePrefix = "swagger";
});

app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();
app.MapControllers();

app.Run();

static string ResolveDefaultConnectionString(IWebHostEnvironment environment)
{
    if (Directory.Exists("/home/data"))
    {
        return "Data Source=/home/data/consumer_sentiment.db";
    }

    return $"Data Source={Path.Combine(environment.ContentRootPath, "App_Data", "consumer_sentiment.db")}";
}

public partial class Program;
