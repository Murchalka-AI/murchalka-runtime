using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.RootSecurity.Bundles;

namespace Murchalka.Runtime.Tests.Infrastructure;

internal sealed class TestBundleBuilder : IDisposable
{
    private static readonly string[] PublicDataClasses = ["public"];
    private readonly ECDsa _publisherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Gets the publisher identifier used by generated test bundles.</summary>
    public const string Publisher = "dev.murchalka.tests";
    /// <summary>Gets the signing key identifier used by generated test bundles.</summary>
    public const string KeyId = "test-publisher";

    /// <summary>Writes the test publisher key into the runtime trust configuration.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    public void WriteTrust(RuntimePaths paths)
    {
        paths.EnsureCreated();
        var document = new JsonObject
        {
            ["publishers"] = new JsonObject
            {
                [Publisher] = new JsonObject
                {
                    ["keys"] = new JsonObject
                    {
                        [KeyId] = new JsonObject
                        {
                            ["algorithm"] = "ecdsa-p256-sha256",
                            ["publicKeyPem"] = _publisherKey.ExportSubjectPublicKeyInfoPem()
                        }
                    }
                }
            },
            ["grantAuthorities"] = new JsonObject()
        };
        File.WriteAllText(paths.TrustedPublishers, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Builds a signed module bundle for an integration test.</summary>
    /// <param name="directory">The output directory.</param>
    /// <param name="tamperArtifactAfterSigning">Whether to corrupt the artifact after signing.</param>
    /// <param name="requestProcessSpawn">Whether to request process spawning permission.</param>
    /// <param name="requireDependency">Whether to declare a missing required dependency.</param>
    /// <returns>The generated bundle location and logical digest.</returns>
    public BuiltBundle Build(string directory, bool tamperArtifactAfterSigning = false, bool requestProcessSpawn = false, bool requireDependency = false)
    {
        Directory.CreateDirectory(directory);
        var output = FindTestModuleOutput();
        var executableName = OperatingSystem.IsWindows() ? "Murchalka.Runtime.TestModule.exe" : "Murchalka.Runtime.TestModule";
        var executable = Path.Combine(output, executableName);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the test module before constructing its bundle.", executable);
        var artifactPath = "runtime/process/" + executableName;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(output).Where(path => !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            files["runtime/process/" + Path.GetFileName(file)] = File.ReadAllBytes(file);
        var artifactDigest = CanonicalBundleContent.Sha256(files[artifactPath]);

        var requestSchema = Encoding.UTF8.GetBytes("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}}}");
        var responseSchema = Encoding.UTF8.GetBytes("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"greeting\"],\"properties\":{\"greeting\":{\"type\":\"string\"}}}");
        var contract = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = "capabilities.murchalka.dev/v1",
            kind = "CapabilityContract",
            metadata = new { id = "hello.greet", version = "1.0.0", category = "examples.greeting", description = "Deterministic Phase 1 greeting fixture." },
            request = new { schema = "hello.greet.request.schema.json" },
            response = new { schema = "hello.greet.response.schema.json" },
            semantics = new { interaction = "requestResponse", sideEffect = "none", idempotency = "readOnly", streaming = false, cancellation = "required", defaultTimeout = "2s", maxPayloadBytes = 4096 },
            security = new { inputDataClasses = PublicDataClasses, outputDataClasses = PublicDataClasses, requiredPurpose = "greeting" },
            errors = Array.Empty<object>()
        });
        const string contractPath = "schemas/capabilities/hello.greet.json";
        files[contractPath] = contract;
        files["schemas/capabilities/hello.greet.request.schema.json"] = requestSchema;
        files["schemas/capabilities/hello.greet.response.schema.json"] = responseSchema;
        files["schemas/events/greeting.completed.schema.json"] = responseSchema;
        files["sbom/test.spdx.json"] = Encoding.UTF8.GetBytes("{\"spdxVersion\":\"SPDX-2.3\",\"name\":\"hello-test\"}");
        files["provenance/build.json"] = Encoding.UTF8.GetBytes("{\"builder\":\"murchalka-runtime-tests\",\"reproducible\":true}");

        var manifest = BuildManifest(artifactPath, artifactDigest, requestProcessSpawn, requireDependency);
        files["manifest/murchalka.module.yaml"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var contractDigest = CanonicalBundleContent.Sha256(contract);
        var lockDocument = BuildLock(artifactDigest, contractDigest, CanonicalBundleContent.ZeroDigest);
        var normalizedLock = JsonSerializer.SerializeToUtf8Bytes(lockDocument);
        files["manifest/module.lock.json"] = normalizedLock;

        var hashes = files.ToDictionary(pair => pair.Key, pair => CanonicalBundleContent.Sha256(pair.Value), StringComparer.Ordinal);
        hashes["manifest/module.lock.json"] = CanonicalBundleContent.Sha256(normalizedLock);
        var bundleDigest = CanonicalBundleContent.Digest(hashes);
        lockDocument["module"]!["bundleDigest"] = bundleDigest;
        files["manifest/module.lock.json"] = JsonSerializer.SerializeToUtf8Bytes(lockDocument);
        var hashDocument = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["algorithm"] = "sha256",
            ["files"] = new JsonObject(hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value)))
        };
        files["manifest/file-hashes.json"] = JsonSerializer.SerializeToUtf8Bytes(hashDocument);
        var signature = _publisherKey.SignData(CanonicalBundleContent.Create(hashes), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        files["signature/signature.json"] = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, publisher = Publisher, keyId = KeyId, algorithm = "ecdsa-p256-sha256", signature = Convert.ToBase64String(signature) });
        if (tamperArtifactAfterSigning) files[artifactPath][Math.Min(128, files[artifactPath].Length - 1)] ^= 0x01;

        var path = Path.Combine(directory, "hello-1.0.0.murchalka");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(pair.Value);
            }
        }
        return new BuiltBundle(path, bundleDigest);
    }

    private static JsonObject BuildManifest(string artifactPath, string artifactDigest, bool requestProcessSpawn, bool requireDependency)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var root = new JsonObject
        {
            ["apiVersion"] = "modules.murchalka.dev/v1",
            ["kind"] = "Module",
            ["metadata"] = new JsonObject { ["id"] = "dev.murchalka.hello-test", ["name"] = "Hello Test", ["version"] = "1.0.0", ["publisher"] = Publisher, ["description"] = "Out-of-process Phase 1 fixture.", ["license"] = "Apache-2.0" },
            ["compatibility"] = new JsonObject { ["moduleSdk"] = ">=0.1.0 <1.0.0", ["runtime"] = ">=0.1.0 <0.4.0", ["moduleProtocol"] = "1" },
            ["artifacts"] = new JsonObject { ["runtime"] = new JsonArray(new JsonObject { ["id"] = "hello-process", ["mode"] = "process", ["os"] = new JsonArray(os), ["architectures"] = new JsonArray(architecture), ["entrypoint"] = artifactPath, ["digest"] = artifactDigest, ["protocolVersion"] = 1 }) },
            ["provides"] = new JsonObject { ["capabilities"] = new JsonArray(new JsonObject { ["id"] = "hello.greet", ["category"] = "examples.greeting", ["version"] = "1.0.0", ["contract"] = "schemas/capabilities/hello.greet.json", ["execution"] = new JsonObject { ["kind"] = "requestResponse", ["idempotency"] = "readOnly", ["timeout"] = "2s" } }) },
            ["contributes"] = new JsonObject
            {
                ["events"] = new JsonObject
                {
                    ["publications"] = new JsonArray(new JsonObject { ["topic"] = "greeting.completed", ["schema"] = "schemas/events/greeting.completed.schema.json" })
                }
            },
            ["permissions"] = requestProcessSpawn ? new JsonObject { ["processes"] = new JsonObject { ["spawn"] = true } } : new JsonObject(),
            ["health"] = new JsonObject { ["startupTimeout"] = "10s", ["readiness"] = new JsonObject { ["interval"] = "1s", ["timeout"] = "2s", ["failureThreshold"] = 3 }, ["liveness"] = new JsonObject { ["interval"] = "30s", ["timeout"] = "2s", ["failureThreshold"] = 3 } },
            ["activation"] = new JsonObject { ["mode"] = "automaticWhenTrusted", ["failurePolicy"] = "keepInactive", ["hotReload"] = true, ["drainTimeout"] = "5s" }
        };
        if (requireDependency) root["requires"] = new JsonObject { ["modules"] = new JsonArray(new JsonObject { ["id"] = "dev.murchalka.missing", ["version"] = ">=1.0.0 <2.0.0" }) };
        return root;
    }

    private static JsonObject BuildLock(string artifactDigest, string contractDigest, string bundleDigest) => new()
    {
        ["schemaVersion"] = 1,
        ["module"] = new JsonObject { ["id"] = "dev.murchalka.hello-test", ["version"] = "1.0.0", ["bundleDigest"] = bundleDigest },
        ["resolvedAt"] = "2026-01-01T00:00:00Z",
        ["runtimeVersion"] = "0.1.0",
        ["dependencies"] = new JsonArray(),
        ["artifacts"] = new JsonArray(new JsonObject { ["target"] = "runtime", ["id"] = "hello-process", ["digest"] = artifactDigest }),
        ["contracts"] = new JsonArray(new JsonObject { ["id"] = "hello.greet", ["version"] = "1.0.0", ["schemaDigest"] = contractDigest })
    };

    private static string FindTestModuleOutput()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Murchalka.Runtime.slnx"))) current = current.Parent;
        if (current is null) throw new DirectoryNotFoundException("Solution root was not found.");
        return Path.Combine(current.FullName, "tests", "Murchalka.Runtime.TestModule", "bin", configuration, "net10.0");
    }

    /// <summary>Releases the test signing key.</summary>
    public void Dispose() => _publisherKey.Dispose();
}
