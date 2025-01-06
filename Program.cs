
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json.Serialization;
using System.Buffers.Text;
using System.Buffers;

await ConsoleApp.RunAsync(args, default(StaticInstanceMethod)!.Root);

sealed class StaticInstanceMethod;

static class Command
{
    private const string ManifestFileName = "package.json";

    /// <param name="createUnityPackage">-u</param>
    /// <param name="createZip">-z</param>
    /// <param name="manifestPath">-p</param>
    /// <param name="outputDirectoryName">-o</param>
    /// <returns></returns>
    public static async Task<int> Root(this StaticInstanceMethod _instance, string? manifestPath = null, bool createUnityPackage = false, bool createZip = false, string outputDirectoryName = "output")
    {
        manifestPath ??= ManifestFileName;
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine("Manifest is not found");
            return 1;
        }

        JsonNode? manifest;
        {
            using var fs = File.OpenRead(manifestPath);
            manifest = await JsonNode.ParseAsync(fs, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (manifest is null)
            {
                Console.Error.WriteLine("Can't parse manifest");
                return 1;
            }
        }

        using var output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { } outputPath ? new StreamWriter(outputPath, true, Encoding.UTF8) : null;

        if (!manifest.AsObject().TryGetPropertyValue("name", out var name))
        {
            Console.Error.WriteLine("Can't read name property");
            return 1;
        }

        if (!manifest.AsObject().TryGetPropertyValue("version", out var version))
        {
            Console.Error.WriteLine("Can't read version property");
            return 1;
        }

        output?.WriteLine($"package-name={name}");
        output?.WriteLine($"package-version={version}");

        var manifestPathInfo = new FileInfo(manifestPath);
        var entries = manifestPathInfo.Directory?.EnumerateFileSystemInfos("*", new EnumerationOptions() { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false}).ToArray() ?? [];
        var rootDir = manifestPathInfo.Directory!.FullName;

        Directory.CreateDirectory(outputDirectoryName);

        if (createZip)
        {
            var zipPath = Path.Join(outputDirectoryName, $"{name}.{version}.zip");
            {
                using ZipArchive zip = new(File.Create(zipPath), ZipArchiveMode.Create);
                foreach (var entry in entries)
                {
                    if (entry is DirectoryInfo)
                        continue;

                    bool needSkip = false;
                    foreach(var segmentRange in entry.FullName.AsSpan().SplitAny(@"/\"))
                    {
                        var segment = entry.FullName.AsSpan()[segmentRange];
                        needSkip = segment is ".github" || segment.EndsWith("~");
                        if (needSkip)
                            break;
                    }
                    if (needSkip)
                        continue;

                    var entryName = $"{entry.FullName.AsSpan(rootDir.Length + 1)}";

                    zip.CreateEntryFromFile(entry.FullName, entryName);
                }
            }
            output?.WriteLine($"zip-path={zipPath}");
            var hash = GetHashCode(zipPath);
            manifest.AsObject().Add("zipSHA256", hash);

            static string GetHashCode(string path)
            {
                var hash = (stackalloc byte[32]);
                var result = (stackalloc char[64]);

                using var fs = File.OpenRead(path);
                SHA256.HashData(fs, hash);

                if (Avx2.IsSupported)
                {
                    ToString_Vector256(hash, result);
                }
                else if (Ssse3.IsSupported)
                {
                    ToString_Vector128(hash, result);
                }
                else
                {
                    ToString(hash, result);
                }

                return result.ToString();

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static void ToString_Vector256(ReadOnlySpan<byte> source, Span<char> destination)
                {
                    ref var srcRef = ref MemoryMarshal.GetReference(source);
                    ref var dstRef = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(destination));
                    var hexMap = Vector256.Create("0123456789abcdef0123456789abcdef"u8);

                    for (int i = 0; i < 2; i++)
                    {
                        var src = Vector256.LoadUnsafe(ref srcRef);
                        var shiftedSrc = Vector256.ShiftRightLogical(src.AsUInt64(), 4).AsByte();
                        var lowNibbles = Avx2.UnpackLow(shiftedSrc, src);
                        var highNibbles = Avx2.UnpackHigh(shiftedSrc, src);

                        var l = Avx2.Shuffle(hexMap, lowNibbles & Vector256.Create((byte)0xF));
                        var h = Avx2.Shuffle(hexMap, highNibbles & Vector256.Create((byte)0xF));

                        var lh = l.WithUpper(h.GetLower());

                        var (v0, v1) = Vector256.Widen(lh);

                        v0.StoreUnsafe(ref dstRef);
                        v1.StoreUnsafe(ref Unsafe.AddByteOffset(ref dstRef, 32));

                        srcRef = ref Unsafe.AddByteOffset(ref srcRef, 16);
                        dstRef = ref Unsafe.AddByteOffset(ref dstRef, 64);
                    }
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static void ToString_Vector128(ReadOnlySpan<byte> source, Span<char> destination)
                {
                    ref var srcRef = ref MemoryMarshal.GetReference(source);
                    ref var dstRef = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(destination));
                    var hexMap = Vector128.Create("0123456789abcdef0123456789abcdef"u8);

                    for (int i = 0; i < 8; i++)
                    {
                        var src = Vector128.LoadUnsafe(ref srcRef);
                        var shiftedSrc = Vector128.ShiftRightLogical(src.AsUInt64(), 4).AsByte();
                        var lowNibbles = Sse2.UnpackLow(shiftedSrc, src);

                        var l = Ssse3.Shuffle(hexMap, lowNibbles & Vector128.Create((byte)0xF));

                        var (v0, _) = Vector128.Widen(l);

                        v0.StoreUnsafe(ref dstRef);

                        srcRef = ref Unsafe.AddByteOffset(ref srcRef, 4);
                        dstRef = ref Unsafe.AddByteOffset(ref dstRef, 16);
                    }
                }

                static void ToString(ReadOnlySpan<byte> source, Span<char> destination)
                {
                    for (int i = 0, i2 = 0; i < source.Length && i2 < destination.Length; i++, i2 += 2)
                    {
                        var value = source[i];
                        uint difference = ((value & 0xF0U) << 4) + ((uint)value & 0x0FU) - 0x8989U;
                        uint packedResult = ((((uint)(-(int)difference) & 0x7070U) >> 4) + difference + 0xB9B9U) | (uint)0x2020;

                        destination[i2 + 1] = (char)(packedResult & 0xFF);
                        destination[i2] = (char)(packedResult >> 8);
                    }
                }
            }
        }

        if (createUnityPackage)
        {
            var unitypackagePath = Path.Join(outputDirectoryName, $"{name}.{version}.unitypackage");
            using TarWriter unitypackage = new(new GZipStream(File.Create(unitypackagePath), CompressionMode.Compress));

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in entries)
            {
                var fullName = entry.FullName;
                if (fullName.EndsWith(".meta"))
                    continue;

                bool needSkip = false;
                foreach (var segmentRange in fullName.AsSpan().SplitAny(@"/\"))
                {
                    var segment = fullName.AsSpan()[segmentRange];
                    needSkip = segment is ".github" || segment.EndsWith("~");
                    if (needSkip)
                        break;
                }
                if (needSkip)
                    continue;

                dict.TryAdd(fullName, "");
            }

            var lookup = dict.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach(var entry in entries.Where(x => x.Extension is ".meta"))
            {
                var assetName = entry.FullName.AsSpan()[..^".meta".Length];
                ref var x = ref CollectionsMarshal.GetValueRefOrNullRef(lookup, assetName);
                if (Unsafe.IsNullRef(ref x))
                    continue;

                x = entry.FullName;
            }

            var pathRoot = $"Packages/{name}/";
            var guidBuffer = (stackalloc byte[1024]);
            foreach(var (path, metaPath) in dict)
            {
                if (Path.GetFileName(path.AsSpan()) is ".gitignore")
                    continue;

                Guid guid;
                var relativePath = $"{path.AsSpan(rootDir.Length + 1)}".Replace("\\", "/");
                {
                    Stream stream;
                    if (File.Exists(metaPath) || Directory.Exists(metaPath))
                    {
                        var fs = File.OpenRead(metaPath);
                        _ = fs.Read(guidBuffer);
                        fs.Position = 0;

                        var idx = guidBuffer.IndexOf("guid:"u8);

                        _ = Utf8Parser.TryParse(guidBuffer.Slice(idx + 6), out guid, out _, 'N');

                        stream = fs;
                    }
                    else
                    {
                        SHA256.HashData(MemoryMarshal.AsBytes<char>($"{name}/{relativePath}"), guidBuffer);
                        guid = MemoryMarshal.Read<Guid>(guidBuffer);
                        var ext = Path.GetExtension(path.AsSpan());
                        var ms = EncodeText($$"""
                            fileFormatVersion: 2
                            guid: {{guid:N}}
                            {{(Directory.Exists(path) ? "folderAsset: yes" : "")}}
                            DefaultImporter:
                              externalObjects: {}
                              userData: 
                              assetBundleName: 
                              assetBundleVariant: 
                            """);
                        stream = ms;
                    }
                    var entry = new UstarTarEntry(TarEntryType.RegularFile, $"{guid:N}/asset.meta")
                    {
                        DataStream = stream
                    };
                    unitypackage.WriteEntry(entry);
                    stream.Dispose();
                }

                {
                    var entry = new UstarTarEntry(TarEntryType.RegularFile, $"{guid:N}/pathname");
                    entry.DataStream = EncodeText($"{pathRoot}{relativePath}");
                    unitypackage.WriteEntry(entry);
                }

                if (File.Exists(path))
                {
                    var entry = new UstarTarEntry(TarEntryType.RegularFile, $"{guid:N}/asset");
                    entry.DataStream = File.OpenRead(path);
                    unitypackage.WriteEntry(entry);
                }

                output?.WriteLine($"unitypackage-path={unitypackagePath}");
            }
        }

        {
            var newManifestPath = Path.Join(outputDirectoryName, ManifestFileName);
            using var fs = File.Create(newManifestPath);
            await JsonSerializer.SerializeAsync(fs, manifest, JsonSerializerContexts.Default.JsonNode);
            output?.WriteLine($"manifest-path={newManifestPath}");
        }

        if (output is not null)
            await output.FlushAsync();

        return 0;
    }

    [SkipLocalsInit]
    internal static MemoryStream EncodeText(ReadOnlySpan<char> source)
    {
        var buffer = (stackalloc byte[source.Length * 3]);
        Utf8.FromUtf16(source, buffer, out _, out var len);
        var ms = new MemoryStream(buffer[0..len].ToArray());
        return ms;
    }
}
