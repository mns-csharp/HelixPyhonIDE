using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.AddIns.Kit;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 250: three Luna accent themes (Blue / Green / Crimson). LunaThemes is the single active
    /// palette that both LunaForm (caption) and LunaColors (menus, toolbars, status bar) read, so a
    /// switch recolors the whole app. Guards the registry + that LunaColors follows the active theme.
    /// </summary>
    [TestClass]
    public class LunaThemeTests
    {
        [TestCleanup]
        public void Reset() => LunaThemes.Apply(LunaThemeKind.Blue);

        [TestMethod]
        public void FromName_IsCaseInsensitive_AndDefaultsToBlue()
        {
            Assert.AreEqual(LunaThemeKind.Green, LunaThemes.FromName("green"));
            Assert.AreEqual(LunaThemeKind.Crimson, LunaThemes.FromName("CRIMSON"));
            Assert.AreEqual(LunaThemeKind.Blue, LunaThemes.FromName("Blue"));
            Assert.AreEqual(LunaThemeKind.Blue, LunaThemes.FromName("nonsense"));
            Assert.AreEqual(LunaThemeKind.Blue, LunaThemes.FromName(null));
        }

        [TestMethod]
        public void Apply_SwitchesCurrentPalette()
        {
            LunaThemes.Apply(LunaThemeKind.Green);
            Assert.AreEqual(LunaThemeKind.Green, LunaThemes.Kind);
            Assert.AreSame(LunaThemes.Green, LunaThemes.Current);

            LunaThemes.Apply(LunaThemeKind.Crimson);
            Assert.AreSame(LunaThemes.Crimson, LunaThemes.Current);
        }

        [TestMethod]
        public void LunaColors_AccentFollowsActiveTheme()
        {
            LunaThemes.Apply(LunaThemeKind.Blue);
            var blueStatus = LunaColors.StatusBarBackground;

            LunaThemes.Apply(LunaThemeKind.Green);
            Assert.AreEqual(LunaThemes.Green.StatusBottom, LunaColors.StatusBarBackground,
                "LunaColors.StatusBarBackground must follow the active theme");
            Assert.AreNotEqual(blueStatus, LunaColors.StatusBarBackground, "green status differs from blue");
            Assert.AreEqual(LunaThemes.Green.Accent, LunaColors.Accent);

            LunaThemes.Apply(LunaThemeKind.Crimson);
            Assert.AreEqual(LunaThemes.Crimson.TabActive, LunaColors.TabActiveBackground);
            Assert.AreEqual(LunaThemes.Crimson.Selection, LunaColors.Selection);
        }

        /// <summary>
        /// The docked tool windows' title bar is part of the skin. Its three colours were written
        /// down as the Luna Blue literals, so a green window opened with a blue Solution Explorer
        /// caption on it — the one blue thing left on screen.
        /// </summary>
        [TestMethod]
        public void ToolWindowCaption_FollowsActiveTheme()
        {
            LunaThemes.Apply(LunaThemeKind.Blue);
            var blueBack = ToolWindowChrome.HeaderBack;
            var blueEdge = ToolWindowChrome.HeaderEdge;
            var blueLight = ToolWindowChrome.HeaderHighlight;

            LunaThemes.Apply(LunaThemeKind.Green);
            Assert.AreEqual(LunaThemes.Green.TabActive, ToolWindowChrome.HeaderBack,
                "the tool-window caption must be the active theme's accent");
            Assert.AreNotEqual(blueBack, ToolWindowChrome.HeaderBack, "green caption differs from blue");
            Assert.AreNotEqual(blueEdge, ToolWindowChrome.HeaderEdge, "its under-line follows too");
            Assert.AreNotEqual(blueLight, ToolWindowChrome.HeaderHighlight, "its inner light follows too");

            LunaThemes.Apply(LunaThemeKind.Crimson);
            Assert.AreEqual(LunaThemes.Crimson.TabActive, ToolWindowChrome.HeaderBack);
        }

        /// <summary>The lift is a shade of the face, so it stays lighter than it in every theme.</summary>
        [TestMethod]
        public void ToolWindowCaption_HighlightIsLighterThanFace_AndEdgeDarker()
        {
            foreach (LunaThemeKind kind in new[] { LunaThemeKind.Blue, LunaThemeKind.Green, LunaThemeKind.Crimson })
            {
                LunaThemes.Apply(kind);
                var face = ToolWindowChrome.HeaderBack;
                Assert.IsTrue(ToolWindowChrome.HeaderHighlight.GetBrightness() > face.GetBrightness(),
                    kind + ": the inner light must be lighter than the caption face");
                Assert.IsTrue(ToolWindowChrome.HeaderEdge.GetBrightness() < face.GetBrightness(),
                    kind + ": the under-line must be darker than the caption face");
            }
        }

        [TestMethod]
        public void NeutralColors_StayConstantAcrossThemes()
        {
            LunaThemes.Apply(LunaThemeKind.Blue);
            var faceBlue = LunaColors.MainBackground;
            var inputBlue = LunaColors.InputBackground;
            LunaThemes.Apply(LunaThemeKind.Crimson);
            Assert.AreEqual(faceBlue, LunaColors.MainBackground, "the tan face is theme-independent");
            Assert.AreEqual(inputBlue, LunaColors.InputBackground, "input fields stay white in every theme");
        }
    }
}
