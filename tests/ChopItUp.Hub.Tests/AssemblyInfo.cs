// Collections run in parallel, at most two at once: on the four-vCPU CI runner two and four measure the
// same (medians 356 s and 316 s over three runs each, ranges overlapping, against 757 s serial), and the
// one crash this suite suffers, a host start failing with WSAENOBUFS, scales with concurrent hubs.
// Classes that touch process or machine state sit in ProcessStateCollection, which runs alone after
// the parallel ones. docs/testing/hub-test-resources.md has the table.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
[assembly: TestCollectionOrderer("ChopItUp.Hub.Tests.SeededTestCollectionOrderer", "ChopItUp.Hub.Tests")]
[assembly: TestCaseOrderer("ChopItUp.Hub.Tests.SeededTestCaseOrderer", "ChopItUp.Hub.Tests")]
