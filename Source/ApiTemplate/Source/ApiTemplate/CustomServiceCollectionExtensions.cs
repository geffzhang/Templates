namespace ApiTemplate;

using ApiTemplate.ConfigureOptions;
#if CORS
using ApiTemplate.Constants;
#endif
using ApiTemplate.Options;
using Boxed.AspNetCore;
#if (!ForwardedHeaders && HostFiltering)
using Microsoft.AspNetCore.HostFiltering;
#endif
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
#if OpenTelemetry
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
#endif
#if Swagger
using Swashbuckle.AspNetCore.SwaggerGen;
#endif

/// <summary>
/// <see cref="IServiceCollection"/> extension methods which extend ASP.NET Core services.
/// </summary>
internal static class CustomServiceCollectionExtensions
{
    /// <summary>
    /// Configures the settings by binding the contents of the appsettings.json file to the specified Plain Old CLR
    /// Objects (POCO) and adding <see cref="IOptions{T}"/> objects to the services collection.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The services with options services added.</returns>
    public static IServiceCollection AddCustomOptions(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services
            // ConfigureAndValidateSingleton registers IOptions<T> and also T as a singleton to the services collection.
            .ConfigureAndValidateSingleton<ApplicationOptions>(configuration)
            .ConfigureAndValidateSingleton<CacheProfileOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.CacheProfiles)))
#if ResponseCompression
            .ConfigureAndValidateSingleton<CompressionOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.Compression)))
#endif
#if ForwardedHeaders
            .ConfigureAndValidateSingleton<ForwardedHeadersOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.ForwardedHeaders)))
            .Configure<ForwardedHeadersOptions>(
                options =>
                {
                    options.KnownNetworks.Clear();
                    options.KnownProxies.Clear();
                })
#elif HostFiltering
            .ConfigureAndValidateSingleton<HostFilteringOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.HostFiltering)))
#endif
            .ConfigureAndValidateSingleton<HostOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.Host)))
#if Redis
            .ConfigureAndValidateSingleton<RedisOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.Redis)))
#endif
            .ConfigureAndValidateSingleton<KestrelServerOptions>(configuration.GetRequiredSection(nameof(ApplicationOptions.Kestrel)));

    public static IServiceCollection AddCustomConfigureOptions(this IServiceCollection services) =>
        services
#if CORS
            .ConfigureOptions<ConfigureCorsOptions>()
#endif
            .ConfigureOptions<ConfigureJsonOptions>()
#if Redis
            .ConfigureOptions<ConfigureRedisCacheOptions>()
#endif
#if ResponseCompression
            .ConfigureOptions<ConfigureResponseCompressionOptions>()
#endif
            .ConfigureOptions<ConfigureRouteOptions>();
#if HttpsEverywhere

    /// <summary>
    /// Adds the Strict-Transport-Security HTTP header to responses. This HTTP header is only relevant if you are
    /// using TLS. It ensures that content is loaded over HTTPS and refuses to connect in case of certificate
    /// errors and warnings.
    /// See https://developer.mozilla.org/en-US/docs/Web/Security/HTTP_strict_transport_security and
    /// http://www.troyhunt.com/2015/06/understanding-http-strict-transport.html
    /// Note: Including subdomains and a minimum maxage of 18 weeks is required for preloading.
    /// Note: You can refer to the following article to clear the HSTS cache in your browser:
    /// http://classically.me/blogs/how-clear-hsts-settings-major-browsers
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services with HSTS services added.</returns>
    public static IServiceCollection AddCustomStrictTransportSecurity(this IServiceCollection services) =>
        services
            .AddHsts(
                options =>
                {
                    // Preload the HSTS HTTP header for better security. See https://hstspreload.org/
#if HstsPreload
                    options.IncludeSubDomains = true;
                    options.MaxAge = TimeSpan.FromSeconds(31536000); // 1 Year
                    options.Preload = true;
#else
                    // options.IncludeSubDomains = true;
                    // options.MaxAge = TimeSpan.FromSeconds(31536000); // 1 Year
                    // options.Preload = true;
#endif
                });
#endif
#if HealthCheck

    public static IServiceCollection AddCustomHealthChecks(this IServiceCollection services) =>
        services
            .AddHealthChecks()
            // Add health checks for external dependencies here. See https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks
            .Services;
#endif
#if Versioning

    public static IServiceCollection AddCustomApiVersioning(this IServiceCollection services) =>
        services
            .AddApiVersioning(
                options =>
                {
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ReportApiVersions = true;
                })
            .AddVersionedApiExplorer(x => x.GroupNameFormat = "'v'VVV"); // Version format: 'v'major[.minor][-status]
#endif
#if OpenTelemetry

    /// <summary>
    /// Adds Open Telemetry services and configures instrumentation and exporters.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="webHostEnvironment">The environment the application is running under.</param>
    /// <returns>The services with open telemetry added.</returns>
    public static IServiceCollection AddCustomOpenTelemetryTracing(this IServiceCollection services, IWebHostEnvironment webHostEnvironment) =>
        services.AddOpenTelemetryTracing(
            builder =>
            {
                builder
                    .SetResourceBuilder(ResourceBuilder
                        .CreateEmpty()
                        .AddService(
                            webHostEnvironment.ApplicationName,
                            serviceVersion: AssemblyInformation.Current.Version)
                        .AddAttributes(
                            new KeyValuePair<string, object>[]
                            {
                                new(OpenTelemetryAttributeName.Deployment.Environment, webHostEnvironment.EnvironmentName),
                                new(OpenTelemetryAttributeName.Host.Name, Environment.MachineName),
                            })
                        .AddEnvironmentVariableDetector())
                    .AddAspNetCoreInstrumentation(
                        options =>
                        {
                            // Enrich spans with additional request and response meta data.
                            // See https://github.com/open-telemetry/opentelemetry-specification/blob/master/specification/trace/semantic_conventions/http.md
                            options.Enrich = (activity, eventName, obj) =>
                        {
                            if (obj is HttpRequest request)
                            {
                                var context = request.HttpContext;
                                activity.AddTag(OpenTelemetryAttributeName.Http.Flavor, GetHttpFlavour(request.Protocol));
                                activity.AddTag(OpenTelemetryAttributeName.Http.Scheme, request.Scheme);
                                activity.AddTag(OpenTelemetryAttributeName.Http.ClientIP, context.Connection.RemoteIpAddress);
                                activity.AddTag(OpenTelemetryAttributeName.Http.RequestContentLength, request.ContentLength);
                                activity.AddTag(OpenTelemetryAttributeName.Http.RequestContentType, request.ContentType);

                                var user = context.User;
                                if (user.Identity?.Name is not null)
                                {
                                    activity.AddTag(OpenTelemetryAttributeName.EndUser.Id, user.Identity.Name);
                                    activity.AddTag(OpenTelemetryAttributeName.EndUser.Scope, string.Join(',', user.Claims.Select(x => x.Value)));
                                }
                            }
                            else if (obj is HttpResponse response)
                            {
                                activity.AddTag(OpenTelemetryAttributeName.Http.ResponseContentLength, response.ContentLength);
                                activity.AddTag(OpenTelemetryAttributeName.Http.ResponseContentType, response.ContentType);
                            }

                            static string GetHttpFlavour(string protocol)
                            {
                                if (HttpProtocol.IsHttp10(protocol))
                                {
                                    return OpenTelemetryHttpFlavour.Http10;
                                }
                                else if (HttpProtocol.IsHttp11(protocol))
                                {
                                    return OpenTelemetryHttpFlavour.Http11;
                                }
                                else if (HttpProtocol.IsHttp2(protocol))
                                {
                                    return OpenTelemetryHttpFlavour.Http20;
                                }
                                else if (HttpProtocol.IsHttp3(protocol))
                                {
                                    return OpenTelemetryHttpFlavour.Http30;
                                }

                                throw new InvalidOperationException($"Protocol {protocol} not recognised.");
                            }
                        };
                            options.RecordException = true;
                        });
#if Redis
                builder.AddRedisInstrumentation();
#endif

                if (webHostEnvironment.IsDevelopment())
                {
                    builder.AddConsoleExporter(
                        options => options.Targets = ConsoleExporterOutputTargets.Console | ConsoleExporterOutputTargets.Debug);
                }

                // TODO: Add OpenTelemetry.Instrumentation.* NuGet packages and configure them to collect more span data.
                //       E.g. Add the OpenTelemetry.Instrumentation.Http package to instrument calls to HttpClient.
                // TODO: Add OpenTelemetry.Exporter.* NuGet packages and configure them here to export open telemetry span data.
                //       E.g. Add the OpenTelemetry.Exporter.OpenTelemetryProtocol package to export span data to Jaeger.
            });
#endif
#if Swagger

    /// <summary>
    /// Adds Swagger services and configures the Swagger services.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services with Swagger services added.</returns>
    public static IServiceCollection AddCustomSwagger(this IServiceCollection services) =>
        services
            .AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>()
            .AddSwaggerGen();
#endif
}
