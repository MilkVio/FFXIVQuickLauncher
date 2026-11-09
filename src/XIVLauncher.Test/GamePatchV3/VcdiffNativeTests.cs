using System.IO;
using System.Security.Cryptography;
using XIVLauncher.GamePatchV3;
using Xunit;

namespace XIVLauncher.Test.GamePatchV3;

// Requires a normal build of XIVLauncher.VcdiffShim (including the Rust/C DLL).
[Trait("Category", "Native")]
public sealed class VcdiffNativeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "VioletNativeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentWorkersProduceVerifiedOutputAndCanBeReused()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        using var client = new VcdiffClient(WorkerPath());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var payload = new byte[4096 + i];
            Random.Shared.NextBytes(payload);
            var target = Path.Combine(directory, $"output-{i}");
            await client.ApplyVcdiff(source, LiteralDelta(payload), target, Convert.ToHexString(MD5.HashData(payload)), payload.Length,
                cancellationToken: timeout.Token);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".tmp"));
        }));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("delta")]
    [InlineData("source")]
    public async Task InvalidPatchPreservesTargetAndCleansTemporaryFile(string invalid)
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source");
        var target = Path.Combine(directory, "target");
        if (invalid != "source") await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllTextAsync(target, "keep original");
        byte[] payload = [4, 5, 6, 7];
        using var client = new VcdiffClient(WorkerPath());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<IOException>(() => client.ApplyVcdiff(source,
            invalid == "delta" ? new byte[] { 0, 1 } : LiteralDelta(payload), target,
            invalid == "hash" ? new string('0', 32) : Convert.ToHexString(MD5.HashData(payload)),
            invalid == "size" ? 99 : payload.Length, cancellationToken: timeout.Token));
        Assert.Equal("keep original", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task CancelledMergeDoesNotTouchTarget()
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "target");
        await File.WriteAllTextAsync(target, "keep original");
        using var client = new VcdiffClient(WorkerPath());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ApplyVcdiff("unused", LiteralDelta([1]), target, "", 1,
            cancellationToken: cts.Token));
        Assert.Equal("keep original", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    private static string WorkerPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "XIVLauncher.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var worker = Path.Combine(root.FullName, "src", "bin", "VcdiffShim", "XIVLauncher.VcdiffShim.exe");
        Assert.True(File.Exists(worker), "Build XIVLauncher.VcdiffShim with native shims before running Category=Native tests.");
        return worker;
    }

    private static byte[] LiteralDelta(byte[] payload)
    {
        // VCDIFF default code table: instruction 1 is ADD with an explicit variable-length size.
        var instructions = new List<byte> { 1 };
        WriteInteger(instructions, payload.Length);
        var window = new List<byte>();
        WriteInteger(window, payload.Length);
        window.Add(0); // No secondary compression.
        WriteInteger(window, payload.Length);
        WriteInteger(window, instructions.Count);
        WriteInteger(window, 0); // No COPY addresses.
        window.AddRange(payload);
        window.AddRange(instructions);
        var delta = new List<byte> { 0xd6, 0xc3, 0xc4, 0, 0, 0 }; // Header, then a window without source data.
        WriteInteger(delta, window.Count);
        delta.AddRange(window);
        return [.. delta];
    }

    private static void WriteInteger(List<byte> bytes, int value)
    {
        var encoded = new List<byte> { (byte)(value & 127) };
        while ((value >>= 7) > 0) encoded.Insert(0, (byte)((value & 127) | 128));
        bytes.AddRange(encoded);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
