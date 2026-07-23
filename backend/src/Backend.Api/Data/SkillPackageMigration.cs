using System.Collections;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Backend.Api.Data;

/// <summary>
/// 05-standard-conformance 的一次性資料升級器。
/// 這不是 import validator：新輸入仍只由 workflow 判讀。它只把已通過舊版 validator、
/// 已存在 DB 的 legacy agentic frontmatter 投影成新標準，並保留所有非 SKILL.md entry bytes。
/// </summary>
internal static partial class SkillPackageMigration
{
    private static readonly Encoding Utf8Strict =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .DisableAliases()
        .Build();

    internal sealed record MigratedPackage(byte[] Bytes, string? CanonicalDefinition);

    public static bool DefinitionNameMatches(string definition, string targetName)
    {
        try
        {
            var scalar = LocateUniqueRootScalar(definition, "name");
            return string.Equals(scalar.Value, targetName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsStandardAgenticCanonical(string definition, string targetName)
    {
        try
        {
            return IsStandardAgentic(DeserializeMap(definition), targetName);
        }
        catch
        {
            return false;
        }
    }

    public static bool PackageNeedsRewrite(byte[] package, string targetName, string kind)
    {
        try
        {
            using var input = new ZipArchive(
                new MemoryStream(package, writable: false), ZipArchiveMode.Read);
            var entry = input.GetEntry("SKILL.md");
            if (entry is null)
            {
                return true;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Utf8Strict, detectEncodingFromByteOrderMarks: true);
            var match = FrontmatterRegex().Match(reader.ReadToEnd());
            if (!match.Success)
            {
                return true;
            }

            var root = DeserializeMap(match.Groups["frontmatter"].Value);
            return string.Equals(kind, "agentic", StringComparison.Ordinal)
                ? !IsStandardAgentic(root, targetName)
                : !string.Equals(Scalar(Get(root, "name")), targetName, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    public static MigratedPackage Rewrite(
        byte[] package, string targetName, string kind, string? canonicalDefinition = null)
    {
        using var input = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        var entries = input.Entries.Select(ReadEntry).ToList();
        var skillMd = entries.SingleOrDefault(e => e.Name == "SKILL.md")
            ?? throw new InvalidDataException("既有 Skill package 缺少 root SKILL.md");
        var source = Utf8Strict.GetString(skillMd.Bytes);

        string rewritten;
        string? canonical = null;
        if (string.Equals(kind, "agentic", StringComparison.Ordinal))
        {
            (rewritten, canonical) = RewriteAgenticSkillMd(
                source, targetName, canonicalDefinition);
        }
        else
        {
            rewritten = RewriteFlowSkillMd(source, targetName);
        }

        if (string.Equals(source, rewritten, StringComparison.Ordinal))
        {
            return new MigratedPackage(package, canonical);
        }

        skillMd.Bytes = Utf8Strict.GetBytes(rewritten);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var stored in entries)
            {
                var entry = archive.CreateEntry(stored.Name, CompressionLevel.Optimal);
                entry.ExternalAttributes = stored.ExternalAttributes;
                try
                {
                    entry.LastWriteTime = stored.LastWriteTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 非法/零 DOS timestamp 不影響 entry bytes 契約；採 ZipArchive 預設值。
                }

                using var stream = entry.Open();
                stream.Write(stored.Bytes);
            }
        }

        return new MigratedPackage(output.ToArray(), canonical);
    }

    public static string RewriteDefinitionName(string definition, string targetName)
    {
        var scalar = LocateUniqueRootScalar(definition, "name");
        return definition[..scalar.Start] + targetName + definition[scalar.End..];
    }

    public static string RewriteAgenticCanonical(string definition, string targetName)
    {
        var root = DeserializeMap(definition);
        var standard = ToStandardAgentic(root, targetName, fallback: null);
        return SerializeMap(standard);
    }

    private static (string SkillMd, string Canonical) RewriteAgenticSkillMd(
        string source, string targetName, string? canonicalDefinition)
    {
        var match = FrontmatterRegex().Match(source);
        if (!match.Success)
        {
            throw new InvalidDataException("既有 agentic SKILL.md 缺少 YAML frontmatter");
        }

        var root = DeserializeMap(match.Groups["frontmatter"].Value);
        var fallback = canonicalDefinition is null
            ? null
            : DeserializeMap(canonicalDefinition);
        var standard = ToStandardAgentic(root, targetName, fallback);
        var yaml = SerializeMap(standard);
        var body = match.Groups["body"].Value;
        return ($"---\n{yaml}---\n{body}", yaml);
    }

    private static string RewriteFlowSkillMd(string source, string targetName)
    {
        var match = FrontmatterRegex().Match(source);
        if (!match.Success)
        {
            throw new InvalidDataException("既有 flow SKILL.md 缺少 YAML frontmatter");
        }

        var frontmatter = match.Groups["frontmatter"];
        var body = match.Groups["body"];
        var rewrittenFrontmatter = RewriteDefinitionName(frontmatter.Value, targetName);
        var rewrittenBody = RewriteFirstYamlBlock(body.Value, targetName);
        return source[..frontmatter.Index]
            + rewrittenFrontmatter
            + source[(frontmatter.Index + frontmatter.Length)..body.Index]
            + rewrittenBody
            + source[(body.Index + body.Length)..];
    }

    private static string RewriteFirstYamlBlock(string body, string targetName)
    {
        var match = YamlFenceRegex().Match(body);
        if (!match.Success)
        {
            return body;
        }

        var definition = RewriteDefinitionName(match.Groups["definition"].Value, targetName);
        return body[..match.Groups["definition"].Index]
            + definition
            + body[(match.Groups["definition"].Index + match.Groups["definition"].Length)..];
    }

    private static Dictionary<string, object?> ToStandardAgentic(
        Dictionary<string, object?> legacy,
        string targetName,
        IReadOnlyDictionary<string, object?>? fallback)
    {
        var metadata = AsStringObjectMap(Get(legacy, "metadata"));
        var fallbackMetadata = AsStringObjectMap(
            fallback is null ? null : Get(fallback, "metadata"));
        var kind = Scalar(metadata.GetValueOrDefault("kind"))
            ?? Scalar(Get(legacy, "kind"))
            ?? Scalar(fallbackMetadata.GetValueOrDefault("kind"))
            ?? (fallback is null ? null : Scalar(Get(fallback, "kind")));
        if (!string.Equals(kind, "agentic", StringComparison.Ordinal))
        {
            throw new InvalidDataException("既有 package 並非 agentic frontmatter");
        }

        var description = Get(legacy, "description")
            ?? (fallback is null ? null : Get(fallback, "description"))
            ?? throw new InvalidDataException("既有 agentic frontmatter 缺少 description");
        if (description is not string descriptionText || string.IsNullOrWhiteSpace(descriptionText))
        {
            throw new InvalidDataException("既有 agentic frontmatter 的 description 無法推導為非空字串");
        }

        var standard = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = targetName,
            ["description"] = descriptionText,
        };
        CopyOptionalScalar(legacy, standard, "license");
        CopyOptionalScalar(legacy, standard, "compatibility");

        var allowedTools = OptionalString(legacy, "allowed-tools");
        allowedTools ??= fallback is null ? null : OptionalString(fallback, "allowed-tools");
        if (allowedTools is null && Get(legacy, "uses_tools") is { } oldTools)
        {
            allowedTools = oldTools is string text
                ? text
                : string.Join(" ", AsSequence(oldTools).Select(Scalar).Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        else if (allowedTools is null && fallback is not null
            && Get(fallback, "uses_tools") is { } fallbackTools)
        {
            allowedTools = fallbackTools is string text
                ? text
                : string.Join(
                    " ", AsSequence(fallbackTools).Select(Scalar)
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        if (!string.IsNullOrWhiteSpace(allowedTools))
        {
            standard["allowed-tools"] = allowedTools;
        }

        var newMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            newMetadata[key] = Scalar(value)
                ?? throw new InvalidDataException(
                    $"既有 agentic metadata.{key} 無法轉成標準字串");
        }

        newMetadata["kind"] = "agentic";
        newMetadata["required_role"] =
            Scalar(Get(legacy, "required_role"))
            ?? newMetadata.GetValueOrDefault("required_role")
            ?? Scalar(Get(fallbackMetadata, "required_role"))
            ?? (fallback is null ? null : Scalar(Get(fallback, "required_role")))
            ?? "USER";

        var timeout = Scalar(Get(legacy, "timeout_seconds"))
            ?? newMetadata.GetValueOrDefault("timeout_seconds")
            ?? Scalar(Get(fallbackMetadata, "timeout_seconds"))
            ?? (fallback is null ? null : Scalar(Get(fallback, "timeout_seconds")))
            // 舊 runner 對 null timeout 使用 DEFAULT_AGENT_TIMEOUT_S = 60。
            ?? "60";
        newMetadata["timeout_seconds"] = timeout;

        if (Get(legacy, "input_schema") is { } oldSchema)
        {
            newMetadata["input_schema"] = JsonSerializer.Serialize(ToJsonValue(oldSchema));
        }
        else if (newMetadata.TryGetValue("input_schema", out var schema))
        {
            // 已符合標準的 JSON 字串原樣保留。
            newMetadata["input_schema"] = schema;
        }
        else if (Get(fallbackMetadata, "input_schema") is { } fallbackSchema)
        {
            newMetadata["input_schema"] = Scalar(fallbackSchema)
                ?? JsonSerializer.Serialize(ToJsonValue(fallbackSchema));
        }
        else if (fallback is not null && Get(fallback, "input_schema") is { } oldFallbackSchema)
        {
            newMetadata["input_schema"] =
                JsonSerializer.Serialize(ToJsonValue(oldFallbackSchema));
        }
        else
        {
            // 舊 Skill model 的 input_schema default_factory=dict。
            newMetadata["input_schema"] = "{}";
        }

        standard["metadata"] = newMetadata;
        ValidateOptionalStandardFields(standard);
        ValidateAgenticValues(descriptionText, newMetadata);
        return standard;
    }

    private static bool IsStandardAgentic(
        IReadOnlyDictionary<string, object?> root, string targetName)
    {
        var allowedTopLevel = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "description", "license", "compatibility", "metadata", "allowed-tools",
        };
        if (!string.Equals(Scalar(Get(root, "name")), targetName, StringComparison.Ordinal)
            || root.Keys.Any(key => !allowedTopLevel.Contains(key))
            || Get(root, "kind") is not null
            || Get(root, "required_role") is not null
            || Get(root, "timeout_seconds") is not null
            || Get(root, "input_schema") is not null
            || Get(root, "uses_tools") is not null)
        {
            return false;
        }

        var metadata = AsStringObjectMap(Get(root, "metadata"));
        if (!string.Equals(Scalar(metadata.GetValueOrDefault("kind")), "agentic", StringComparison.Ordinal))
        {
            return false;
        }

        var required = new[] { "kind", "required_role", "timeout_seconds", "input_schema" };
        if (!required.All(metadata.ContainsKey)
            || !metadata.Values.All(value => value is string)
            || Get(root, "description") is not string description
            || string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        try
        {
            ValidateOptionalStandardFields(root);
            ValidateAgenticValues(
                (string)Get(root, "description")!,
                metadata.ToDictionary(
                    item => item.Key,
                    item => (string)item.Value!,
                    StringComparer.Ordinal));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateAgenticValues(
        string description, IReadOnlyDictionary<string, string> metadata)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 1024)
        {
            throw new InvalidDataException("agentic description 必須為 1–1024 字的非空字串");
        }

        if (metadata.GetValueOrDefault("kind") != "agentic")
        {
            throw new InvalidDataException("agentic metadata.kind 必須為 agentic");
        }

        if (metadata.GetValueOrDefault("required_role") is not ("USER" or "ADMIN"))
        {
            throw new InvalidDataException("agentic metadata.required_role 必須為 USER 或 ADMIN");
        }

        if (!long.TryParse(
                metadata.GetValueOrDefault("timeout_seconds"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var timeout)
            || timeout < 1)
        {
            throw new InvalidDataException("agentic metadata.timeout_seconds 必須為正整數字串");
        }

        try
        {
            using var schema = JsonDocument.Parse(
                metadata.GetValueOrDefault("input_schema") ?? string.Empty);
            if (schema.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "agentic metadata.input_schema JSON 必須是 object");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "agentic metadata.input_schema 必須是合法 JSON object", ex);
        }
    }

    private static Dictionary<string, object?> DeserializeMap(string yaml)
    {
        var value = Deserializer.Deserialize<object?>(yaml);
        return AsStringObjectMap(value);
    }

    private static Dictionary<string, object?> AsStringObjectMap(object? value)
    {
        if (value is not IDictionary dictionary)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry item in dictionary)
        {
            if (item.Key is not null)
            {
                result[Convert.ToString(item.Key, System.Globalization.CultureInfo.InvariantCulture)!] = item.Value;
            }
        }

        return result;
    }

    private static IEnumerable<object?> AsSequence(object value)
        => value is IEnumerable sequence and not string
            ? sequence.Cast<object?>()
            : new[] { value };

    private static object? ToJsonValue(object? value)
    {
        if (value is IDictionary map)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry item in map)
            {
                result[Convert.ToString(item.Key, System.Globalization.CultureInfo.InvariantCulture)!]
                    = ToJsonValue(item.Value);
            }

            return result;
        }

        if (value is IEnumerable sequence and not string)
        {
            return sequence.Cast<object?>().Select(ToJsonValue).ToList();
        }

        return value;
    }

    private static object? Get(IReadOnlyDictionary<string, object?> map, string key)
        => map.GetValueOrDefault(key);

    private static string? Scalar(object? value)
        => value switch
        {
            null => null,
            string text => text,
            bool boolean => boolean ? "true" : "false",
            IFormattable scalar => scalar.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

    private static void CopyOptionalScalar(
        IReadOnlyDictionary<string, object?> source, IDictionary<string, object?> target, string key)
    {
        if (OptionalString(source, key) is { } value)
        {
            target[key] = value;
        }
    }

    private static string? OptionalString(
        IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value as string
            ?? throw new InvalidDataException($"agentic {key} 必須是字串");
    }

    private static void ValidateOptionalStandardFields(
        IReadOnlyDictionary<string, object?> root)
    {
        var license = OptionalString(root, "license");
        if (license is not null && string.IsNullOrWhiteSpace(license))
        {
            throw new InvalidDataException("agentic license 必須是非空字串");
        }

        var compatibility = OptionalString(root, "compatibility");
        if (compatibility is { Length: > 500 })
        {
            throw new InvalidDataException("agentic compatibility 不得超過 500 字");
        }

        _ = OptionalString(root, "allowed-tools");
    }

    private static string SerializeMap(object map)
    {
        var yaml = Serializer.Serialize(map).Replace("\r\n", "\n", StringComparison.Ordinal);
        return yaml.EndsWith('\n') ? yaml : yaml + "\n";
    }

    private sealed class StoredEntry
    {
        public required string Name { get; init; }
        public required DateTimeOffset LastWriteTime { get; init; }
        public required int ExternalAttributes { get; init; }
        public required byte[] Bytes { get; set; }
    }

    private static StoredEntry ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new StoredEntry
        {
            Name = entry.FullName,
            LastWriteTime = entry.LastWriteTime,
            ExternalAttributes = entry.ExternalAttributes,
            Bytes = buffer.ToArray(),
        };
    }

    private sealed record ScalarLocation(int Start, int End, string Value);

    /// <summary>
    /// 以 YamlDotNet event mark 定位 root mapping 的唯一 scalar 欄位。只回傳 value scalar span，
    /// 因此替換不會動到 key 間距、註解、其他欄位、inline/quoted mapping 格式或換行。
    /// </summary>
    private static ScalarLocation LocateUniqueRootScalar(string yaml, string key)
    {
        var parser = new Parser(new StringReader(yaml));
        var events = new List<ParsingEvent>();
        while (parser.MoveNext())
        {
            events.Add(parser.Current
                ?? throw new InvalidDataException("YAML parser 回傳空 event"));
        }

        var index = events.FindIndex(e => e is DocumentStart);
        if (index < 0 || ++index >= events.Count || events[index] is not MappingStart)
        {
            throw new InvalidDataException("Skill definition root 必須是 YAML mapping");
        }

        index++;
        var matches = new List<ScalarLocation>();
        while (index < events.Count && events[index] is not MappingEnd)
        {
            var keyNodeIndex = index;
            var keyScalar = events[keyNodeIndex] as YamlDotNet.Core.Events.Scalar;
            SkipNode(events, ref index);
            if (index >= events.Count)
            {
                throw new InvalidDataException("Skill definition mapping 缺少 value");
            }

            var valueNodeIndex = index;
            SkipNode(events, ref index);
            if (keyScalar?.Value == key)
            {
                if (events[valueNodeIndex] is not YamlDotNet.Core.Events.Scalar valueScalar)
                {
                    throw new InvalidDataException($"Skill definition 的 {key} 必須是 scalar");
                }

                matches.Add(new ScalarLocation(
                    checked((int)valueScalar.Start.Index),
                    checked((int)valueScalar.End.Index),
                    valueScalar.Value));
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidDataException(
                $"Skill definition root 必須恰有一個 {key} scalar（實際 {matches.Count} 個）");
        }

        return matches[0];
    }

    private static void SkipNode(IReadOnlyList<ParsingEvent> events, ref int index)
    {
        if (index >= events.Count)
        {
            throw new InvalidDataException("YAML node 不完整");
        }

        switch (events[index])
        {
            case YamlDotNet.Core.Events.Scalar:
            case AnchorAlias:
                index++;
                return;
            case SequenceStart:
                index++;
                while (index < events.Count && events[index] is not SequenceEnd)
                {
                    SkipNode(events, ref index);
                }

                if (index >= events.Count)
                {
                    throw new InvalidDataException("YAML sequence 未結束");
                }

                index++;
                return;
            case MappingStart:
                index++;
                while (index < events.Count && events[index] is not MappingEnd)
                {
                    SkipNode(events, ref index);
                    SkipNode(events, ref index);
                }

                if (index >= events.Count)
                {
                    throw new InvalidDataException("YAML mapping 未結束");
                }

                index++;
                return;
            default:
                throw new InvalidDataException(
                    $"不支援的 YAML node：{events[index].GetType().Name}");
        }
    }

    [GeneratedRegex(@"\A(?:\uFEFF)?---\r?\n(?<frontmatter>[\s\S]*?)\r?\n---\r?\n?(?<body>[\s\S]*)\z")]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"```yaml\r?\n(?<definition>[\s\S]*?)\r?\n```")]
    private static partial Regex YamlFenceRegex();
}
