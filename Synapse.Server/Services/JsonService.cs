using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapse.Server.Models;

namespace Synapse.Server.Services;

public interface IJsonService
{
    public Task<TResult?> LoadJson<TValue, TResult>(string path, Func<TValue, TResult> transform, bool verbatim);

    public Task SaveJson<TSource>(TSource list, string path);
}

public class JsonService(ILogger<JsonService> log) : IJsonService
{
    public static JsonSerializerOptions PrettySettings { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static JsonSerializerOptions Settings { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new StageStatusConverter()
        }
    };

    public async Task<TResult?> LoadJson<TValue, TResult>(string path, Func<TValue, TResult> transform, bool verbatim)
    {
        if (!File.Exists(path))
        {
            if (verbatim)
            {
                log.LogWarning("Could not find [{Path}]", path);
            }

            return default;
        }

        using StreamReader reader = new(path);
        TValue? deserialized = await JsonSerializer.DeserializeAsync<TValue>(reader.BaseStream, Settings);
        if (deserialized != null)
        {
            return transform(deserialized);
        }

        log.LogError("Could not load [{Path}]", path);

        return default;
    }

    public async Task SaveJson<TSource>(TSource list, string path)
    {
        try
        {
            await using StreamWriter output = new(path);
            await JsonSerializer.SerializeAsync(output.BaseStream, list, PrettySettings);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not save [{Path}]", path);
        }
    }
}
