using Xunit.Abstractions;
using Xunit.Sdk;

namespace RavensPort.SystemTests;

/// <summary>
/// Runs the approval stages in the order they are numbered.
///
/// xUnit does not order tests, and for almost every suite that is right: a test that depends on
/// another running first is usually a test with a hidden dependency. This one is the exception. The
/// stages are a sequence — the vault is empty only before anything is added, and the restart proves
/// nothing until there is state that had to survive it — and the alternative to ordering them was
/// the single opaque method they used to be.
///
/// Ordering is by method name, which is why every stage is named Stage01_, Stage02_ and so on. A
/// numeric prefix is cruder than an attribute and harder to get wrong: the order is visible in the
/// source, in the CI log, and in any test explorer, without anyone having to look it up.
/// </summary>
public sealed class StageOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderBy(c => c.TestMethod.Method.Name, StringComparer.Ordinal);
}
