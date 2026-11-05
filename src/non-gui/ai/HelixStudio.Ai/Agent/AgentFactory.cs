using System;
using System.Collections.Generic;
using HelixPythonNamespace.Ai.Abstractions;
using HelixPythonNamespace.Ai.Providers;
using HelixPythonNamespace.Ai.Tools;

namespace HelixPythonNamespace.Ai.Agent
{
    /// <summary>
    /// The providers the IDE knows about, resolved by id.
    ///
    /// Adding a vendor is adding one entry here plus its <see cref="IAiProvider"/> — nothing in the
    /// agent loop, the tools, or the panel changes.
    /// </summary>
    public sealed class AiProviderRegistry
    {
        private readonly List<IAiProvider> _providers = new List<IAiProvider>();

        /// <param name="anthropicKey">Reads the Anthropic key from settings at call time, or null.</param>
        /// <param name="openAiKey">Reads the OpenAI key from settings at call time, or null.</param>
        public AiProviderRegistry(Func<string> anthropicKey = null, Func<string> openAiKey = null)
        {
            _providers.Add(new AnthropicProvider(anthropicKey));
            _providers.Add(new OpenAiProvider(openAiKey));
        }

        public IReadOnlyList<IAiProvider> Providers { get { return _providers; } }

        /// <summary>The provider with this id, or null.</summary>
        public IAiProvider Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (IAiProvider provider in _providers)
                if (string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) return provider;
            return null;
        }

        /// <summary>
        /// The provider to use when the user has not chosen: their pick if it is configured,
        /// otherwise the first configured one, otherwise the first — so the panel can still load
        /// and explain how to add a key rather than failing to construct.
        /// </summary>
        public IAiProvider Resolve(string preferredId)
        {
            IAiProvider preferred = Find(preferredId);
            if (preferred != null && preferred.IsConfigured) return preferred;
            foreach (IAiProvider provider in _providers)
                if (provider.IsConfigured) return provider;
            return preferred ?? _providers[0];
        }
    }

    /// <summary>Builds a ready-to-run agent for one solution.</summary>
    public static class AgentFactory
    {
        /// <summary>
        /// The agent's standing instructions.
        ///
        /// Written for current models, which follow instructions closely and literally — so this
        /// states the environment and the rules that are genuinely non-obvious, and does not pad
        /// with generic virtues ("be helpful", "be accurate") or emphasis the model does not need.
        /// The HelixPython policy rules are here because no amount of general Python knowledge
        /// would tell the model that a module-level `def` is a hard compile blocker in this IDE.
        /// </summary>
        public static string BuildSystemPrompt(string solutionRoot, string solutionName)
        {
            return
"You are the AI agent embedded in HelixPython, a visual Python IDE. You work directly in the "
+ "user's open solution using the tools you have been given.\n"
+ "\n"
+ "Solution: " + (string.IsNullOrWhiteSpace(solutionName) ? "(unnamed)" : solutionName) + "\n"
+ "Root: " + solutionRoot + "\n"
+ "\n"
+ "# This IDE enforces three rules that ordinary Python does not\n"
+ "Code that violates any of them cannot Compile, Run, or Debug — they are hard blockers, not "
+ "lint, and `# noqa` does not suppress them. Write code that satisfies them from the start, and "
+ "when you diagnose a problem, check these first:\n"
+ "\n"
+ "1. PY8001 — every source file must belong to a workspace member project listed in "
+ "[tool.uv.workspace].members. Never fix an import by adding to sys.path or PYTHONPATH; that is "
+ "the exact anti-pattern this rule exists to prevent. Move the file into a member instead.\n"
+ "2. PY8002 — user code is object-oriented. A module-level `def` is an error, with two "
+ "exceptions: `def main()` in an executable package, and — inside a project whose pyproject.toml "
+ "declares kind = \"test\" — the test-runner protocol (test_* functions, @pytest.fixture, "
+ "pytest_* hooks, setup_module/teardown_module). An ordinary helper is still a class, even in a "
+ "test project.\n"
+ "3. PY8003 — only a project with kind = \"test\" may contain test files. Tests live in a sibling "
+ "<project>_tests project that references the project it exercises, never inside it.\n"
+ "\n"
+ "# Working here\n"
+ "Read before you edit — use read_file, grep, and glob to find out what the code actually says "
+ "rather than assuming. Prefer edit_file over write_file for changes to existing files.\n"
+ "\n"
+ "Every write and every command is shown to the user for approval before it happens, so say what "
+ "you are about to do and why in one short sentence. If the user denies something, do not retry "
+ "it — pick another approach or ask what they would prefer.\n"
+ "\n"
+ "Deliver what was asked, at the scope intended. Make routine judgment calls yourself; check in "
+ "only when different readings would lead to materially different work. Finish the whole task and "
+ "report completion only when it is actually done — if part of it is blocked, do the rest and say "
+ "plainly what is missing and why. Report outcomes faithfully: if tests fail, say so with the "
+ "output.\n"
+ "\n"
+ "Keep responses brief. Lead with the outcome, then the detail that changes what the user does "
+ "next.";
        }

        /// <summary>
        /// Assemble the agent: sandbox rooted at the solution, the six tools, and a shared edit
        /// journal so the panel can offer a single Undo for everything one run touched.
        /// </summary>
        public static AgentSession Create(
            IAiProvider provider,
            string solutionRoot,
            string solutionName,
            IAgentPermissionPolicy policy,
            out EditJournal journal)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (policy == null) throw new ArgumentNullException("policy");

            var workspace = new AgentWorkspace(solutionRoot);
            journal = new EditJournal();

            var tools = new List<IAgentTool>
            {
                new ReadFileTool(workspace),
                new GlobTool(workspace),
                new GrepTool(workspace),
                new EditFileTool(workspace, journal),
                new WriteFileTool(workspace, journal),
                new RunCommandTool(workspace),
            };

            return new AgentSession(provider, tools, policy,
                BuildSystemPrompt(workspace.RootDirectory, solutionName))
            {
                Model = provider.Models.Count > 0 ? provider.Models[0] : null,
            };
        }
    }
}
