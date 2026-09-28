using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using static FunctionsMcpTool.ToolsInformation;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Only enable the Azure Monitor exporter when a connection string is configured.
// Calling UseAzureMonitorExporter() unconditionally crashes the isolated worker
// at startup with "A connection string was not found" when running locally
// without APPLICATIONINSIGHTS_CONNECTION_STRING (which then surfaces as
// "dotnet.exe exited with code 0xE0434352").
var openTelemetryBuilder = builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    openTelemetryBuilder.UseAzureMonitorExporter();
}

builder.Services.AddSingleton(_ => CreateBlobServiceClient());

// Demonstrate how you can define tool properties in Program.cs
// without requiring McpToolProperty input binding attributes:
builder
    .ConfigureMcpTool(EchoToolName)
    .WithProperty(EchoMessagePropertyName, McpToolPropertyType.String, EchoMessagePropertyDescription, required: true);

// Demonstrate explicit JSON input and output schemas (Worker.Extensions.Mcp 1.5.0+).
// The tool's arguments and the shape of its structured output are advertised to MCP
// clients exactly as written here, instead of being inferred from attributes or POCOs.
builder
    .ConfigureMcpTool(SearchSnippetsToolName)
    .WithInputSchema("""
        {
            "type": "object",
            "properties": {
                "prefix": {
                    "type": "string",
                    "description": "Snippet name prefix to match. Empty string matches all snippets."
                },
                "limit": {
                    "type": "integer",
                    "description": "Maximum number of results to return (1-100).",
                    "minimum": 1,
                    "maximum": 100,
                    "default": 10
                }
            },
            "required": ["prefix"]
        }
        """)
    .WithOutputSchema("""
        {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "The prefix that was searched."
                },
                "count": {
                    "type": "integer",
                    "description": "Number of snippets returned."
                },
                "results": {
                    "type": "array",
                    "description": "Names of matching snippets.",
                    "items": { "type": "string" }
                }
            },
            "required": ["query", "count", "results"]
        }
        """);

builder.Build().Run();

static BlobServiceClient CreateBlobServiceClient()
{
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        // Azure.Storage.Blobs does not understand the Functions/WebJobs shorthand
        // "UseDevelopmentStorage=true" - translate it to the Azurite dev-store
        // connection string for local development.
        if (connectionString.Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;"
                + "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
                + "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";
        }

        return new BlobServiceClient(connectionString);
    }

    // In Azure (see infra/app/api.bicep), identity-based storage is configured via
    // AzureWebJobsStorage__blobServiceUri and AzureWebJobsStorage__clientId instead of a connection string.
    var blobServiceUri = Environment.GetEnvironmentVariable("AzureWebJobsStorage__blobServiceUri");

    if (!string.IsNullOrWhiteSpace(blobServiceUri))
    {
        var clientId = Environment.GetEnvironmentVariable("AzureWebJobsStorage__clientId");
        TokenCredential credential = !string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new DefaultAzureCredential();

        return new BlobServiceClient(new Uri(blobServiceUri), credential);
    }

    throw new InvalidOperationException(
        "AzureWebJobsStorage is not set. Set it to your storage connection string " +
        "or to \"UseDevelopmentStorage=true\" (with Azurite running) for local development, " +
        "or configure AzureWebJobsStorage__blobServiceUri for identity-based access.");
}
