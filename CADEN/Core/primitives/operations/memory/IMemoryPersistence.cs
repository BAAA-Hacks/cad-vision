namespace Core.Primitives.Operations.Memory
{
    public sealed class MemoryPersistenceConflictException : System.IO.IOException
    { public MemoryPersistenceConflictException(string message) : base(message) { } }
    public interface IMemoryPersistence
    {
        // Null means confirmed missing. All unreadable/corrupt storage errors must throw.
        string? Read();
        // Compare and atomically replace. Throw before commit on failure; never report failure after commit.
        // The adapter must reject external changes instead of overwriting them.
        void Commit(string? expectedContent, string newContent);
    }
}
