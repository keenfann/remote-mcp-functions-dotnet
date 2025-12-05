using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using static FunctionsSnippetTool.ToolsInformation;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.EnableMcpToolMetadata();

builder.Services.Configure<OboOptions>(builder.Configuration.GetSection("Obo"));
builder.Services.AddSingleton<IOboTokenService, OboTokenService>();
builder.Build().Run();


// Options for OBO (configured from app settings or environment variables)
public sealed class OboOptions
{
    public string TenantId { get; set; } = string.Empty;
    // This is the backend API app registration (the audience your Function validates)
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    // Scopes for downstream APIs (e.g., Graph .default)
    public string[] DownstreamScopes { get; set; } = new[] { "https://graph.microsoft.com/.default" };
}

public interface IOboTokenService
{
    Task<string> AcquireOnBehalfOfAsync(string userJwt, CancellationToken ct);
}

public sealed class OboTokenService : IOboTokenService
{
    private readonly OboOptions _opt;
    private readonly IConfidentialClientApplication _cca;

    public OboTokenService(IOptions<OboOptions> options)
    {
        _opt = options.Value;
        _cca = ConfidentialClientApplicationBuilder
            .Create(_opt.ClientId)
            .WithClientSecret(_opt.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_opt.TenantId}")
            .Build();
    }

    public async Task<string> AcquireOnBehalfOfAsync(string userJwt, CancellationToken ct)
    {
        var result = await _cca
            .AcquireTokenOnBehalfOf(_opt.DownstreamScopes, new UserAssertion(userJwt))
            .ExecuteAsync(ct);
        return result.AccessToken;
    }
}