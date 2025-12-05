using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using static FunctionsSnippetTool.ToolsInformation;

namespace FunctionsSnippetTool;

public class HelloTool
{
    private readonly ILogger<HelloTool> _logger;
    private readonly IOboTokenService _obo;
    private static readonly HttpClient _http = new HttpClient();

    public HelloTool(ILogger<HelloTool> logger, IOboTokenService obo)
    {
        _logger = logger;
        _obo = obo;
    }

    [Function(nameof(SayHello))]
    public async Task<string> SayHello(
        [McpToolTrigger(HelloToolName, HelloToolDescription)]
        ToolInvocationContext context,

        // Optional tool parameter exposed to Copilot Studio
        [McpToolProperty("name", "string", "Person to greet", Required = false)]
        string? name,

        FunctionContext fc)
    {
        _logger.LogInformation("Saying hello");

        // Get the underlying HttpRequestData from FunctionContext
        var req = await fc.GetHttpRequestDataAsync();
        if (req is null)
        { 
            return "Could not resolve HttpRequestData from FunctionContext.";
        }

        string? userJwt =
            (req.Headers.TryGetValues("X-MS-TOKEN-AAD-ACCESS-TOKEN", out var v1) ? v1.FirstOrDefault() : null)
            ?? (req.Headers.TryGetValues("Authorization", out var v2) ? v2.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase) : null);
        // Decode selected claims from the incoming user token for output
        string? claimName = null;
        string? claimPreferredUsername = null;
        string? claimOid = null;
        string? claimTid = null;
        string? claimScopes = null;
        if (!string.IsNullOrEmpty(userJwt))
        {
            try
            {
                var claims = DecodeJwtPayload(userJwt);
                claims.TryGetValue("name", out claimName);
                claims.TryGetValue("preferred_username", out claimPreferredUsername);
                claims.TryGetValue("oid", out claimOid);
                claims.TryGetValue("tid", out claimTid);
                // scope (v2) or scp (delegated), roles (app roles)
                if (!claims.TryGetValue("scp", out claimScopes))
                {
                    claims.TryGetValue("roles", out claimScopes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decode JWT payload.");
            }
        }

        string? displayName = null;

        try
        {
            if (!string.IsNullOrEmpty(userJwt))
            {
                // 2) On-behalf-of flow to get a Graph token
                var graphToken = await _obo.AcquireOnBehalfOfAsync(userJwt, fc.CancellationToken);

                // 3) Call Microsoft Graph to get the user's display name
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
                var meJson = await _http.GetStringAsync("https://graph.microsoft.com/v1.0/me?$select=displayName", fc.CancellationToken);
                // Lightweight extraction to avoid pulling in JSON deps
                // Expect: {"@odata.context":"...","displayName":"John Doe"}
                var marker = "\"displayName\":\"";
                var start = meJson.IndexOf(marker, StringComparison.Ordinal);
                if (start >= 0)
                {
                    start += marker.Length;
                    var end = meJson.IndexOf('"', start);
                    if (end > start) displayName = meJson.Substring(start, end - start);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve user via Graph. Proceeding without user context.");
        }

        // 4) Build the greeting
        var who = !string.IsNullOrWhiteSpace(name) ? name
            : !string.IsNullOrWhiteSpace(displayName) ? displayName
            : "världen";

        var message = $"Tja {who}! Jag är Keen Test MCP‑verktyg.";

        // 5) Return structured output for MCP
        return JsonSerializer.Serialize(new
        {
            message,
            resolvedUser = displayName,
            providedName = name,
            token = new
            {
                name = claimName,
                preferred_username = claimPreferredUsername,
                oid = claimOid,
                tid = claimTid,
                scopes_or_roles = claimScopes
            }
        });
    }

    private static Dictionary<string, string?> DecodeJwtPayload(string jwt)
    {
        // Expect header.payload.signature
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new ArgumentException("Invalid JWT format");
        var payloadJson = Base64UrlDecodeToString(parts[1]);
        var doc = JsonDocument.Parse(payloadJson);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var n) ? n.ToString() : prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => prop.Value.GetRawText()
            };
        }

        return dict;
    }

    private static string Base64UrlDecodeToString(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        var bytes = Convert.FromBase64String(s);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}