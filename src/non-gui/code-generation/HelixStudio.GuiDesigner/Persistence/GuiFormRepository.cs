namespace HelixPythonNamespace.GuiDesigner.Persistence
{
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using HelixPythonNamespace.GuiDesigner.Model;

    /// <summary>
    /// Loads/saves <see cref="GuiForm"/> to a <c>.designer.json</c> sidecar. Port of the C++
    /// <c>GuiFormRepository</c> (nlohmann/json → Newtonsoft).
    /// </summary>
    public sealed class GuiFormRepository
    {
        public const int CurrentVersion = 1;

        public GuiForm Load(string jsonPath)
        {
            if (!File.Exists(jsonPath)) return null;
            JObject doc;
            try { doc = JObject.Parse(File.ReadAllText(jsonPath)); }
            catch { return null; }

            GuiForm f = new GuiForm
            {
                FormClassName = (string)doc["formClassName"] ?? "MainForm",
                Title = (string)doc["title"] ?? "Form",
                Width = (int?)doc["width"] ?? 640,
                Height = (int?)doc["height"] ?? 480,
                SchemaVersion = (int?)doc["schemaVersion"] ?? 1,
                Framework = GuiFrameworks.FromToken((string)doc["framework"] ?? "tkinter"),
            };
            JToken controls = doc["controls"];
            if (controls != null && controls.Type == JTokenType.Array)
                foreach (JToken c in (JArray)controls) f.Controls.Add(JsonToControl((JObject)c));

            JToken vp = doc["viewport"];
            if (vp != null && vp.Type == JTokenType.Object)
            {
                f.Viewport.Zoom = (double?)vp["zoom"] ?? 1.0;
                f.Viewport.PanX = (int?)vp["panX"] ?? 0;
                f.Viewport.PanY = (int?)vp["panY"] ?? 0;
                f.Viewport.GridSize = (int?)vp["gridSize"] ?? 8;
                f.Viewport.SnapToGrid = (bool?)vp["snapToGrid"] ?? false;
            }
            return f;
        }

        public bool Save(string jsonPath, GuiForm form)
        {
            JObject doc = new JObject
            {
                ["schemaVersion"] = CurrentVersion,
                ["formClassName"] = form.FormClassName,
                ["title"] = form.Title,
                ["width"] = form.Width,
                ["height"] = form.Height,
                ["framework"] = GuiFrameworks.ToToken(form.Framework),
            };
            JArray arr = new JArray();
            foreach (GuiControl c in form.Controls) arr.Add(ControlToJson(c));
            doc["controls"] = arr;

            GuiViewport d = new GuiViewport();
            if (form.Viewport.Zoom != d.Zoom || form.Viewport.PanX != d.PanX || form.Viewport.PanY != d.PanY
                || form.Viewport.GridSize != d.GridSize || form.Viewport.SnapToGrid != d.SnapToGrid)
            {
                doc["viewport"] = new JObject
                {
                    ["zoom"] = form.Viewport.Zoom,
                    ["panX"] = form.Viewport.PanX,
                    ["panY"] = form.Viewport.PanY,
                    ["gridSize"] = form.Viewport.GridSize,
                    ["snapToGrid"] = form.Viewport.SnapToGrid,
                };
            }

            try
            {
                string dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(jsonPath, doc.ToString(Formatting.Indented));
                return true;
            }
            catch { return false; }
        }

        public static string PySourceFor(string designerJsonPath)
        {
            string dir = Path.GetDirectoryName(designerJsonPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(designerJsonPath);
            const string suffix = ".designer";
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, System.StringComparison.Ordinal))
                stem = stem.Substring(0, stem.Length - suffix.Length);
            return Path.Combine(dir, stem + ".py");
        }

        /// <summary>Inverse of <see cref="PySourceFor"/>: the <c>.designer.json</c> sidecar path for a
        /// given <c>.py</c> source path — e.g. <c>main.py</c> → <c>main.designer.json</c>.</summary>
        public static string DesignerJsonFor(string pySourcePath)
        {
            string dir = Path.GetDirectoryName(pySourcePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(pySourcePath);
            return Path.Combine(dir, stem + ".designer.json");
        }

        private static JObject ControlToJson(GuiControl c)
        {
            JObject props = new JObject();
            foreach (KeyValuePair<string, string> kv in c.Props) props[kv.Key] = kv.Value;
            JArray events = new JArray();
            foreach (GuiEvent e in c.Events)
                events.Add(new JObject { ["event"] = e.EventName, ["handler"] = e.HandlerName });
            return new JObject
            {
                ["id"] = c.Id,
                ["kind"] = GuiControlKinds.ToToken(c.Kind),
                ["name"] = c.Name,
                ["parentId"] = c.ParentId,
                ["x"] = c.X,
                ["y"] = c.Y,
                ["w"] = c.Width,
                ["h"] = c.Height,
                ["props"] = props,
                ["events"] = events,
            };
        }

        private static GuiControl JsonToControl(JObject j)
        {
            GuiControl c = new GuiControl
            {
                Id = (string)j["id"] ?? "",
                Kind = GuiControlKinds.FromToken((string)j["kind"] ?? "Button"),
                Name = (string)j["name"] ?? "",
                ParentId = (string)j["parentId"] ?? "",
                X = (int?)j["x"] ?? 0,
                Y = (int?)j["y"] ?? 0,
                Width = (int?)j["w"] ?? 100,
                Height = (int?)j["h"] ?? 24,
            };
            JToken props = j["props"];
            if (props != null && props.Type == JTokenType.Object)
                foreach (JProperty p in ((JObject)props).Properties())
                    c.Props[p.Name] = (string)p.Value;
            JToken events = j["events"];
            if (events != null && events.Type == JTokenType.Array)
                foreach (JToken e in (JArray)events)
                    c.Events.Add(new GuiEvent { EventName = (string)e["event"] ?? "", HandlerName = (string)e["handler"] ?? "" });
            return c;
        }
    }
}
