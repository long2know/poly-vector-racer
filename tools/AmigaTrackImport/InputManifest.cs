using System.Security.Cryptography;
using System.Text.Json;
using StuntRacer.Core.Compatibility;
using StuntRacer.Core.Compatibility.Tracks;

namespace AmigaTrackImport;

internal sealed record HashedInput(byte[] Bytes, long ByteLength, string Sha256)
{
    internal static HashedInput Read(string path, int maximumBytes, string role)
    {
        using var stream = File.OpenRead(path);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + count > maximumBytes)
                throw new InvalidDataException($"{role} exceeds its {maximumBytes}-byte safety limit.");
            bytes.Write(buffer, 0, count);
        }
        var result = bytes.ToArray();
        return new HashedInput(result, result.LongLength, Convert.ToHexStringLower(SHA256.HashData(result)));
    }

    internal MemoryStream Open() => new(Bytes, writable: false);

    internal static HashedInput HashOnly(string path, string role)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        long length = 0;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            length += count;
            if (length > 256L * 1024 * 1024)
                throw new InvalidDataException($"{role} exceeds its 268435456-byte safety limit.");
            hash.AppendData(buffer, 0, count);
        }
        return new HashedInput([], length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

internal sealed record ValidatedManifest(
    IReadOnlyList<CompatibilityAssetProvenance> Assets, string? RightsEvidenceSha256);

internal static class InputManifest
{
    internal static ValidatedManifest Validate(HashedInput input,
        IReadOnlyDictionary<string, HashedInput> actual, AmigaCommonData common)
    {
        using var document = JsonDocument.Parse(input.Bytes, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 16
        });
        var root = StrictObject(document.RootElement, "provenance", "schemaVersion", "assets", "rights");
        if (Integer(root["schemaVersion"], "provenance.schemaVersion") != 1)
            throw new InvalidDataException("provenance.schemaVersion must be 1.");
        var array = root["assets"];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 2 or > 64)
            throw new InvalidDataException("provenance.assets must contain 2..64 assets.");
        var assets = new Dictionary<string, CompatibilityAssetProvenance>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            var item = StrictObject(element, "provenance asset",
                "role", "byteLength", "sha256", "sourceId", "sourceRevision");
            var role = Text(item["role"], "asset.role");
            ValidateRole(role);
            var byteLength = Integer(item["byteLength"], $"asset {role}.byteLength");
            if (byteLength < 0 || (role == "layout" && byteLength != AmigaTrackLayoutReader.ByteLength))
                throw new InvalidDataException($"Asset {role} has an invalid exact byteLength.");
            var sha256 = Hash(item["sha256"], $"asset {role}.sha256");
            var sourceId = Identifier(item["sourceId"], $"asset {role}.sourceId");
            var revision = item["sourceRevision"].ValueKind == JsonValueKind.Null
                ? null : Identifier(item["sourceRevision"], $"asset {role}.sourceRevision");
            if (!assets.TryAdd(role, new CompatibilityAssetProvenance(role, byteLength, sha256, sourceId, revision)))
                throw new InvalidDataException($"Duplicate provenance role '{role}'.");
        }
        foreach (var required in new[] { "layout", "commonData" })
            if (!assets.ContainsKey(required))
                throw new AmigaTrackBlockedException($"Provenance requires the '{required}' asset.");
        foreach (var asset in assets.Values)
        {
            if (!actual.TryGetValue(asset.Role, out var file))
                throw new AmigaTrackBlockedException(
                    $"Asset '{asset.Role}' is declared but not supplied; use --asset {asset.Role}=FILE.");
            if (asset.ByteLength != file.ByteLength)
                throw new InvalidDataException($"Exact byte count mismatch for asset '{asset.Role}'.");
            if (!string.Equals(asset.Sha256, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"SHA-256 mismatch for asset '{asset.Role}'; hash the complete unmodified file.");
        }
        foreach (var role in actual.Keys)
            if (!assets.ContainsKey(role))
                throw new InvalidDataException($"Consumed asset '{role}' is missing from provenance.");
        var commonAsset = assets["commonData"];
        if (commonAsset.SourceId != common.SourceId || commonAsset.SourceRevision != common.SourceRevision)
            throw new InvalidDataException("Common-data sourceId/sourceRevision must exactly match its provenance asset.");

        var rights = StrictObject(root["rights"], "rights", "status", "evidenceSha256");
        if (Text(rights["status"], "rights.status") != "unresolved")
            throw new InvalidDataException("The importer cannot attest rights; rights.status must remain 'unresolved'.");
        var rightsHash = rights["evidenceSha256"].ValueKind == JsonValueKind.Null
            ? null : Hash(rights["evidenceSha256"], "rights.evidenceSha256");
        if (assets.TryGetValue("rightsEvidence", out var rightsAsset))
        {
            if (rightsHash != rightsAsset.Sha256)
                throw new InvalidDataException("rights.evidenceSha256 must equal the supplied rightsEvidence asset hash.");
        }
        else if (rightsHash is not null)
            throw new AmigaTrackBlockedException("rights.evidenceSha256 requires a hashed rightsEvidence asset and file.");
        return new ValidatedManifest(assets.Values.OrderBy(asset => asset.Role, StringComparer.Ordinal).ToArray(), rightsHash);
    }

    internal static void ValidateRole(string role)
    {
        if (role is "layout" or "commonData" or "executable" or "tables" or "expectedTrace" or "rightsEvidence")
            return;
        if (role.StartsWith("tables:", StringComparison.Ordinal) && role.Length is > 7 and <= 80 &&
            role.AsSpan(7).ToArray().All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            return;
        throw new InvalidDataException($"Unsupported provenance role '{role}'.");
    }

    private static Dictionary<string, JsonElement> StrictObject(
        JsonElement element, string path, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must be an object.");
        var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"{path} contains unknown field '{property.Name}'.");
            if (!result.TryAdd(property.Name, property.Value))
                throw new InvalidDataException($"{path} contains duplicate field '{property.Name}'.");
        }
        foreach (var field in fields)
            if (!result.ContainsKey(field))
                throw new InvalidDataException($"{path} is missing required field '{field}'.");
        return result;
    }

    private static long Integer(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw new InvalidDataException($"{path} must be a signed 64-bit JSON integer.");
        return value;
    }

    private static string Text(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path} must be a string.");
        var text = element.GetString()!;
        if (text.Length is < 1 or > 512 || text != text.Trim() || text.Any(char.IsControl))
            throw new InvalidDataException($"{path} must contain 1..512 trimmed characters without control characters.");
        return text;
    }

    private static string Identifier(JsonElement element, string path)
    {
        var value = Text(element, path);
        if (Path.IsPathRooted(value) || value[0] is '/' or '\\' ||
            (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
             value[2] is '/' or '\\') ||
            value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{path} must be a stable identifier, not an absolute private file path.");
        return value;
    }

    private static string Hash(JsonElement element, string path)
    {
        var value = Text(element, path);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"{path} must contain exactly 64 lowercase SHA-256 hex digits.");
        return value;
    }
}
