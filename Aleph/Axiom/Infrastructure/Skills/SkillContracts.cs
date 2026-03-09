// CONTRACT / INVARIANTS
// - SkillMetadata: immutable record holding parsed YAML frontmatter fields.
// - SkillPlaybook: metadata + markdown body + content hash + file size.
// - SkillRegistrySnapshot: immutable, thread-safe snapshot of all loaded skills.
//   - BySkillName keys are normalized to lowercase for case-insensitive lookup.
// - ISkillRegistry: singleton interface — Snapshot always returns a valid (possibly empty) snapshot.

namespace Aleph
{
    public sealed record SkillMetadata
    {
        public required string SkillName { get; init; }
        public required string DisplayName { get; init; }
        public required string Version { get; init; }
        public required string Description { get; init; }
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RequiredTools { get; init; } = Array.Empty<string>();
        public bool Deprecated { get; init; }
        public IReadOnlyDictionary<string, object> Extras { get; init; } = new Dictionary<string, object>();
    }

    public sealed record SkillPlaybook
    {
        public required SkillMetadata Metadata { get; init; }
        public required string MarkdownBody { get; init; }
        public required string ContentHash { get; init; }
        public required long FileSizeBytes { get; init; }
    }

    public sealed class SkillRegistrySnapshot
    {
        public static readonly SkillRegistrySnapshot Empty = new(Array.Empty<SkillPlaybook>());

        public IReadOnlyList<SkillPlaybook> Playbooks { get; }
        public IReadOnlyDictionary<string, SkillPlaybook> BySkillName { get; }

        public SkillRegistrySnapshot(IReadOnlyList<SkillPlaybook> playbooks)
        {
            Playbooks = playbooks;
            BySkillName = playbooks.ToDictionary(
                p => p.Metadata.SkillName.ToLowerInvariant(),
                p => p,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public interface ISkillRegistry
    {
        SkillRegistrySnapshot Snapshot { get; }
        Task LoadAsync(CancellationToken ct = default);
    }
}
