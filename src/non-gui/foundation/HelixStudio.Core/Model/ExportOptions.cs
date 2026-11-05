namespace HelixPythonNamespace.Core.Model
{
    public class ExportOptions
    {
        public ExportOptions()
        {
        }

        public bool SingleFileOutput { get; set; }
        public string OutputPath { get; set; }
        public bool IncludeDocComments { get; set; }
    }
}
