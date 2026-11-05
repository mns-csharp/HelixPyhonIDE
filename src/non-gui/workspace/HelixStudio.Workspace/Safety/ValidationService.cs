using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Safety
{
    /// <summary>
    /// Result of a validation operation.
    /// </summary>
    public sealed class ValidationResult
    {
        public ValidationResult()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
        }

        public bool IsValid { get { return Errors.Count == 0; } }
        public List<string> Errors { get; }
        public List<string> Warnings { get; }
    }

    /// <summary>
    /// Service for validating solution model integrity before write operations.
    /// </summary>
    public class ValidationService
    {
        /// <summary>
        /// Validate the solution model for consistency.
        /// </summary>
        public ValidationResult ValidateSolution(SolutionModel solution)
        {
            var result = new ValidationResult();

            if (solution == null)
            {
                result.Errors.Add("Solution is null.");
                return result;
            }

            // Check for duplicate type names
            var allTypes = solution.GetAllTypes().ToList();
            var duplicates = allTypes
                .GroupBy(t => t.FullyQualifiedName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            foreach (var duplicate in duplicates)
            {
                result.Errors.Add(string.Format("Duplicate type: {0}", duplicate));
            }

            // Validate each type
            foreach (var type in allTypes)
            {
                ValidateType(type, solution, result);
            }

            return result;
        }

        /// <summary>
        /// Validate a single type.
        /// </summary>
        public ValidationResult ValidateType(TypeModel type, SolutionModel solution)
        {
            var result = new ValidationResult();
            ValidateType(type, solution, result);
            return result;
        }

        private void ValidateType(TypeModel type, SolutionModel solution, ValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(type.Name))
            {
                result.Errors.Add("Type has empty name.");
                return;
            }

            // Check for valid C# identifier
            if (!IsValidIdentifier(type.Name))
            {
                result.Errors.Add(string.Format("Type name '{0}' is not a valid C# identifier.", type.Name));
            }

            // Check single inheritance rule
            ValidateSingleInheritance(type, result);

            // Check for circular inheritance
            ValidateNoCircularInheritance(type, solution, result);

            // Validate members
            foreach (var member in type.Members)
            {
                ValidateMember(member, type, result);
            }
        }

        private void ValidateSingleInheritance(TypeModel type, ValidationResult result)
        {
            if (type.TypeKind == TypeKind.Interface)
                return; // Interfaces can have multiple base interfaces

            var classBaseTypes = type.BaseTypes
                .Where(bt => bt.RelationshipKind == RelationshipKind.Inherits)
                .ToList();

            if (classBaseTypes.Count > 1)
            {
                result.Errors.Add(string.Format(
                    "Type '{0}' has multiple inheritance which is not allowed in C#.",
                    type.FullyQualifiedName));
            }
        }

        private void ValidateNoCircularInheritance(TypeModel type, SolutionModel solution, ValidationResult result)
        {
            var visited = new HashSet<string>();
            var current = type;

            while (current != null)
            {
                if (visited.Contains(current.FullyQualifiedName))
                {
                    result.Errors.Add(string.Format(
                        "Circular inheritance detected involving type '{0}'.",
                        type.FullyQualifiedName));
                    return;
                }

                visited.Add(current.FullyQualifiedName);

                // Find the base class (if any)
                var baseTypeRef = current.BaseTypes
                    .FirstOrDefault(bt => bt.RelationshipKind == RelationshipKind.Inherits);

                if (baseTypeRef == null)
                    break;

                current = baseTypeRef.Resolve(solution);
            }
        }

        private void ValidateMember(MemberModel member, TypeModel type, ValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(member.Name))
            {
                result.Errors.Add(string.Format(
                    "Type '{0}' has a member with empty name.",
                    type.FullyQualifiedName));
                return;
            }

            if (!IsValidIdentifier(member.Name))
            {
                result.Errors.Add(string.Format(
                    "Member name '{0}' in type '{1}' is not a valid C# identifier.",
                    member.Name, type.FullyQualifiedName));
            }

            // Check for duplicate members
            var duplicates = type.Members
                .Count(m => m.Name == member.Name);

            if (duplicates > 1)
            {
                result.Warnings.Add(string.Format(
                    "Type '{0}' has multiple members named '{1}'.",
                    type.FullyQualifiedName, member.Name));
            }

            // Abstract members only in abstract classes or interfaces
            if (member.IsAbstract && type.TypeKind == TypeKind.ConcreteClass)
            {
                result.Errors.Add(string.Format(
                    "Abstract member '{0}' in non-abstract class '{1}'.",
                    member.Name, type.FullyQualifiedName));
            }
        }

        private bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            // First character must be letter or underscore
            var first = name[0];
            if (!char.IsLetter(first) && first != '_')
                return false;

            // Rest can be letters, digits, or underscores
            for (int i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            }

            // Check for C# keywords
            return !IsCSharpKeyword(name);
        }

        private bool IsCSharpKeyword(string name)
        {
            var keywords = new HashSet<string>
            {
                "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
                "char", "checked", "class", "const", "continue", "decimal", "default",
                "delegate", "do", "double", "else", "enum", "event", "explicit",
                "extern", "false", "finally", "fixed", "float", "for", "foreach",
                "goto", "if", "implicit", "in", "int", "interface", "internal",
                "is", "lock", "long", "namespace", "new", "null", "object", "operator",
                "out", "override", "params", "private", "protected", "public",
                "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
                "stackalloc", "static", "string", "struct", "switch", "this", "throw",
                "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
                "ushort", "using", "virtual", "void", "volatile", "while"
            };
            return keywords.Contains(name);
        }
    }
}
