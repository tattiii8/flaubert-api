using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

var builder = WebApplication.CreateBuilder(args);

// ★ "sub" 等の既定マッピングを無効化
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

string jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? "dev_secret_change_me_32bytes_minimum";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "flaubert-auth",
            ValidateAudience = true,
            ValidAudience = "flaubert-data",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),

            // ★ Name は "sub"、Role は "role" を使う
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// 認証不要
app.MapGet("/data/api/{version}/ping", (string version) =>
    Results.Ok(new { ok = true, version }))
   .AllowAnonymous();

// 要認証
app.MapGet("/data/api/{version}/issues", (string version, ClaimsPrincipal user) =>
{
    var name =
        user.Identity?.Name
        ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? "unknown";

    var items = new[]
    {
        new { Id = 1, Subject = "sample", AssignedTo = name, Status = "Open" },
        new { Id = 2, Subject = "redmine-integration", AssignedTo = name, Status = "Closed" }
    };
    return Results.Ok(new { version, user = name, items });
})
.RequireAuthorization();


app.Run();
