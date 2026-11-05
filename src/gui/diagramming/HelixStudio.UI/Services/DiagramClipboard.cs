using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Newtonsoft.Json;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.UI.Services
{
    public static class DiagramClipboard
    {
        public const string ClipboardFormat = "HelixPythonNamespace.Nodes.v1";

        public static void SetPayload(ClipboardPayload payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            DataObject data = new DataObject();
            data.SetData(ClipboardFormat, false, json);
            data.SetData(DataFormats.UnicodeText, false, json);
            Clipboard.SetDataObject(data, true);
        }

        public static ClipboardPayload GetPayload()
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null) return null;
            if (!data.GetDataPresent(ClipboardFormat)) return null;

            string json = data.GetData(ClipboardFormat) as string;
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                return JsonConvert.DeserializeObject<ClipboardPayload>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static bool HasPayload()
        {
            IDataObject data = Clipboard.GetDataObject();
            return data != null && data.GetDataPresent(ClipboardFormat);
        }

        public static ClipboardTypeEntry BuildEntry(TypeModel type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            ClipboardTypeEntry entry = new ClipboardTypeEntry
            {
                Name = type.Name,
                Namespace = type.Namespace,
                TypeKind = type.TypeKind,
                IsStatic = type.IsStatic,
                IsSealed = type.IsSealed,
                IsPartial = type.IsPartial
            };

            foreach (MemberModel member in type.Members)
            {
                ClipboardMemberEntry m = new ClipboardMemberEntry
                {
                    Name = member.Name,
                    Kind = member.MemberKind,
                    ReturnType = member.ReturnType,
                    Access = member.AccessModifier,
                    IsAbstract = member.IsAbstract,
                    IsVirtual = member.IsVirtual,
                    IsStatic = member.IsStatic,
                    IsOverride = member.IsOverride,
                    HasGetter = member.HasGetter,
                    HasSetter = member.HasSetter,
                    Parameters = new List<ClipboardParameterEntry>()
                };

                foreach (MethodParameterModel p in member.Parameters)
                {
                    m.Parameters.Add(new ClipboardParameterEntry
                    {
                        Name = p.Name,
                        TypeName = p.TypeName,
                        IsRef = p.IsRef,
                        IsOut = p.IsOut,
                        IsParams = p.IsParams,
                        HasDefaultValue = p.HasDefaultValue,
                        DefaultValue = p.DefaultValue
                    });
                }

                entry.Members.Add(m);
            }

            foreach (TypeReference baseRef in type.BaseTypes)
            {
                entry.BaseTypeFqns.Add(new ClipboardBaseRef
                {
                    FullyQualifiedName = baseRef.FullyQualifiedName,
                    RelationshipKind = baseRef.RelationshipKind
                });
            }

            foreach (GenericParameterModel gp in type.GenericParameters)
            {
                ClipboardGenericEntry g = new ClipboardGenericEntry
                {
                    Name = gp.Name,
                    HasNewConstraint = gp.HasNewConstraint,
                    IsCovariant = gp.IsCovariant,
                    IsContravariant = gp.IsContravariant,
                    Constraints = new List<string>(gp.Constraints)
                };
                entry.GenericParameters.Add(g);
            }

            return entry;
        }
    }

    public sealed class ClipboardPayload
    {
        public string Version { get; set; }
        public string SourceSolution { get; set; }
        public List<ClipboardTypeEntry> Types { get; set; }

        public ClipboardPayload()
        {
            Version = "1";
            Types = new List<ClipboardTypeEntry>();
        }
    }

    public sealed class ClipboardTypeEntry
    {
        public string Name { get; set; }
        public string Namespace { get; set; }
        public TypeKind TypeKind { get; set; }
        public bool IsStatic { get; set; }
        public bool IsSealed { get; set; }
        public bool IsPartial { get; set; }
        public List<ClipboardMemberEntry> Members { get; set; }
        public List<ClipboardBaseRef> BaseTypeFqns { get; set; }
        public List<ClipboardGenericEntry> GenericParameters { get; set; }

        public ClipboardTypeEntry()
        {
            Members = new List<ClipboardMemberEntry>();
            BaseTypeFqns = new List<ClipboardBaseRef>();
            GenericParameters = new List<ClipboardGenericEntry>();
        }

        public string OriginalFqn
        {
            get
            {
                return string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
            }
        }
    }

    public sealed class ClipboardMemberEntry
    {
        public string Name { get; set; }
        public MemberKind Kind { get; set; }
        public string ReturnType { get; set; }
        public AccessModifier Access { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsStatic { get; set; }
        public bool IsOverride { get; set; }
        public bool HasGetter { get; set; }
        public bool HasSetter { get; set; }
        public List<ClipboardParameterEntry> Parameters { get; set; }

        public ClipboardMemberEntry()
        {
            Parameters = new List<ClipboardParameterEntry>();
        }
    }

    public sealed class ClipboardParameterEntry
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public bool IsRef { get; set; }
        public bool IsOut { get; set; }
        public bool IsParams { get; set; }
        public bool HasDefaultValue { get; set; }
        public string DefaultValue { get; set; }
    }

    public sealed class ClipboardBaseRef
    {
        public string FullyQualifiedName { get; set; }
        public RelationshipKind RelationshipKind { get; set; }
    }

    public sealed class ClipboardGenericEntry
    {
        public string Name { get; set; }
        public bool HasNewConstraint { get; set; }
        public bool IsCovariant { get; set; }
        public bool IsContravariant { get; set; }
        public List<string> Constraints { get; set; }

        public ClipboardGenericEntry()
        {
            Constraints = new List<string>();
        }
    }
}
