namespace ChopItUp.Hub.Tests;

/// <summary>Test classes that change process-wide state (environment variables, the console streams)
/// or read machine-wide tables (the TCP table, the process list). xUnit runs this collection alone,
/// after every parallel collection has finished, so none of these can disturb or be disturbed by a
/// concurrent test. <see cref="ParallelismPolicyTests"/> keeps membership honest.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "Process state";
}
