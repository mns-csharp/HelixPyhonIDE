namespace HelixPythonNamespace.Core.Model.Enums
{
    public enum AccessModifier
    {
        Public = 0,
        Protected = 1,
        Internal = 2,
        Private = 3,
        // C# 7.2 compound modifiers; values appended so existing persisted JSON stays valid.
        ProtectedInternal = 4,
        PrivateProtected = 5,
        // C# 11 file-scoped type modifier.
        File = 6
    }
}

