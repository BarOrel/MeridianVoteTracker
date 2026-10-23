using System.Text.Json.Serialization;
using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Api.ErrorHandling;
using MeridianVoteTracker.Api.Middleware;
using MeridianVoteTracker.Application;
using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);

// Secure by default: every controller action requires an authenticated user unless marked
// [AllowAnonymous]; role-specific actions add [Authorize(Policy = ...)].
builder.Services
    .AddControllers(options => options.Filters.Add(new AuthorizeFilter()))
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddUniformProblemDetails();
builder.Services.AddExceptionHandler<UseCaseExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlPath = Path.Combine(AppContext.BaseDirectory, "MeridianVoteTracker.Api.xml");
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    var userScheme = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = XUserAuthenticationHandler.HeaderName,
        Description = "Pre-defined user id: dana, omar (Analyst) or lena (Admin).",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = XUserAuthenticationHandler.SchemeName },
    };
    options.AddSecurityDefinition(XUserAuthenticationHandler.SchemeName, userScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [userScheme] = [] });
});

const string WebCorsPolicy = "web";
builder.Services.AddCors(options => options.AddPolicy(WebCorsPolicy, policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

builder.Services
    .AddAuthentication(XUserAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, XUserAuthenticationHandler>(XUserAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder().AddRolePolicies();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

// The OpenAPI document is the contract the Angular client is generated from,
// so it is exposed in every environment (this is an internal tool).
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors(WebCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthCheckTags.Ready),
}).AllowAnonymous();

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    await app.Services.InitializeDatabaseAsync();
}

app.Run();

public partial class Program;
