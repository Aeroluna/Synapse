using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Synapse.Extras;

internal static class MediaExtensions
{
    private static readonly ConcurrentDictionary<string, Sprite> _spriteCache = new();

    internal static async Task DownloadAndSave(
        string url,
        string hash,
        string unzipPath,
        Action<float>? progress,
        Action? unzipping,
        Action<float>? unzipProgress,
        CancellationToken token)
    {
        using UnityWebRequest www = UnityWebRequest.Get(url);
        await www.SendAndVerify(progress, token);

        unzipping?.Invoke();

        using MemoryStream stream = new(www.downloadHandler.data);

        using MD5 md5 = MD5.Create();
        string computed = BitConverter
            .ToString(md5.ComputeHash(stream))
            .Replace("-", string.Empty)
            .ToLowerInvariant();
        if (computed != hash && !Plugin.SkipHash)
        {
            throw new InvalidOperationException($"MD5 mismatch, expected: [{hash}], calculated: [{computed}].");
        }

        using ZipArchive zip = new(stream, ZipArchiveMode.Read, false);
        ZipArchiveEntry[] entries = zip.Entries.ToArray();
        for (int j = 0; j < entries.Length; j++)
        {
            unzipProgress?.Invoke((float)j / entries.Length);
            ZipArchiveEntry entry = entries[j];
            string fullPath = Path.GetFullPath(Path.Combine(unzipPath, entry.FullName));
            if (Path.GetFileName(fullPath).Length == 0)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                entry.ExtractToFile(fullPath, true);
            }
        }
    }

    internal static Sprite GetEmbeddedResourceSprite(string path)
    {
        using Stream stream =
            typeof(MediaExtensions).Assembly.GetManifestResourceStream(path) ?? throw new InvalidOperationException();
        using MemoryStream memStream = new();
        stream.CopyTo(memStream);
        Texture2D tex = new(2, 2);
        tex.LoadImage(memStream.ToArray());
        return tex.GetSprite();
    }

    internal static Sprite GetSprite(this Texture2D tex)
    {
        return Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            256f,
            0U,
            SpriteMeshType.FullRect,
            new Vector4(0f, 0f, 0f, 0f),
            false);
    }

    internal static async Task<T> LoadAssetAsyncTask<T>(this AssetBundle assetBundle, string name)
        where T : Object
    {
        TaskCompletionSource<T> taskCompletionSource = new();
        AssetBundleRequest bundleRequest = assetBundle.LoadAssetAsync<T>(name);
        bundleRequest.completed += _ =>
        {
            if (bundleRequest.asset == null)
            {
                throw new InvalidOperationException("Asset was null.");
            }

            if (bundleRequest.asset is not T asset)
            {
                throw new InvalidOperationException($"Asset was not {typeof(T).Name}.");
            }

            taskCompletionSource.SetResult(asset);
        };

        return await taskCompletionSource.Task;
    }

    internal static async Task<AssetBundle?> LoadFromFileAsync(string path, uint crc)
    {
        TaskCompletionSource<AssetBundle?> taskCompletionSource = new();
        AssetBundleCreateRequest bundleRequest = Plugin.SkipHash
            ? AssetBundle.LoadFromFileAsync(path)
            : AssetBundle.LoadFromFileAsync(path, crc);
        bundleRequest.completed += _ => { taskCompletionSource.SetResult(bundleRequest.assetBundle); };

        return await taskCompletionSource.Task;
    }

    internal static bool MatchesGameVersion(this string gameVersion)
    {
        return gameVersion.Split(',').Any(n => n == Plugin.GameVersion);
    }

    internal static void Purge(this DirectoryInfo directory)
    {
        // cleanup
        if (!directory.Exists)
        {
            return;
        }

        try
        {
            foreach (FileInfo file in directory.GetFiles())
            {
                file.Delete();
            }

            foreach (DirectoryInfo dir in directory.GetDirectories())
            {
                dir.Delete(true);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"Exception while purging directory: [{directory}]\n{e}");
        }
    }

    internal static async Task<Sprite> RequestSprite(string url, CancellationToken token)
    {
        // ReSharper disable once InvertIf
        if (!_spriteCache.TryGetValue(url, out Sprite result))
        {
            UnityWebRequest www = UnityWebRequestTexture.GetTexture(url);
            await www.SendAndVerify(token);
            Texture2D tex = DownloadHandlerTexture.GetContent(www);
            _spriteCache[url] = result = tex.GetSprite();
        }

        return result;
    }

    internal static Task SendAndVerify(
        this UnityWebRequest www,
        CancellationToken token)
    {
        return www.SendAndVerify(null, token);
    }

    internal static async Task SendAndVerify(
        this UnityWebRequest www,
        Action<float>? progress,
        CancellationToken token)
    {
        www.SendWebRequest();
        while (!www.isDone)
        {
            if (token.IsCancellationRequested)
            {
                www.Abort();
                token.ThrowIfCancellationRequested();
                return;
            }

            progress?.Invoke(www.downloadProgress);
            await Task.Delay(100, CancellationToken.None);
        }

#pragma warning disable CS0618 // Type or member is obsolete
        if (www.isHttpError)
        {
            throw new InvalidOperationException(
                $"Failed to connect to [{www.url}], server returned an error response ({www.responseCode}).");
        }

        if (www.isNetworkError)
        {
            throw new InvalidOperationException($"Failed to connect to [{www.url}], network error ({www.error}).");
        }
#pragma warning restore CS0618 // Type or member is obsolete
    }
}
