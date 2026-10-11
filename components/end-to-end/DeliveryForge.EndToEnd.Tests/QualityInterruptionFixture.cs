using System.Security.Cryptography;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class QualityInterruptionFixture : IAsyncLifetime
{
    private readonly object _sequenceGate = new();
    private readonly QualityInterruptionClock _budget = new();
    private Task<IReadOnlyDictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>>? _sequence;
    private OwnedScenarioRun? _artifacts;
    private string? _source;
    public int SequenceStarts { get; private set; }
    public async ValueTask InitializeAsync()
    {
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, _budget.WallToken);
        var setupRemaining = TimeSpan.FromSeconds(30) - _budget.Elapsed;
        if (setupRemaining <= TimeSpan.Zero) throw new TimeoutException("Interruption setup budget already exhausted.");
        var workRemaining = _budget.WorkBudget(30);
        setup.CancelAfter(setupRemaining < workRemaining ? setupRemaining : workRemaining);
        _artifacts = await OwnedScenarioRun.CreateAsync(setup.Token);
        _artifacts.RetainArtifacts("Actual .NET intended nonpass evidence retained; no complete real profile certification.");
        _source = Path.Combine(_artifacts.Root, "quality-interruption-source");
        Directory.CreateDirectory(_source);
        var template = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealDotNet");
        if (!Directory.Exists(template)) throw new IOException("Owned source-only .NET fixture was not copied into test output.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(template);
        var entryCount = 0; long totalBytes = 0;
        while (pending.TryPop(out var directory))
        {
            setup.Token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source fixture contains a redirected directory.");
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
            if (++entryCount > 128 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source fixture inventory is redirected or exceeds its bound.");
            var relative = Path.GetRelativePath(template, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) throw new IOException("Source fixture contains forbidden build outputs.");
            if (Directory.Exists(file)) { pending.Push(file); continue; }
            if ((totalBytes += new FileInfo(file).Length) > 1024 * 1024) throw new IOException("Source-only fixture exceeds its one-MiB bound.");
            var target = Path.Combine(_source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = await File.ReadAllBytesAsync(file, setup.Token);
            await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(bytes, setup.Token);
            hashes[relative] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
        }
        var barrierTemplate = Path.Combine(AppContext.BaseDirectory, "Fixtures", "QualityInterruption", "CalculatorTests.cs");
        if ((File.GetAttributes(barrierTemplate) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Compiled barrier source is redirected.");
        var barrierBytes = await File.ReadAllBytesAsync(barrierTemplate, setup.Token);
        if (barrierBytes.Length > 65536) throw new IOException("Compiled barrier source exceeds its bound.");
        var barrierTarget = Path.Combine(_source, "Subject.Tests", "CalculatorTests.cs");
        if ((File.GetAttributes(barrierTarget) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Owned source overlay is redirected.");
        await File.WriteAllBytesAsync(barrierTarget, barrierBytes, setup.Token);
        hashes[Path.Combine("Subject.Tests", "CalculatorTests.cs")] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(barrierBytes));
        await _artifacts.WriteNewJsonAsync("quality-interruption-source-spec.json", new
        {
            fixture = true, productionCertified = false, firstFullRealRunCertified = false,
            integratedBaseCommit = _artifacts.IntegratedSource.Commit, implementationHead = _artifacts.IntegratedSource.HeadCommit,
            seed = 11018, budgetId = QualityInterruptionClock.ProfileId, totalBudgetSeconds = QualityInterruptionClock.TotalSeconds, setupElapsedSeconds = _budget.Elapsed.TotalSeconds, sourceDirectory = _source, hashes,
            limitation = "Owned source/artifact setup only at RED; no SDK/probe/restore/result is synthesized."
        }, setup.Token);
    }
    public Task<IReadOnlyDictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>> ObserveFiveAsync()
    {
        lock (_sequenceGate)
        {
            if (_sequence is null)
            {
                SequenceStarts++;
                _sequence = QualityInterruptionOrchestrator.RunFiveAsync(_artifacts!, _source!, _budget,
                    TestContext.Current.CancellationToken);
            }
            return _sequence;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_artifacts is not null) await _artifacts.DisposeAsync();
        _budget.Dispose();
    }
}

[CollectionDefinition(QualityInterruptionCollection.Name, DisableParallelization = true)]
public sealed class QualityInterruptionCollection : ICollectionFixture<QualityInterruptionFixture>
{
    public const string Name = "Actual .NET720-second interruption fixture";
}

