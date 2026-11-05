using System;
using System.IO;
using System.Runtime.Serialization.Json;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Services;
using HelixPythonNamespace.Export.Metadata;

namespace HelixPythonNamespace.Export
{
    public sealed class JsonMetadataExportService : IMetadataExporter
    {
        private readonly MetadataMapper _mapper;

        public JsonMetadataExportService()
        {
            _mapper = new MetadataMapper();
        }

        public void ExportJson(Diagram diagram, string filePath)
        {
            if (diagram == null)
            {
                throw new ArgumentNullException("diagram");
            }
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException("filePath");
            }

            ArchitectureMetadataDto metadata = _mapper.ToMetadata(diagram);

            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ArchitectureMetadataDto));

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                serializer.WriteObject(stream, metadata);
            }
        }
    }
}
