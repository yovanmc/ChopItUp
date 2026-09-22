// Collections run in parallel, at most four at once. Classes that touch process or machine state sit in
// ProcessStateCollection, which runs alone after the parallel ones.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
[assembly: TestCollectionOrderer("ChopItUp.Hub.Tests.SeededTestCollectionOrderer", "ChopItUp.Hub.Tests")]
[assembly: TestCaseOrderer("ChopItUp.Hub.Tests.SeededTestCaseOrderer", "ChopItUp.Hub.Tests")]
