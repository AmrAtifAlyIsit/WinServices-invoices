using EthraiOrderFixService;
using EthraiOrderFixService.Services;
using EthraiOrderFixService.Workers;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Runtime.InteropServices;
using Serilog;

// Set working directory to the executable's location (for Windows Service)
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json")
        .Build())
    .CreateLogger();

try
{
    Log.Information("=================================================");
    Log.Information("Starting Ethrai Order Fix Service");
    Log.Information("=================================================");

var builder = Host.CreateApplicationBuilder(args);

// Add Serilog
builder.Services.AddSerilog();

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Ethrai Order Fix Service";
    });
}

// Worker config
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("WorkerOptions"));

// Mongo Client
builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(builder.Configuration.GetConnectionString("MongoConnection")));

// Register Mongo Databases
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase("profiledb"));

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase("coursesdb"));

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase("userdatadb"));

// Register Mongo Collections
builder.Services.AddSingleton(sp =>
{
    var profileDb = sp.GetServices<IMongoDatabase>()
        .First(db => db.DatabaseNamespace.DatabaseName == "profiledb");

    return profileDb.GetCollection<BsonDocument>("profile");
});

builder.Services.AddSingleton(sp =>
{
    var userDb = sp.GetServices<IMongoDatabase>()
        .First(db => db.DatabaseNamespace.DatabaseName == "userdatadb");

    return userDb.GetCollection<BsonDocument>("Order");
});

// Application services
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<CourseService>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<CouponService>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<TransactionService>();

// Worker
builder.Services.AddHostedService<OrderFixWorker>();

var app = builder.Build();

Log.Information("Application built successfully. Starting host...");

app.Run();

Log.Information("Application stopped.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
