using System;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Flaubert.Drive.Data;
using Flaubert.Drive.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<DriveDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    options.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, JwtTenantProvider>();

var awsOptions = builder.Configuration.GetAWSOptions();
var regionStr = builder.Configuration["AWS:Region"] ?? builder.Configuration["AWS_REGION"] ?? "ap-northeast-1";
awsOptions.Region = Amazon.RegionEndpoint.GetBySystemName(regionStr);
var accessKey = builder.Configuration["AWS:AccessKey"] ?? builder.Configuration["AWS_ACCESS_KEY_ID"];
var secretKey = builder.Configuration["AWS:SecretKey"] ?? builder.Configuration["AWS_SECRET_ACCESS_KEY"];
if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey)) awsOptions.Credentials = new BasicAWSCredentials(accessKey, secretKey);
builder.Services.AddAWSService<IAmazonS3>(awsOptions);
builder.Services.AddScoped<IStorageService, S3StorageService>();
builder.Services.AddScoped<IVirtualFileSystemService, VirtualFileSystemService>();
builder.Services.AddScoped<ITenantPolicyService, TenantPolicyService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();

var authAuthority = builder.Configuration["AUTH_INTERNAL_URL"] ?? "http://192.168.8.112:5001/api/auth";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = authAuthority;
    options.RequireHttpsMetadata = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
        ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.Zero
    };
});

// ★ CORS サービスの定義追加
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()   // 本番環境でドメインを制限したい場合は .WithOrigins("http://flaubert.lesure.net") に変更
              .AllowAnyMethod()   // GET, POST, OPTIONS 等の全メソッドを許可
              .AllowAnyHeader();  // Authorization 等の全ヘッダーを許可
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "flaubert-drive API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Description = "JWT Bearer token", Name = "Authorization", In = ParameterLocation.Header, Type = SecuritySchemeType.Http, Scheme = "Bearer" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedPrefix;
    options.KnownNetworks.Clear(); options.KnownProxies.Clear();
});
builder.Services.AddHealthChecks().AddDbContextCheck<DriveDbContext>("Database");

var app = builder.Build();
app.UseForwardedHeaders();
app.UsePathBase("/api/drive");
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("EnableSwagger", true))
{
    app.UseSwagger();
    app.UseSwaggerUI(c => { c.SwaggerEndpoint("/api/drive/swagger/v1/swagger.json", "flaubert-drive API v1"); c.RoutePrefix = "swagger"; });
}
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
