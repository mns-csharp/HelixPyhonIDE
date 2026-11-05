namespace HelixPythonNamespace.Persistence.Migration
{
    using HelixPythonNamespace.Persistence.Dto;

    internal interface IFormatMigration
    {
        int FromVersion { get; }
        int ToVersion { get; }
        DiagramFileDto Migrate(DiagramFileDto dto);
    }
}
