namespace HelixPythonNamespace.Persistence
{
    using System;
    using System.IO;
    using System.Runtime.Serialization.Json;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.Persistence.Dto;
    using HelixPythonNamespace.Persistence.Mapping;

    public sealed class JsonDiagramRepository : IDiagramRepository
    {
        public const int CurrentFormatVersion = 1;

        private readonly DiagramMapper _mapper;
        private readonly DataContractJsonSerializer _serializer;

        public JsonDiagramRepository()
        {
            _mapper = new DiagramMapper();
            _serializer = new DataContractJsonSerializer(typeof(DiagramFileDto));
        }

        public Diagram Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    string.Format("Diagram file not found: '{0}'", filePath),
                    filePath);
            }

            DiagramFileDto dto;

            using (FileStream stream = File.OpenRead(filePath))
            {
                dto = (DiagramFileDto)_serializer.ReadObject(stream);
            }

            if (dto.FormatVersion > CurrentFormatVersion)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "The file format version {0} is newer than the supported version {1}. Please update the application.",
                        dto.FormatVersion,
                        CurrentFormatVersion));
            }

            return _mapper.ToDomain(dto);
        }

        public void Save(Diagram diagram, string filePath)
        {
            DiagramFileDto dto = _mapper.ToDto(diagram, CurrentFormatVersion);

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = File.Create(filePath))
            {
                _serializer.WriteObject(stream, dto);
            }
        }
    }
}
