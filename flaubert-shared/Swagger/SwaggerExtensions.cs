using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace Flaubert.Shared.Swagger
{
    public static class SwaggerExtensions
    {
        public static IApplicationBuilder UseFlaubertSwagger(
            this IApplicationBuilder app,
            IWebHostEnvironment env,
            IConfiguration configuration,
            string serviceBaseRoute,
            string title)
        {
            if (env.IsDevelopment() || configuration.GetValue<bool>("EnableSwagger", true))
            {
                var cleanBase = serviceBaseRoute.Trim('/');

                app.UseSwagger(c =>
                {
                    c.RouteTemplate = $"{cleanBase}/swagger/{{documentName}}/swagger.json";

                    c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
                    {
                        // 💡 "http://..." に固定されず、ブラウザが現在アクセスしているホストとスキーム(https)をそのまま引き継ぐ設定
                        swaggerDoc.Servers = new System.Collections.Generic.List<OpenApiServer>
                        {
                            new OpenApiServer { Url = "/" }
                        };
                    });
                });

                app.UseSwaggerUI(c =>
                {
                    c.SwaggerEndpoint("v1/swagger.json", $"{title} v1");
                    c.RoutePrefix = $"{cleanBase}/swagger";
                });
            }

            return app;
        }
    }
}