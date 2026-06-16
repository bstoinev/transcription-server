using AIO.Transcription.Server.Contracts.Protocol;
using Whisper.net.Ggml;

namespace AIO.Transcription.Server.Transcription;

public static class WhisperModelCatalog
{
    private static readonly WhisperModelDefinition[] Definitions =
    [
        new("tiny.en", "Tiny English", GgmlType.TinyEn, true),
        new("base.en", "Base English", GgmlType.BaseEn, true),
        new("small.en", "Small English", GgmlType.SmallEn, true),
        new("medium.en", "Medium English", GgmlType.MediumEn, true),
        new("tiny", "Tiny Multilingual", GgmlType.Tiny, false),
        new("base", "Base Multilingual", GgmlType.Base, false),
        new("small", "Small Multilingual", GgmlType.Small, false),
        new("medium", "Medium Multilingual", GgmlType.Medium, false),
        new("large-v1", "Large V1 Multilingual", GgmlType.LargeV1, false),
        new("large-v2", "Large V2 Multilingual", GgmlType.LargeV2, false),
        new("large-v3", "Large V3 Multilingual", GgmlType.LargeV3, false),
        new("large-v3-turbo", "Large V3 Turbo Multilingual", GgmlType.LargeV3Turbo, false),
    ];

    public static TranscriptionCapabilities BuildCapabilities(WhisperTranscriberOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var defaultModelType = GetEffectiveConfiguredModelType(options);
        var supportsSessionModelSelection = SupportsSessionModelSelection(options);
        var availableDefinitions = supportsSessionModelSelection
            ? Definitions
            : [Resolve(defaultModelType)];

        return new TranscriptionCapabilities
        {
            DefaultModelType = defaultModelType,
            SupportsBinaryAudio = true,
            SupportsSessionModelSelection = supportsSessionModelSelection,
            MaxConcurrentSessions = 1,
            AvailableModels =
            [
                .. availableDefinitions.Select(definition => new TranscriptionModelInfo
                {
                    Id = definition.Id,
                    DisplayName = definition.DisplayName,
                    EnglishOnly = definition.EnglishOnly,
                    IsDownloaded = File.Exists(ResolveModelPath(options, definition.Id))
                })
            ]
        };
    }

    public static WhisperTranscriberOptions CreateSessionOptions(
        WhisperTranscriberOptions baseOptions,
        string? requestedModelType,
        string? requestedLanguage,
        bool? requestedEnableLanguageDetection)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);

        var sessionOptions = baseOptions.Clone();
        var effectiveConfiguredModelType = GetEffectiveConfiguredModelType(baseOptions);
        var normalizedRequestedModelType = string.IsNullOrWhiteSpace(requestedModelType)
            ? null
            : NormalizeModelType(requestedModelType);

        if (SupportsSessionModelSelection(baseOptions))
        {
            sessionOptions.ModelType = normalizedRequestedModelType ?? effectiveConfiguredModelType;
        }
        else
        {
            if (normalizedRequestedModelType is not null &&
                !string.Equals(normalizedRequestedModelType, effectiveConfiguredModelType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Model selection is disabled because the server is pinned to '{effectiveConfiguredModelType}'. Requested model '{requestedModelType}' is not available.");
            }

            sessionOptions.ModelType = effectiveConfiguredModelType;
        }

        if (!string.IsNullOrWhiteSpace(requestedLanguage))
        {
            sessionOptions.Language = requestedLanguage.Trim();
        }

        if (requestedEnableLanguageDetection.HasValue)
        {
            sessionOptions.EnableLanguageDetection = requestedEnableLanguageDetection.Value;
        }

        return sessionOptions;
    }

    public static WhisperModelDefinition Resolve(string? modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType))
        {
            return Definitions.First(x => string.Equals(x.Id, "base.en", StringComparison.OrdinalIgnoreCase));
        }

        var normalized = modelType.Trim();
        if (Enum.TryParse<GgmlType>(normalized, true, out var enumParsed))
        {
            var definitionByEnum = Definitions.FirstOrDefault(x => x.GgmlType == enumParsed);
            if (definitionByEnum is not null)
            {
                return definitionByEnum;
            }
        }

        normalized = normalized.ToLowerInvariant().Replace('_', '-');
        var definition = Definitions.FirstOrDefault(x => string.Equals(x.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (definition is not null)
        {
            return definition;
        }

        throw new InvalidOperationException(
            $"Unsupported Transcription:ModelType '{modelType}'. Use the whisper.cpp stem that comes after 'ggml-' in the filename, such as 'base.en', 'medium.en', 'base', 'medium', 'large-v3', or 'large-v3-turbo'.");
    }

    public static string NormalizeModelType(string? modelType)
    {
        return Resolve(modelType).Id;
    }

    public static string GetEffectiveConfiguredModelType(WhisperTranscriberOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pinnedModelType = TryResolvePinnedModelType(options.ModelPath);
        return pinnedModelType ?? NormalizeModelType(options.ModelType);
    }

    public static string ResolveModelPath(WhisperTranscriberOptions options, string? modelType = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuredPath = options.ModelPath?.Trim();
        if (IsPinnedModelPath(configuredPath))
        {
            return configuredPath!;
        }

        var definition = Resolve(modelType ?? options.ModelType);
        var modelDirectory = ResolveModelDirectory(options);
        return Path.Combine(modelDirectory, $"ggml-{definition.Id}.bin");
    }

    public static string ResolveModelDirectory(WhisperTranscriberOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            return Path.Combine(AppContext.BaseDirectory, "models");
        }

        var configuredPath = options.ModelPath.Trim();
        if (Directory.Exists(configuredPath))
        {
            return configuredPath;
        }

        if (IsPinnedModelPath(configuredPath))
        {
            return Path.GetDirectoryName(configuredPath)
                ?? throw new InvalidOperationException($"Whisper model path '{configuredPath}' does not have a valid parent directory.");
        }

        return configuredPath;
    }

    public static bool SupportsSessionModelSelection(WhisperTranscriberOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !IsPinnedModelPath(options.ModelPath?.Trim());
    }

    private static bool IsPinnedModelPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        if (Directory.Exists(configuredPath))
        {
            return false;
        }

        if (File.Exists(configuredPath))
        {
            return true;
        }

        return configuredPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolvePinnedModelType(string? configuredPath)
    {
        if (!IsPinnedModelPath(configuredPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(configuredPath);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var modelStem = fileName["ggml-".Length..^".bin".Length];
        return NormalizeModelType(modelStem);
    }
}

public sealed record WhisperModelDefinition(
    string Id,
    string DisplayName,
    GgmlType GgmlType,
    bool EnglishOnly);
