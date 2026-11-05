namespace HelixPythonNamespace.Lint.Tests
{
    using System;
    using System.Text;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Lint;

    /// <summary>
    /// Regression tests for cooperative cancellation in the linter. Running the rule library over a
    /// cancelled token must abort with <see cref="OperationCanceledException"/> (checked between the
    /// parse and each rule) rather than running every rule to completion.
    /// </summary>
    [TestClass]
    public class CancellationTests
    {
        private static string BigSource(int lines)
        {
            StringBuilder sb = new StringBuilder(lines * 12);
            for (int i = 0; i < lines; i++) sb.Append("x").Append(i).Append(" = ").Append(i).Append("\n");
            return sb.ToString();
        }

        private static CancellationToken AlreadyCancelled()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [TestMethod]
        public void Linter_HonorsAlreadyCancelledToken()
        {
            PythonLinter linter = new PythonLinter();
            Assert.ThrowsException<OperationCanceledException>(
                () => linter.Run(BigSource(1000), "big.py", AlreadyCancelled()));
        }

        [TestMethod]
        public void Linter_NoToken_Completes()
        {
            PythonLinter linter = new PythonLinter();
            Assert.IsNotNull(linter.Run(BigSource(50), "ok.py"));
        }
    }
}
