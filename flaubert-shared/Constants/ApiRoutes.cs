namespace Flaubert.Shared.Constants;

public static class ApiRoutes
{
    // アプリケーション全体のプレフィックス
    public const string Prefix = "api";

    // OpenID Connect / OAuth2 規格準拠のエンドポイント（ルーティング直下）
    //public static class Oidc
    //{
    //    public const string Base = Prefix + "/auth";
    //    public const string OpenIdConfig = ".well-known/openid-configuration";
    //    public const string Jwks = ".well-known/jwks";
    //    public const string Token = "connect/token";
    //}

    // Deleuze 独自の業務/リソース API (/api/{service}/internal 形式)
    public static class Auth
    {
        public const string Base = Prefix + "/auth";
        public const string InternalBase = Base + "/internal"; // -> "api/auth/internal"
        public const string OpenIdConfig = ".well-known/openid-configuration";
        public const string Jwks = ".well-known/jwks";
        public const string Token = "connect/token";
    }

    public static class Management
    {
        public const string Base = Prefix + "/mng";            // -> "api/mng"
        public const string InternalBase = Base + "/internal";
        public const string System = Base + "/system";         // -> "api/mng/system"
        public const string Tenants = Base + "/tenants";       // -> "api/mng/tenants"
        public const string Users = Base + "/users";           // -> "api/mng/users"
    }
}