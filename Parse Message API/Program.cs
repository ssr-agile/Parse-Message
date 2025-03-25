using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Parse_Message_API.Data;
using Parse_Message_API.Services;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register Redis as a singleton service
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"]));

builder.Services.AddDbContext<ApiContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgresConnection")).LogTo(Console.WriteLine, LogLevel.Information));


builder.Services.AddSingleton<RedisCacheServices>();
builder.Services.AddSingleton<MessageProducer>();
builder.Services.AddHostedService<MessageConsumer>();

builder.Logging.AddConsole();

builder.Host.UseSerilog((context, config) =>
{
    config.WriteTo.File("logs/errors.log", rollingInterval: RollingInterval.Day);
});

var app = builder.Build();

app.MapGet("/", () => "Hello, .Net!");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
