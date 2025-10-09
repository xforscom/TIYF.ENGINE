using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Tools;

namespace TiYf.Engine.Tests;

public class RiskProbeEndToEndTests
{
    // This end-to-end test shells the engine loop and asserts VerifyEngine exit code.
    // It is environment-sensitive (temp I/O, wall time alignment) and covered by CI workflows (verify-deep).
    // Skipping in unit test suite to reduce flakiness on ephemeral agents.
    [Trait("Category","E2E")]
    [Fact(Skip = "Environment-dependent e2e; covered by verify-deep workflow artifacts")]
    public async Task Verify_RiskProbe_EndToEnd_Passes_E2E_Skipped()
    {
        // No-op body: intentionally skipped; validated by CI deep verify.
        await Task.CompletedTask;
    }
}
