namespace DeliveryForge.Tool;

// Physical image ownership only. Every node keeps its own root/configuration,
// runtime trust, native/session owner and review issuer. No DATA grants a borrow.
internal sealed class BatchWindowsImages(ToolLifetime original)
{
    internal sealed class Entry(AdmissionFile pin)
    {
        internal readonly AdmissionFile Pin = pin;
        internal BoundedFiles.RetainedNativeImage? Image;
        internal bool AcquisitionEntered, Failed;
        internal readonly HashSet<WindowsImageUse> Uses = new(ReferenceEqualityComparer.Instance);
    }
    readonly object gate = new();
    // Case-insensitive lookup never authorizes a different exact pin spelling.
    // Aliases remain separate acquisitions/costs; conservative refusal is intentional.
    readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    bool sealedPlan, closing, closed, uncertain;
    int uses;

    void Current(ToolLifetime actual)
    {
        if (!ReferenceEquals(actual, original)) throw new ToolUsageException("Batch image proof belongs to another original.");
        original.Check();
        if (closing || closed || uncertain || original.HasExplicitUnknown || entries.Values.Any(e => e.Failed))
            throw new IOException("Original batch image ownership is closed or uncertain.");
    }
    static AdmissionFile Pin(AdmissionFile pin)
    {
        if (pin is null || !Path.IsPathFullyQualified(pin.Path) || pin.Bytes is < 1 or > BoundedFiles.MaximumNativeImageBytes ||
            pin.Sha256 is not { Length: 64 } || pin.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ToolUsageException("Exact finite batch image selection required.");
        return pin with { Path = Path.GetFullPath(pin.Path) };
    }
    Entry Selected(AdmissionFile pin)
    {
        var selected = Pin(pin);
        if (!entries.TryGetValue(selected.Path, out var entry) || entry.Pin != selected)
            throw new ToolUsageException("Image was not in the immutable original batch selection.");
        return entry;
    }
    internal void Plan(AdmissionFile pin, ToolLifetime actual)
    {
        lock (gate)
        {
            Current(actual);
            if (!OperatingSystem.IsWindows() || sealedPlan) throw new ToolUsageException("Batch image planning is Windows-only and precedes actual use.");
            var selected = Pin(pin);
            if (entries.TryGetValue(selected.Path, out var present))
            {
                if (present.Pin != selected) throw new ToolUsageException("One batch image path has conflicting selections.");
                return;
            }
            if (entries.Count >= 64) throw new ToolUsageException("Finite two-images-per-node batch selection exhausted.");
            entries.Add(selected.Path, new(selected));
        }
    }
    InstalledHostAdmission.RuntimeTrust.ReadRequirement Pending()
    {
        var pending = entries.Values.Where(e => !e.AcquisitionEntered).ToArray();
        return new(checked(pending.Length * 2), pending.Aggregate(0L, (sum, e) => checked(sum + e.Pin.Bytes + 1)));
    }
    internal void SealAndRequireCapacity(ToolLifetime actual, int operations, long bytes)
    {
        lock (gate)
        {
            Current(actual); if (sealedPlan) throw new IOException("Batch image plan already sealed.");
            sealedPlan = true; var pending = Pending();
            BoundedFiles.RequireReadCapacity(original, checked(operations + pending.Operations), checked(bytes + pending.Bytes));
        }
    }
    internal void RequireNodeCapacity(ToolLifetime actual, AdmissionFile worker, AdmissionFile reviewer,
        InstalledHostAdmission.RuntimeTrust.ReadRequirement complete, InstalledHostAdmission.RuntimeTrust.ReadRequirement images)
    {
        lock (gate)
        {
            Current(actual); if (!sealedPlan) throw new IOException("Batch image plan is not sealed.");
            _ = Selected(worker); _ = Selected(reviewer); var pending = Pending();
            BoundedFiles.RequireReadCapacity(original, checked(complete.Operations - images.Operations + pending.Operations),
                checked(complete.Bytes - images.Bytes + pending.Bytes));
        }
    }
    BoundedFiles.RetainedNativeImage Image(Entry entry, CancellationToken token)
    {
        if (!sealedPlan) throw new IOException("Batch image plan is not sealed.");
        if (entry.Image is null)
        {
            if (entry.AcquisitionEntered) throw new IOException("Batch image acquisition cannot retry.");
            entry.AcquisitionEntered = true;
            try { entry.Image = BoundedFiles.RetainVerifiedNativeImage(entry.Pin, original, token); }
            catch { entry.Failed = true; throw; }
        }
        try { entry.Image.Check(token); return entry.Image; }
        catch { entry.Failed = true; throw; }
    }
    internal void Verify(AdmissionFile pin, ToolLifetime actual)
    {
        lock (gate) { Current(actual); _ = Image(Selected(pin), original.Token); }
    }
    internal WindowsImageUse Borrow(AdmissionFile pin, ToolLifetime actual, CancellationToken token)
    {
        lock (gate)
        {
            Current(actual); var entry = Selected(pin); var image = Image(entry, token);
            if (uses >= 64) throw new IOException("Finite actual batch image uses exhausted.");
            var use = new WindowsImageUse(image, original, this, entry); entry.Uses.Add(use); uses++; return use;
        }
    }
    internal void Check(Entry entry, WindowsImageUse use, CancellationToken token)
    {
        lock (gate)
        {
            Current(original); if (!entry.Uses.Contains(use)) throw new IOException("Batch image use is no longer retained.");
            _ = Image(entry, token);
        }
    }
    internal void Release(Entry entry, WindowsImageUse use)
    {
        lock (gate)
        {
            if (uncertain || !entry.Uses.Remove(use)) throw new IOException("Batch image use cannot be released twice or after uncertainty.");
        }
    }
    internal void RetainUnknown(Entry entry)
    {
        lock (gate) { uncertain = true; original.RetainUnknown(); entry.Image?.RetainUnknown(); }
    }
    internal void CloseAfterOwners()
    {
        lock (gate)
        {
            if (closing) throw new IOException("Batch image first-close already entered; no retry.");
            closing = true;
            if (uncertain || original.HasRetainedUnknown || entries.Values.Any(e => e.Uses.Count != 0))
            {
                uncertain = true; original.RetainUnknown();
                foreach (var entry in entries.Values) entry.Image?.RetainUnknown();
                return;
            }
            List<Exception> failures = [];
            foreach (var entry in entries.Values)
            {
                if (entry.Image is not { } image) continue;
                try { image.CloseAfterSettledOwner(); }
                catch (Exception error) { failures.Add(error); if (!image.IsClosed) image.RetainUnknown(); }
            }
            closed = entries.Values.All(e => e.Image is null || e.Image.IsClosed);
            if (!closed) { uncertain = true; original.RetainUnknown(); }
            if (failures.Count != 0) throw new AggregateException("Batch image first-disposition failed.", failures);
        }
    }
}

// A node releases only its use; physical first-close belongs to the batch.
// The ordinary single-node/conformance route still owns and closes its image.
internal sealed class WindowsImageUse(BoundedFiles.RetainedNativeImage image, ToolLifetime original,
    BatchWindowsImages? owner = null, BatchWindowsImages.Entry? entry = null)
{
    bool released, uncertain;
    internal bool IsReleased => released;
    internal bool Unknown => uncertain || image.Unknown;
    internal static WindowsImageUse Open(AdmissionFile pin, ToolLifetime original, CancellationToken token) =>
        new(BoundedFiles.RetainVerifiedNativeImage(pin, original, token), original);
    internal void Check(CancellationToken token = default)
    {
        if (released) throw new IOException("Image use was already released.");
        if (owner is null) image.Check(token); else owner.Check(entry!, this, token);
    }
    internal void RetainUnknown()
    {
        uncertain = true;
        if (owner is null) image.RetainUnknown(); else owner.RetainUnknown(entry!);
    }
    internal void Close(bool cleanup)
    {
        if (released) return;
        if (owner is null) { try { image.Close(cleanup); } finally { released = image.IsClosed; } return; }
        if (uncertain) { RetainUnknown(); throw new IOException("Uncertain image use cannot release its owner."); }
        Exception? primary = null;
        try { if (cleanup) { original.BeginFailureCleanup(); _ = original.CleanupRemaining; } else original.Check(); }
        catch (Exception error) { primary = error; }
        try { owner.Release(entry!, this); released = true; }
        catch (Exception release)
        {
            if (primary is not null) throw new AggregateException("Image-use admission and owner release failed.", primary, release);
            throw;
        }
        try { if (cleanup || primary is not null) { original.BeginFailureCleanup(); _ = original.CleanupRemaining; } else original.Check(); }
        catch (Exception post) { if (primary is not null) throw new AggregateException("Image-use release and same-original postcheck failed.", primary, post); throw; }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
