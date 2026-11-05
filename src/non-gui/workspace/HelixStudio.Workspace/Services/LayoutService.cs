using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Implementation of ILayoutService for managing layout persistence.
    /// </summary>
    public class LayoutService : ILayoutService
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        public LayoutModel Load(string solutionPath)
        {
            var layoutPath = GetLayoutPath(solutionPath);

            if (!File.Exists(layoutPath))
            {
                return new LayoutModel(solutionPath);
            }

            try
            {
                var json = File.ReadAllText(layoutPath);
                var layout = JsonConvert.DeserializeObject<LayoutModel>(json, JsonSettings);

                if (layout != null)
                {
                    layout.SolutionFilePath = solutionPath;
                    return layout;
                }
            }
            catch
            {
                // If loading fails, return a new layout model
            }

            return new LayoutModel(solutionPath);
        }

        public void Save(LayoutModel layout)
        {
            if (layout == null || string.IsNullOrEmpty(layout.SolutionFilePath))
                return;

            var layoutPath = GetLayoutPath(layout.SolutionFilePath);

            try
            {
                var json = JsonConvert.SerializeObject(layout, JsonSettings);
                File.WriteAllText(layoutPath, json);
            }
            catch (Exception ex)
            {
                // Log but don't throw - layout is not critical
                System.Diagnostics.Debug.WriteLine("Failed to save layout: " + ex.Message);
            }
        }

        public string GetLayoutPath(string solutionPath)
        {
            var solutionDir = Path.GetDirectoryName(solutionPath);
            var solutionName = Path.GetFileNameWithoutExtension(solutionPath);
            return Path.Combine(solutionDir, solutionName + ".csooptool-layout.json");
        }
    }
}
