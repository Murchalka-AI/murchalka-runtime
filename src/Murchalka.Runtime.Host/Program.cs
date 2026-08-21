using System.Net;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Bootstrap.Composition;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Common;

var root = ReadOption(args, "--root") ?? Path.Combine(AppContext.BaseDirectory, "var");
var url = ReadOption(args, "--url") ?? "http://127.0.0.1:5078";
var endpoint = new Uri(url, UriKind.Absolute);
if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address))
    throw new InvalidOperationException("The Runtime control API must bind to an explicit HTTP loopback address.");

await using var runtime = RuntimeBootstrap.Create(root);
await runtime.Kernel.StartAsync();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(url);
var app = builder.Build();

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
