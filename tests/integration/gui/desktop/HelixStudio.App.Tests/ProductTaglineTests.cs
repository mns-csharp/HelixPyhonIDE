using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 214: the About box and Welcome screen described the product as a Python-only
    /// "UML-style class diagram editor for Python solutions", which misrepresents a tool that
    /// now targets Python, Rust, C#, Julia, CUDA, TOML and more. Guard against reverting to the
    /// Python-only framing (intent-level: reject the stale phrase, require non-empty copy).
    /// </summary>
    [TestClass]
    public class ProductTaglineTests
    {
        [TestMethod]
        public void AboutSubtitle_IsNotPythonOnly()
        {
            using (var dlg = new AboutDialog())
            {
                FieldInfo f = typeof(AboutDialog).GetField(
                    "subtitleLabel", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(f, "subtitleLabel field expected on AboutDialog.");
                string text = ((Label)f.GetValue(dlg)).Text ?? "";

                Assert.IsFalse(string.IsNullOrWhiteSpace(text), "About subtitle must not be blank.");
                Assert.IsFalse(text.Contains("for Python solutions"),
                    "About subtitle must reflect the multi-language product, not Python only. Was: " + text);

                // If the copy contains an '&', the label MUST disable mnemonic parsing, otherwise
                // WinForms swallows the '&' and renders a blank gap (regression from iter 214).
                var label = (Label)f.GetValue(dlg);
                if (text.Contains("&"))
                    Assert.IsFalse(label.UseMnemonic,
                        "A label whose text contains '&' must set UseMnemonic=false so the '&' renders.");
            }
        }
    }
}
