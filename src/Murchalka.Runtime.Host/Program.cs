using System.Net;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Bootstrap.Composition;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Configuration;
using Murchalka.Runtime.Host.Bootstrap;
using Murchalka.Runtime.Host.Security;

var root = ReadOption(args, "--root") ?? Path.Combine(AppContext.BaseDirectory, "var");
var url = ReadOption(args, "--url") ?? "http://127.0.0.1:5078";
var installationId = ReadOption(args, "--installation") ?? "local";
var bootstrapBindings = ReadOption(args, "--bootstrap-bindings");
var bootstrapConfiguration = ReadOption(args, "--bootstrap-configuration");
var adminTokenPath = ReadOption(args, "--admin-token-file")
    ?? throw new InvalidOperationException("--admin-token-file is required for the Runtime control plane.");
using var adminToken = AdminTokenValidator.Load(adminTokenPath);
var endpoint = new Uri(url, UriKind.Absolute);
if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address))
    throw new InvalidOperationException("The Runtime control API must bind to an explicit HTTP loopback address.");

await using var runtime = RuntimeBootstrap.Create(root, installationId: installationId);
await runtime.Kernel.StartAsync();
await DeploymentBootstrapper.ApplyAsync(
    runtime.Kernel,
    bootstrapBindings,
    bootstrapConfiguration,
    TimeSpan.FromMinutes(2),
    CancellationToken.None);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(url);
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1") &&
        !adminToken.IsAuthorized(context.Request.Headers.Authorization.ToString()))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { code = "admin-authentication-required", message = "A valid administrative bearer token is required." });
        return;
    }

    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "ready", runtimeVersion = RuntimeConstants.Version.ToString() }));
app.MapGet("/v1/modules", async (CancellationToken cancellationToken) => Results.Ok(await runtime.Kernel.GetStatusAsync(cancellationToken)));
app.MapGet("/v1/capabilities", () => Results.Ok(runtime.Kernel.Capabilities.Snapshot().Select(value => new
{
    id = value.CapabilityId.Value,
    version = value.Version.ToString(),
    module = value.ModuleId.Value,
    instance = value.InstanceId.Value,
    category = value.Category
})));
app.MapPost("/v1/capabilities/{capabilityId}/invoke", async (string capabilityId, HttpRequest request, CancellationToken cancellationToken) =>
{
    if (request.ContentLength is > 1_048_576)
        return Results.Json(new { code = "request-too-large", message = "Administrative capability requests are limited to 1 MiB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
    try
    {
        var document = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: cancellationToken);
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("payload", out var payload))
            return Results.BadRequest(new { code = "request-invalid", message = "Property 'payload' is required." });
        var idempotencyKey = document.TryGetProperty("idempotencyKey", out var key) && key.ValueKind != JsonValueKind.Null
            ? key.GetString()
            : null;
        var scope = document.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind != JsonValueKind.Null
            ? scopeElement.Deserialize<InvocationScope>()
            : null;
        var result = await runtime.Kernel.InvokeAdministrativeCapabilityAsync(
            new CapabilityId(capabilityId),
            payload,
            scope,
            idempotencyKey,
            cancellationToken);
        return result.Status == InvocationStatus.Succeeded
            ? Results.Ok(result.Payload)
            : Results.Json(new
            {
                code = result.Error?.Code ?? "capability-failed",
                message = result.Error?.Message ?? "Administrative capability invocation failed.",
                retryable = result.Error?.Retryable ?? false
            }, statusCode: result.Error?.Category switch
            {
                ErrorCategory.InvalidRequest => StatusCodes.Status400BadRequest,
                ErrorCategory.PermissionDenied => StatusCodes.Status403Forbidden,
                ErrorCategory.NotFound => StatusCodes.Status404NotFound,
                ErrorCategory.Conflict => StatusCodes.Status409Conflict,
                ErrorCategory.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status502BadGateway
            });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { code = "capability-unavailable", message = exception.Message });
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { code = "administrative-invocation-rejected", message = exception.Message });
    }
});
app.MapGet("/v1/pipelines", () =>
{
    var snapshot = runtime.Kernel.Pipelines.Snapshot();
    return Results.Ok(new
    {
        snapshot.Revision,
        pipelines = snapshot.Pipelines.Select(pipeline => new
        {
            id = pipeline.Definition.Id,
            version = pipeline.Definition.Version,
            owner = pipeline.Definition.OwnerModule.Value,
            executable = pipeline.IsExecutable,
            stages = pipeline.Stages.Select(stage => new
            {
                id = stage.Definition.Id,
                mode = stage.Definition.Mode.ToString(),
                issue = stage.Issue,
                handlers = stage.Handlers.Select(handler => new { id = handler.HandlerId, module = handler.ModuleId.Value, instance = handler.InstanceId.Value })
            })
        })
    });
});
app.MapGet("/v1/events/quarantine", async (CancellationToken cancellationToken) =>
    Results.Ok(await runtime.Kernel.Events.GetQuarantineAsync(cancellationToken)));
app.MapPost("/v1/events/quarantine/{quarantineId}/replay", async (string quarantineId, CancellationToken cancellationToken) =>
    await runtime.Kernel.Events.ReplayAsync(quarantineId, cancellationToken) ? Results.Accepted() : Results.NotFound());
app.MapGet("/v1/bindings", async (CancellationToken cancellationToken) =>
    Results.Ok(BindingDocumentJson.Serialize(await runtime.Kernel.GetBindingsAsync(cancellationToken))));
app.MapPut("/v1/bindings", async (HttpRequest request, long expectedRevision, CancellationToken cancellationToken) =>
{
    try
    {
        var document = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: cancellationToken);
        var updated = await runtime.Kernel.ReplaceBindingsAsync(document, expectedRevision, cancellationToken);
        return Results.Ok(BindingDocumentJson.Serialize(updated));
    }
    catch (BindingRevisionConflictException exception)
    {
        return Results.Conflict(new { code = "binding-revision-conflict", expectedRevision = exception.ExpectedRevision, actualRevision = exception.ActualRevision });
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentOutOfRangeException)
    {
        return Results.BadRequest(new { code = "binding-document-invalid", message = exception.Message });
    }
});
app.MapGet("/v1/modules/{moduleId}/configuration", async (string moduleId, CancellationToken cancellationToken) =>
{
    try { return await runtime.Kernel.GetConfigurationAsync(new ModuleId(moduleId), cancellationToken) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { code = "module-id-invalid", message = exception.Message }); }
    catch (InvalidDataException exception) { return Results.Conflict(new { code = "configuration-invalid", message = exception.Message }); }
});
app.MapPut("/v1/modules/{moduleId}/configuration", async (string moduleId, HttpRequest request, long expectedRevision, CancellationToken cancellationToken) =>
{
    try
    {
        var values = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: cancellationToken);
        return await runtime.Kernel.ReplaceConfigurationAsync(new ModuleId(moduleId), values, expectedRevision, cancellationToken) is { } snapshot
            ? Results.Ok(snapshot)
            : Results.NotFound();
    }
    catch (ConfigurationRevisionConflictException exception)
    {
        return Results.Conflict(new { code = "configuration-revision-conflict", expectedRevision = exception.ExpectedRevision, actualRevision = exception.ActualRevision });
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { code = "configuration-update-rejected", message = exception.Message });
    }
});
app.MapPut("/v1/secrets/{*name}", async (string name, HttpRequest request, long expectedRevision, CancellationToken cancellationToken) =>
{
    byte[] value = [];
    try
    {
        var document = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: cancellationToken);
        value = Convert.FromBase64String(document.GetProperty("value").GetString() ?? string.Empty);
        return Results.Ok(await runtime.Kernel.PutSecretAsync(name, value, expectedRevision, cancellationToken));
    }
    catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { code = "secret-update-rejected", message = exception.Message });
    }
    finally
    {
        if (value.Length > 0) System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
    }
});
app.MapPost("/v1/modules/{moduleId}/state/{namespaceName}/export", async (string moduleId, string namespaceName, CancellationToken cancellationToken) =>
{
    try { return await runtime.Kernel.ExportStateAsync(new ModuleId(moduleId), namespaceName, cancellationToken) is { } stateExport ? Results.Ok(stateExport) : Results.NotFound(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { code = "state-export-rejected", message = exception.Message });
    }
});
app.MapPost("/v1/modules/{moduleId}/state/import/{exportId}", async (string moduleId, string exportId, CancellationToken cancellationToken) =>
{
    try { return await runtime.Kernel.ImportStateAsync(new ModuleId(moduleId), exportId, cancellationToken) ? Results.Ok() : Results.NotFound(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { code = "state-import-rejected", message = exception.Message });
    }
});
app.MapPost("/v1/modules/{moduleId}/enable", async (string moduleId, CancellationToken cancellationToken) =>
{
    try { return await runtime.Kernel.EnableAsync(new ModuleId(moduleId), cancellationToken) is { } status ? Results.Ok(status) : Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { code = "module-id-invalid", message = exception.Message }); }
});
app.MapPost("/v1/modules/{moduleId}/disable", async (string moduleId, CancellationToken cancellationToken) =>
{
    try { return await runtime.Kernel.DisableAsync(new ModuleId(moduleId), cancellationToken) is { } status ? Results.Ok(status) : Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { code = "module-id-invalid", message = exception.Message }); }
});

await app.RunAsync();

static string? ReadOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
        if (arguments[index] == name) return arguments[index + 1];
    return null;
}
