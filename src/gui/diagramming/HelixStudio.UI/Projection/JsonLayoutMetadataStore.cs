using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.UI.Projection
{
    /// <summary>
    /// JSON-based implementation of ILayoutMetadataStore.
    /// Stores layout in {SolutionName}.csooptool-layout.json alongside the .sln file.
    /// </summary>
    public sealed class JsonLayoutMetadataStore : ILayoutMetadataStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private LayoutModel _layout;
        private readonly string _solutionPath;

        public JsonLayoutMetadataStore(string solutionPath)
        {
            _solutionPath = solutionPath ?? throw new ArgumentNullException("solutionPath");
            _layout = new LayoutModel(solutionPath);
        }

        public LayoutModel LayoutModel
        {
            get { return _layout; }
        }

        public NodeLayoutData GetPosition(string fullyQualifiedTypeName)
        {
            return _layout.GetNodeLayout(fullyQualifiedTypeName);
        }

        public void SetPosition(string fullyQualifiedTypeName, NodeLayoutData position)
        {
            _layout.SetNodeLayout(fullyQualifiedTypeName, position);
        }

        public void OnTypeRenamed(string oldFQN, string newFQN)
        {
            _layout.OnTypeRenamed(oldFQN, newFQN);
        }

        public void Save()
        {
            var layoutPath = GetLayoutPath();

            try
            {
                var json = JsonConvert.SerializeObject(_layout, JsonSettings);
                File.WriteAllText(layoutPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save layout: " + ex.Message);
            }
        }

        public void Load()
        {
            var layoutPath = GetLayoutPath();

            if (!File.Exists(layoutPath))
            {
                _layout = new LayoutModel(_solutionPath);
                return;
            }

            try
            {
                var json = File.ReadAllText(layoutPath);
                var loaded = JsonConvert.DeserializeObject<LayoutModel>(json, JsonSettings);

                if (loaded != null)
                {
                    loaded.SolutionFilePath = _solutionPath;
                    _layout = loaded;
                }
            }
            catch
            {
                _layout = new LayoutModel(_solutionPath);
            }
        }

        private string GetLayoutPath()
        {
            var solutionDir = Path.GetDirectoryName(_solutionPath);
            var solutionName = Path.GetFileNameWithoutExtension(_solutionPath);
            return Path.Combine(solutionDir, solutionName + ".csooptool-layout.json");
        }
    }
}
