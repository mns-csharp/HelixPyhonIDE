using System.Collections.Generic;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>Built-in Python expansion snippets exposed through normal completion shortcuts.</summary>
    public static class PythonSnippetCatalog
    {
        public static IEnumerable<CompletionItem> Items()
        {
            yield return Snippet("if", "if condition:\n    pass", "Python if block");
            yield return Snippet("ife", "if condition:\n    pass\nelse:\n    pass", "Python if/else block");
            yield return Snippet("for", "for item in iterable:\n    pass", "Python for loop");
            yield return Snippet("while", "while condition:\n    pass", "Python while loop");
            yield return Snippet("try", "try:\n    pass\nexcept Exception as error:\n    raise", "Python try/except block");
            yield return Snippet("tryf", "try:\n    pass\nfinally:\n    pass", "Python try/finally block");
            yield return Snippet("with", "with expression as value:\n    pass", "Python context-manager block");
            yield return Snippet("def", "def function_name(self):\n    pass", "Python method declaration");
            yield return Snippet("stat", "@staticmethod\ndef function_name():\n    pass", "Python static method declaration");
            yield return Snippet("ctor", "def __init__(self):\n    pass", "Python constructor");
            yield return Snippet("class", "class ClassName:\n    pass", "Python class declaration");
            yield return Snippet("main", "def main() -> int:\n    return 0\n\nif __name__ == \"__main__\":\n    raise SystemExit(main())", "Python application entry point");
            yield return Snippet("prop", "@property\ndef name(self):\n    return self._name", "Python property");
            yield return Snippet("pytest", "def test_behavior():\n    # Arrange\n    \n    # Act\n    \n    # Assert\n    assert True", "pytest test skeleton");
        }

        private static CompletionItem Snippet(string shortcut, string expansion, string description)
        {
            return new CompletionItem(shortcut, CompletionKind.Snippet, expansion,
                shortcut + " — code snippet", description);
        }
    }
}
