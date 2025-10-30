using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Dapper;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// env
string JwtSecret() => Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? "dev_secret_change_me_32bytes_minimum";
string ConnStr() => Environment.GetEnvironmentVariable("REDMINE_CONN")
    ?? "Server=127.0.0.1;Database=redmine;User Id=redmine;Password=redmine;Port=3306;";

// /auth/api/{version}/login → JWT
app.MapPost("/auth/api/{version}/login", async (string version, LoginRequest req) =>
{
    await using var conn = new MySqlConnection(ConnStr());

    // admin は bool で受ける（Dapper は tinyint(1) → bool）
    var user = await conn.QuerySingleOrDefaultAsync<UserRow>(
        "SELECT id, login, hashed_password, salt, admin, status " +
        "FROM users WHERE login=@login LIMIT 1", new { login = req.Username });

    // 有効ユーザ & ローカル認証
    if (user is null || user.status != 1 || string.IsNullOrEmpty(user.hashed_password) || string.IsNullOrEmpty(user.salt))
        return Results.Unauthorized();

    if (!VerifyRedmineLocalPassword(req.Password, user.salt, user.hashed_password))
        return Results.Unauthorized();

    var claims = new List<Claim> {
        new(JwtRegisteredClaimNames.Sub, user.login),
        new("uid", user.id.ToString()),
        new("role", user.admin ? "admin" : "user")   // ← ここを bool に合わせる
    };

    var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret()));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var jwt = new JwtSecurityToken(
        issuer: "flaubert-auth",
        audience: "flaubert-data",
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(15),
        signingCredentials: creds);

    var token = new JwtSecurityTokenHandler().WriteToken(jwt);
    return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 900, version });
});

app.MapGet("/auth/api/{version}/health", (string version) =>
    Results.Ok(new { ok = true, version }))
   .AllowAnonymous();

app.Run();

static string Sha1Hex(string s)
{
    using var sha1 = SHA1.Create();
    var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(s));
    var sb = new StringBuilder(bytes.Length * 2);
    foreach (var b in bytes) sb.Append(b.ToString("x2"));
    return sb.ToString();
}

// Redmine: SHA1( salt_hex + SHA1(password) )
static bool VerifyRedmineLocalPassword(string clearPassword, string saltHex, string hashedPasswordHex)
{
    var inner = Sha1Hex(clearPassword);
    var candidate = Sha1Hex(saltHex + inner);
    return string.Equals(candidate, hashedPasswordHex, StringComparison.Ordinal);
}

record LoginRequest(string Username, string Password);
record UserRow(int id, string login, string hashed_password, string salt, bool admin, int status);
