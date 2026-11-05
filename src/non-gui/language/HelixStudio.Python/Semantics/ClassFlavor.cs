namespace HelixPythonNamespace.Python.Semantics
{
    /// <summary>
    /// Classifies a Python class definition into the UML-style kind the tool
    /// renders. `Protocol` and `AbstractBaseClass` both map to "interface"-like
    /// semantics; see <see cref="ProtocolClassifier"/> for the exact rules.
    /// </summary>
    public enum ClassFlavor
    {
        ConcreteClass,
        Protocol,
        AbstractBaseClass,
        Dataclass,
        Enum,
        NamedTuple,
        TypedDict
    }
}
