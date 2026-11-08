namespace Chameleon.Engine;

/// A persisted entity that names the PROPERTY holding its natural key (used as document _id).
/// Returning the property name — not its value — lets a storage backend map that member to
/// _id without guessing by convention, and without adding a redundant serialized field.
/// Optional: an entity that doesn't implement it lets the backend fall back to a generated id.
public interface IHasNaturalKey
{
    /// Name of the key property (e.g. "TxId", "Rps"). Must be a real mapped member.
    string NaturalKeyProperty { get; }
}
