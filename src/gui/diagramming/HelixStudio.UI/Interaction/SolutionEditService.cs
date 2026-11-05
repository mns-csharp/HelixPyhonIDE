using System;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Operations;
using HelixPythonNamespace.UI.Projection;

namespace HelixPythonNamespace.UI.Interaction
{
    /// <summary>
    /// Service that applies edit operations to the solution model and updates visuals.
    /// This bridges the operation layer with the workspace services.
    /// </summary>
    public sealed class SolutionEditService : ISolutionEditService
    {
        private readonly Func<SolutionModel> _getSolution;
        private readonly SolutionProjectionBuilder _projectionBuilder;
        private readonly Action<TypeModel> _onTypeAdded;
        private readonly Action<string> _onTypeRemoved;
        private readonly Action _invalidateCanvas;

        public SolutionEditService(
            Func<SolutionModel> getSolution,
            SolutionProjectionBuilder projectionBuilder,
            Action<TypeModel> onTypeAdded,
            Action<string> onTypeRemoved,
            Action invalidateCanvas)
        {
            _getSolution = getSolution ?? throw new ArgumentNullException("getSolution");
            _projectionBuilder = projectionBuilder ?? throw new ArgumentNullException("projectionBuilder");
            _onTypeAdded = onTypeAdded;
            _onTypeRemoved = onTypeRemoved;
            _invalidateCanvas = invalidateCanvas;
        }

        public OperationResult Apply(SolutionEditOperation operation)
        {
            if (operation == null)
            {
                return OperationResult.Fail("Operation is null");
            }

            var solution = _getSolution();
            if (solution == null)
            {
                return OperationResult.Fail("No solution loaded");
            }

            try
            {
                if (operation is AddTypeOperation addType)
                {
                    return ApplyAddType(solution, addType);
                }
                else if (operation is DeleteTypeOperation deleteType)
                {
                    return ApplyDeleteType(solution, deleteType);
                }
                else if (operation is ModifyTypeOperation modifyType)
                {
                    return ApplyModifyType(solution, modifyType);
                }
                else if (operation is AddRelationshipOperation addRel)
                {
                    return ApplyAddRelationship(solution, addRel);
                }
                else if (operation is RemoveRelationshipOperation removeRel)
                {
                    return ApplyRemoveRelationship(solution, removeRel);
                }
                else if (operation is AddMemberOperation addMember)
                {
                    return ApplyAddMember(solution, addMember);
                }
                else if (operation is RemoveMemberOperation removeMember)
                {
                    return ApplyRemoveMember(solution, removeMember);
                }
                else
                {
                    return OperationResult.Fail("Unknown operation type: " + operation.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }

        private OperationResult ApplyAddType(SolutionModel solution, AddTypeOperation op)
        {
            // Find the project
            ProjectModel project = null;
            foreach (var p in solution.Projects)
            {
                if (p.FilePath == op.ProjectPath)
                {
                    project = p;
                    break;
                }
            }

            if (project == null)
            {
                // Use first project as default
                if (solution.Projects.Count > 0)
                {
                    project = solution.Projects[0];
                }
                else
                {
                    return OperationResult.Fail("No project found");
                }
            }

            // Create the type
            var type = new TypeModel(op.TypeName, op.Namespace, op.TypeKind, project);
            if (!string.IsNullOrEmpty(op.SourceFilePath))
            {
                type.SourceFilePath = op.SourceFilePath;
            }

            project.Types.Add(type);

            // Update visual
            _projectionBuilder.AddType(type);
            _onTypeAdded?.Invoke(type);
            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }

        private OperationResult ApplyDeleteType(SolutionModel solution, DeleteTypeOperation op)
        {
            var type = solution.FindType(op.FullyQualifiedName);
            if (type == null)
            {
                return OperationResult.Fail("Type not found: " + op.FullyQualifiedName);
            }

            // Remove from project
            type.Project.Types.Remove(type);

            // Update visual
            _projectionBuilder.RemoveType(op.FullyQualifiedName);
            _onTypeRemoved?.Invoke(op.FullyQualifiedName);
            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }

        private OperationResult ApplyModifyType(SolutionModel solution, ModifyTypeOperation op)
        {
            var type = solution.FindType(op.FullyQualifiedName);
            if (type == null)
            {
                return OperationResult.Fail("Type not found: " + op.FullyQualifiedName);
            }

            var oldFQN = type.FullyQualifiedName;

            if (!string.IsNullOrEmpty(op.NewName))
            {
                type.Name = op.NewName;
            }

            if (!string.IsNullOrEmpty(op.NewNamespace))
            {
                type.Namespace = op.NewNamespace;
            }

            if (op.NewTypeKind.HasValue)
            {
                type.TypeKind = op.NewTypeKind.Value;
            }

            var newFQN = type.FullyQualifiedName;

            // Update visual
            if (oldFQN != newFQN)
            {
                _projectionBuilder.RenameType(oldFQN, newFQN, type);
            }
            else
            {
                _projectionBuilder.UpdateType(type);
            }

            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }

        private OperationResult ApplyAddRelationship(SolutionModel solution, AddRelationshipOperation op)
        {
            var sourceType = solution.FindType(op.SourceFullyQualifiedName);
            var targetType = solution.FindType(op.TargetFullyQualifiedName);

            if (sourceType == null)
            {
                return OperationResult.Fail("Source type not found: " + op.SourceFullyQualifiedName);
            }

            if (targetType == null)
            {
                return OperationResult.Fail("Target type not found: " + op.TargetFullyQualifiedName);
            }

            // Add the base type reference
            var typeRef = new TypeReference(op.TargetFullyQualifiedName, op.RelationshipKind);
            typeRef.Bind(solution);
            sourceType.BaseTypes.Add(typeRef);

            // Rebuild visuals (relationships changed)
            _projectionBuilder.RebuildFromSolution(solution);
            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }

        private OperationResult ApplyRemoveRelationship(SolutionModel solution, RemoveRelationshipOperation op)
        {
            var sourceType = solution.FindType(op.SourceFullyQualifiedName);

            if (sourceType == null)
            {
                return OperationResult.Fail("Source type not found: " + op.SourceFullyQualifiedName);
            }

            // Remove the base type reference
            sourceType.BaseTypes.RemoveAll(bt =>
                bt.FullyQualifiedName == op.TargetFullyQualifiedName &&
                bt.RelationshipKind == op.RelationshipKind);

            // Rebuild visuals (relationships changed)
            _projectionBuilder.RebuildFromSolution(solution);
            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }

        private OperationResult ApplyAddMember(SolutionModel solution, AddMemberOperation op)
        {
            var type = solution.FindType(op.TypeFullyQualifiedName);
            if (type == null)
            {
                return OperationResult.Fail("Type not found: " + op.TypeFullyQualifiedName);
            }

            var member = new MemberModel(op.MemberName, op.MemberKind);
            member.ReturnType = op.ReturnType;
            member.AccessModifier = op.AccessModifier;
            member.IsAbstract = op.IsAbstract;
            member.IsVirtual = op.IsVirtual;
            member.IsStatic = op.IsStatic;

            type.Members.Add(member);

            // Update visual
            _projectionBuilder.UpdateType(type);
            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }

        private OperationResult ApplyRemoveMember(SolutionModel solution, RemoveMemberOperation op)
        {
            var type = solution.FindType(op.TypeFullyQualifiedName);
            if (type == null)
            {
                return OperationResult.Fail("Type not found: " + op.TypeFullyQualifiedName);
            }

            type.Members.RemoveAll(m => m.Name == op.MemberName);

            // Update visual
            _projectionBuilder.UpdateType(type);
            _invalidateCanvas?.Invoke();

            return OperationResult.Ok();
        }
    }
}
