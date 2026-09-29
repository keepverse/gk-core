using Xunit;

// StructureCatalog is a process-wide catalog configured by the module initializer and temporarily
// replaced by StructureCatalogImportTests. Serialising this assembly prevents an import fixture's
// intentionally invalid corpus from racing production-shaped world tests that read the same cache.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
