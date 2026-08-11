using System.Diagnostics;
using System.Security.Cryptography;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using AetherPC.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace AetherPC.Infrastructure.Benchmarks;

public sealed class LocalBenchmarkService : IBenchmarkService
{
    private readonly SqliteStore _store;
    private readonly ILogger<LocalBenchmarkService> _logger;

    public LocalBenchmarkService(SqliteStore store, ILogger<LocalBenchmarkService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<BenchmarkResult> RunCpuAsync(CancellationToken ct = default)
    {
        var result = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            long ops = 0;
            var until = sw.ElapsedMilliseconds + 2500;
            while (sw.ElapsedMilliseconds < until)
            {
                ct.ThrowIfCancellationRequested();
                var x = 0.0;
                for (var i = 0; i < 25_000; i++)
                    x += Math.Sqrt(i) * Math.Sin(i) * Math.Cos(i * 0.01);
                if (x < -1) ops--;
                ops++;
            }
            sw.Stop();
            var score = ops / Math.Max(1.0, sw.Elapsed.TotalSeconds);
            return new BenchmarkResult
            {
                Kind = "CPU",
                Score = Math.Round(score, 2),
                Unit = "ops/s",
                Details = $"Logical cores: {Environment.ProcessorCount} · load {sw.Elapsed.TotalSeconds:F2}s · math loop"
            };
        }, ct).ConfigureAwait(false);

        await _store.SaveBenchmarkAsync(result, ct).ConfigureAwait(false);
        _logger.LogInformation("Benchmark CPU: {Score}", result.Score);
        return result;
    }

    public async Task<BenchmarkResult> RunMemoryAsync(CancellationToken ct = default)
    {
        var result = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            const int size = 48 * 1024 * 1024;
            var buffer = new byte[size];
            RandomNumberGenerator.Fill(buffer);
            var sw = Stopwatch.StartNew();
            long sum = 0;
            for (var pass = 0; pass < 6; pass++)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < buffer.Length; i += 64)
                    sum += buffer[i];
                for (var i = 0; i < buffer.Length; i += 4096)
                    buffer[i] = (byte)(buffer[i] ^ 0x5A);
            }
            sw.Stop();
            var mbPerSec = (size * 6.0 / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
            return new BenchmarkResult
            {
                Kind = "RAM",
                Score = Math.Round(mbPerSec, 2),
                Unit = "MB/s (rw)",
                Details = sum == long.MinValue ? "" : $"6×48MB sequential R/W · {sw.Elapsed.TotalSeconds:F2}s"
            };
        }, ct).ConfigureAwait(false);

        await _store.SaveBenchmarkAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<BenchmarkResult> RunDiskAsync(string? driveLetter = null, CancellationToken ct = default)
    {
        var root = string.IsNullOrWhiteSpace(driveLetter)
            ? Path.GetPathRoot(Environment.SystemDirectory)!
            : driveLetter.EndsWith("\\") ? driveLetter : driveLetter + "\\";

        const int size = 64 * 1024 * 1024;
        try
        {
            var di = new DriveInfo(root);
            if (di.IsReady && di.AvailableFreeSpace < size * 3L)
                throw new InvalidOperationException($"Insufficient free space on {root}");
        }
        catch (InvalidOperationException) { throw; }
        catch { /* DriveInfo may fail; continue with write attempt */ }

        var path = Path.Combine(root, "AetherPC_Bench.tmp");
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);

        double writeMbps = 0, readMbps = 0;
        try
        {
            var sw = Stopwatch.StartNew();
            await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
            sw.Stop();
            writeMbps = (size / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds;

            sw.Restart();
            _ = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            sw.Stop();
            readMbps = (size / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        var result = new BenchmarkResult
        {
            Kind = "Disk",
            Score = Math.Round((writeMbps + readMbps) / 2.0, 2),
            Unit = "MB/s avg",
            Details = $"Write {writeMbps:F1} / Read {readMbps:F1} @ {root}"
        };
        await _store.SaveBenchmarkAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    public Task<IReadOnlyList<BenchmarkResult>> ListHistoryAsync(CancellationToken ct = default)
        => _store.ListBenchmarksAsync(ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
        => _store.DeleteBenchmarkAsync(id, ct);
}
