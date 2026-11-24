using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MmiConverter.Services;

internal sealed record VideoSource(string Entry, int Frames);
internal sealed record DiscProfile(long OriginalSize, long LastWriteTicks, VideoSource[] Videos, SortedDictionary<int, long> Estimates)
{
    internal long Estimate(int quality)
    {
        if (Estimates.TryGetValue(quality, out long exact)) return exact;
        var lower = Estimates.Last(point => point.Key < quality);
        var upper = Estimates.First(point => point.Key > quality);
        return lower.Value + (upper.Value - lower.Value) * (quality - lower.Key) / (upper.Key - lower.Key);
    }
}

internal sealed class DiscConverter(NativeTools native, string? profileDirectory = null)
{
    internal const int MinimumQuality = 80;
    internal const int MaximumQuality = 100;
    internal const int RecommendedQuality = 94;
    private const string Encoder = "encoder.exe";
    private const int HeaderSize = 64, IndexSize = 32, RecordSize = 20;
    private const int MaxJpegSize = 4 * 1024 * 1024;
    private static readonly int[] EstimatePoints = [80, 85, 89, 92, 94, 96, 97, 98, 99, 100];
    private readonly string cacheDirectory = profileDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MmiConverter", "Estimates-v2");

    internal static string? CheckInput(string path, out long size)
    {
        size = 0;
        bool hasMmiManifest = false;
        try
        {
            using var source = File.OpenRead(path);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            string system = ReadMmiSystem(archive);
            hasMmiManifest = true;
            var videos = FindVideoEntries(archive, system);
            Span<byte> signature = stackalloc byte[4];
            foreach (var video in videos)
            {
                using var stream = video.Open();
                if (video.Length < 24 || stream.ReadAtLeast(signature, signature.Length, false) != signature.Length
                    || !signature.SequenceEqual("qon1"u8))
                    return "The MMI has a damaged QON video stream";
            }
            size = source.Length;
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return hasMmiManifest ? ex.Message : "This file is not an MMI file";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "The file could not be read";
        }
    }

    internal async Task<DiscProfile> AnalyzeAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        long length = info.Length, lastWrite = info.LastWriteTimeUtc.Ticks;
        string cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        string cacheFile = Path.Combine(cacheDirectory, cacheKey + ".json");
        try
        {
            if (File.Exists(cacheFile))
            {
                var existing = JsonSerializer.Deserialize<DiscProfile>(await File.ReadAllTextAsync(cacheFile, token));
                if (existing is not null && existing.OriginalSize == length && existing.LastWriteTicks == lastWrite &&
                    existing.Videos.Length > 0 && existing.Estimates.Keys.SequenceEqual(EstimatePoints)) return existing;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        using var archive = ZipFile.OpenRead(path);
        var entries = FindVideoEntries(archive);
        long otherBytes = archive.Entries.Except(entries).Sum(entry => entry.Length);
        long archiveOverhead = 22 + (archive.Entries.Count * 128L);
        var sizes = new SortedDictionary<int, long>();
        foreach (int point in EstimatePoints) sizes[point] = otherBytes + archiveOverhead;
        var videos = new List<VideoSource>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            int frames = await ProbeAsync(path, entry.FullName, token);
            videos.Add(new VideoSource(entry.FullName, frames));
            int windows = Math.Min(18, frames);
            int workers = Math.Min(windows, Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
            var outputs = await Task.WhenAll(Enumerable.Range(0, workers).Select(worker =>
                native.RunAsync(Encoder, ["sample", path, entry.FullName,
                    Number(worker * windows / workers), Number((worker + 1) * windows / workers)], token)));
            var points = outputs.SelectMany(output => output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(fields => int.Parse(fields[0], CultureInfo.InvariantCulture))
                .ToDictionary(group => group.Key, group => group.Sum(fields => long.Parse(fields[1], CultureInfo.InvariantCulture)));
            foreach (int point in EstimatePoints)
                sizes[point] = checked(sizes[point] + points[point] + HeaderSize + (long)frames * IndexSize);
        }
        var result = new DiscProfile(length, lastWrite, videos.ToArray(), sizes);
        VerifySource(path, result);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            string pending = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(result), token);
                File.Move(pending, cacheFile, overwrite: true);
            }
            finally { File.Delete(pending); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    internal async Task<string> ConvertAsync(string path, DiscProfile? profile, int quality,
        Action<string, double> progress, CancellationToken token)
    {
        if (quality is < MinimumQuality or > MaximumQuality) throw new ArgumentOutOfRangeException(nameof(quality));
        string output = Path.ChangeExtension(path, ".mjmmi");
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException($"The destination already exists: {output}");
        using var original = ZipFile.OpenRead(path);
        var entries = FindVideoEntries(original);
        if (profile is not null) VerifySource(path, profile);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        string folder = Path.GetDirectoryName(path)!;
        string work = Path.Combine(folder, ".mmi-converter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string pending = Path.Combine(work, "disc.tmp");
        try
        {
            using (var stream = File.Open(pending, FileMode.CreateNew))
            using (var packed = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                progress("Copying original audio and game data", 0);
                foreach (var item in original.Entries.Except(entries))
                {
                    token.ThrowIfCancellationRequested();
                    ZipArchiveEntry copy = packed.CreateEntry(item.FullName, CompressionLevel.NoCompression);
                    copy.LastWriteTime = item.LastWriteTime;
                    using Stream from = item.Open();
                    using Stream to = copy.Open();
                    await from.CopyToAsync(to, 1024 * 1024, token);
                }
                for (int side = 0; side < entries.Length; side++)
                {
                    var entry = entries[side];
                    var video = new VideoSource(entry.FullName, await ProbeAsync(path, entry.FullName, token));
                    string outputName = Path.ChangeExtension(video.Entry, ".jvcv");
                    ZipArchiveEntry packedVideo = packed.CreateEntry(outputName, CompressionLevel.NoCompression);
                    int currentSide = side;
                    sizes[outputName] = await EncodeSideAsync(path, video, work, packedVideo, quality,
                        (label, percent) => progress(label, (currentSide * 100 + percent) / entries.Length), token);
                }
            }
            progress("Checking the disc", 99);
            using (var completed = ZipFile.OpenRead(pending))
            {
                foreach (var item in original.Entries.Except(entries))
                {
                    token.ThrowIfCancellationRequested();
                    var copy = completed.GetEntry(item.FullName);
                    if (copy is null || copy.Length != item.Length || copy.Crc32 != item.Crc32)
                        throw new InvalidDataException($"Archive check failed for {item.FullName}");
                }
                foreach (var (name, size) in sizes)
                {
                    var video = completed.GetEntry(name);
                    if (video is null || video.Length != size || video.CompressedLength != size)
                        throw new InvalidDataException($"The resulting video entry is invalid: {name}");
                }
            }
            if (profile is not null) VerifySource(path, profile);
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(pending, output);
            }
            catch (IOException ex) when (File.Exists(output) || Directory.Exists(output))
            {
                throw new IOException($"The destination already exists: {output}", ex);
            }
            return output;
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private async Task<long> EncodeSideAsync(string path, VideoSource video, string directory, ZipArchiveEntry target,
        int quality, Action<string, double> progress, CancellationToken token)
    {
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 12);
        int segment = Math.Clamp((video.Frames - 1) / threads + 1, 480, 3600);
        int partCount = (video.Frames - 1) / segment + 1;
        string[] parts = Enumerable.Range(0, partCount).Select(i => Path.Combine(directory, $"part-{i}.bin")).ToArray();
        long encoded = 0;
        progress("Encoding video", 1);
        await Parallel.ForEachAsync(Enumerable.Range(0, partCount),
            new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token },
            async (part, stop) =>
            {
                int first = part * segment, count = Math.Min(segment, video.Frames - first), reported = 0;
                await native.RunAsync(Encoder, ["encode", path, video.Entry, Number(first), Number(count), Number(quality), parts[part]], stop,
                    line =>
                    {
                        if (!int.TryParse(line, out int done)) return;
                        long total = Interlocked.Add(ref encoded, done - reported);
                        reported = done;
                        progress("Encoding video", 1 + 84.0 * total / video.Frames);
                    });
                if (reported != count) throw new InvalidDataException("The encoder did not write the expected number of frames");
            });

        progress("Packaging video", 85);
        int indexBytes = checked(video.Frames * IndexSize);
        long offset = HeaderSize + indexBytes;
        using var header = new MemoryStream(checked(HeaderSize + indexBytes));
        using var writer = new BinaryWriter(header);
        writer.Write("JVC1"u8);
        writer.Write((ushort)1);
        writer.Write((ushort)HeaderSize);
        writer.Write(910);
        writer.Write(525);
        writer.Write(video.Frames);
        writer.Write(1);
        writer.Write((long)HeaderSize);
        writer.Write(offset);
        writer.Write(new byte[24]);

        using var samples = new MemoryStream();
        byte[] record = new byte[RecordSize];
        int number = 0;
        foreach (string part in parts)
        {
            token.ThrowIfCancellationRequested();
            using var data = File.OpenRead(part);
            bool sample = true;
            while (data.Position < data.Length)
            {
                if ((number & 63) == 0) token.ThrowIfCancellationRequested();
                data.ReadExactly(record);
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(record);
                if (size is < 4 or > MaxJpegSize || size > data.Length - data.Position || number >= video.Frames)
                    throw new InvalidDataException($"Invalid JPEG packet at frame {number}");
                long start = data.Position;
                if (data.ReadByte() != 255 || data.ReadByte() != 216)
                    throw new InvalidDataException($"Invalid JPEG start at frame {number}");
                data.Position = start + size - 2;
                if (data.ReadByte() != 255 || data.ReadByte() != 217)
                    throw new InvalidDataException($"Invalid JPEG end at frame {number}");
                if (sample)
                {
                    data.Position = start;
                    byte[] bytes = new byte[size];
                    data.ReadExactly(bytes);
                    samples.Write(bytes);
                    sample = false;
                }
                writer.Write(offset);
                writer.Write(size);
                writer.Write(number++);
                writer.Write(record, 4, 16);
                offset = checked(offset + size);
            }
        }
        if (number != video.Frames) throw new InvalidDataException("The encoded frame count has changed");
        samples.Position = 0;
        await native.RunAsync("ffmpeg.exe", ["-hide_banner", "-nostdin", "-v", "error", "-xerror",
            "-err_detect", "explode", "-f", "mjpeg", "-i", "pipe:0", "-f", "null", "-"], token, input: samples);

        using Stream output = target.Open();
        header.Position = 0;
        await header.CopyToAsync(output, token);
        byte[] buffer = new byte[MaxJpegSize];
        int written = 0;
        foreach (string part in parts)
        {
            using (var data = File.OpenRead(part))
            {
                while (data.Position < data.Length)
                {
                    if ((written++ & 63) == 0) token.ThrowIfCancellationRequested();
                    data.ReadExactly(record);
                    int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(record);
                    data.ReadExactly(buffer.AsSpan(0, size));
                    output.Write(buffer, 0, size);
                }
            }
            File.Delete(part);
            progress("Packaging video", 85 + 14.0 * written / video.Frames);
        }
        return offset;
    }

    private async Task<int> ProbeAsync(string path, string entry, CancellationToken token)
    {
        string output = await native.RunAsync(Encoder, ["probe", path, entry], token);
        if (int.TryParse(output, CultureInfo.InvariantCulture, out int frames) && frames > 0) return frames;
        throw new InvalidDataException("Could not read the QON frame count");
    }

    private static string ReadMmiSystem(ZipArchive archive)
    {
        var manifest = archive.GetEntry("MediaInfo.json") ?? throw new InvalidDataException("This file has no MMI manifest");
        using var metadata = manifest.Open();
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("system", out var system) || system.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(system.GetString()) ||
            !root.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array ||
            !media.EnumerateArray().Any(disc => disc.ValueKind == JsonValueKind.Object &&
                disc.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array &&
                streams.EnumerateArray().Any(stream => stream.ValueKind == JsonValueKind.Object &&
                    stream.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String &&
                    file.GetString() is { Length: > 0 } name && archive.GetEntry(name) is { Length: > 0 })))
            throw new InvalidDataException("The MMI manifest does not identify a system and a media stream");
        return system.GetString()!;
    }

    private static ZipArchiveEntry[] FindVideoEntries(ZipArchive archive, string? system = null)
    {
        if (archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new InvalidDataException("This MMI contains duplicate ZIP entry names");
        system ??= ReadMmiSystem(archive);
        if (system is not ("MegaLD" or "LDROM2"))
            throw new InvalidDataException("This version supports Sega MegaLD and NEC LD-ROM² MMI discs");
        var video = archive.Entries.Where(entry => entry.FullName.EndsWith("/AnalogVideo.qon", StringComparison.Ordinal)).ToArray();
        if (video.Length == 0 || video.Any(entry => entry.Length != entry.CompressedLength))
            throw new InvalidDataException("This MMI needs an uncompressed QON stream inside its archive");
        return video;
    }

    private static void VerifySource(string path, DiscProfile profile)
    {
        var file = new FileInfo(path);
        if (file.Length != profile.OriginalSize || file.LastWriteTimeUtc.Ticks != profile.LastWriteTicks)
            throw new IOException("The source file changed. Remove it from the list and open it again.");
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
