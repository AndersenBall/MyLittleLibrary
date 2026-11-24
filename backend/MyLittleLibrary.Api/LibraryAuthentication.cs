using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

internal static class LibraryAuthentication
{
    public static void AddLibraryAuthentication(this WebApplicationBuilder builder)
    {
        var localIdentityEnabled = builder.Configuration.GetValue<bool>("Auth:EnableLocalDevIdentity");
        if (localIdentityEnabled)
        {
            if (!builder.Environment.IsEnvironment("Local"))
                throw new InvalidOperationException("Auth:EnableLocalDevIdentity is allowed only in Local.");

            builder.Services.AddAuthentication(LocalIdentityHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, LocalIdentityHandler>(LocalIdentityHandler.SchemeName, _ => { });
        }
        else
        {
            var domain = builder.Configuration["Auth0:Domain"]?.Trim();
            var audience = builder.Configuration["Auth0:Audience"]?.Trim();
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || string.IsNullOrEmpty(audience))
                throw new InvalidOperationException("Set Auth0:Domain (a hostname) and Auth0:Audience to start without the Local identity.");

            var issuer = $"https://{domain!.ToLowerInvariant()}/";
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.Authority = issuer;
                options.Audience = audience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidIssuer = issuer;
                options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.FindFirstValue("iss") != issuer ||
                            string.IsNullOrWhiteSpace(context.Principal?.FindFirstValue("sub")))
                            context.Fail("The access token must identify a user from the configured issuer.");
                        return Task.CompletedTask;
                    }
                };
            });
        }

        builder.Services.AddAuthorizationBuilder().AddFallbackPolicy("LibraryOwner", policy => policy
            .RequireAuthenticatedUser().RequireClaim("iss").RequireClaim("sub"));
    }

    // Raven document IDs are case-insensitive. Hash the claim pair without changing its case or boundaries.
    public static string OwnerKey(ClaimsPrincipal user) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { user.FindFirstValue("iss"), user.FindFirstValue("sub") })));
}

internal sealed class LocalIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalIdentity";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", "urn:mylittlelibrary:local"), new Claim("sub", "developer")], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName)));
    }
}