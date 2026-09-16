namespace RavensPort.Core.Tests.Storage;

/// <summary>
/// Every test class that sets <c>ManifestLocalStore.RootOverride</c> or
/// <c>ManifestBackupStore.RootOverride</c>, run one at a time.
///
/// Both are static fields — the only seam either store has for pointing test I/O away from the
/// machine's real %LocalAppData% — and xUnit runs test classes in parallel by default. Two classes
/// setting the override concurrently would each point the other's file writes at the wrong temp
/// directory, or clear it out from under a still-running test. See
/// <c>Vault/NativeCliRunnerCollection.cs</c> for the same problem solved the same way.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ManifestStoreCollection
{
    public const string Name = "ManifestStore";
}
