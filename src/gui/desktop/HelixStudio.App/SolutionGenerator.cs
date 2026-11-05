using System;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App
{
    /// <summary>
    /// Generates new Visual Studio solutions.
    /// </summary>
    public sealed class SolutionGenerator
    {
        /// <summary>
        /// Create a new solution at the specified directory.
        /// </summary>
        /// <param name="directory">Directory to create the solution in.</param>
        /// <param name="name">Name of the solution and initial project.</param>
        /// <returns>The created SolutionModel.</returns>
        public SolutionModel CreateNewSolution(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Directory cannot be empty.", "directory");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Name cannot be empty.", "name");
            }

            // Ensure directory exists
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var slnPath = Path.Combine(directory, name + ".sln");
            var srcDir = Path.Combine(directory, "src");
            var projectDir = Path.Combine(srcDir, name);
            var csprojPath = Path.Combine(projectDir, name + ".csproj");
            var programPath = Path.Combine(projectDir, "Program.cs");

            // Create directories
            Directory.CreateDirectory(projectDir);

            // Generate GUIDs
            var projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"; // C# project type
            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

            // Create .sln file
            CreateSlnFile(slnPath, name, projectTypeGuid, projectGuid);

            // Create .csproj file
            CreateCsprojFile(csprojPath, name);

            // Create Program.cs
            CreateProgramCs(programPath, name);

            // Build the in-memory model to mirror the files just written: one project
            // containing a single Program class with an empty static Main entry point.
            var solution = new SolutionModel(slnPath, name);

            var project = new ProjectModel(csprojPath, name, solution)
            {
                TargetFramework = "net47",
                DefaultNamespace = name
            };

            var programClass = new TypeModel("Program", name, TypeKind.ConcreteClass, project)
            {
                SourceFilePath = programPath
            };

            // Add Main method
            var mainMethod = new MemberModel("Main", MemberKind.Method)
            {
                ReturnType = "void",
                AccessModifier = AccessModifier.Private,
                IsStatic = true
            };
            mainMethod.Parameters.Add(new MethodParameterModel("args", "string[]"));
            programClass.Members.Add(mainMethod);

            project.Types.Add(programClass);
            solution.Projects.Add(project);

            return solution;
        }

        // Emit a minimal VS 2022-format .sln referencing the single project, with the
        // Debug/Release|Any CPU configuration matrix Visual Studio expects.
        private void CreateSlnFile(string path, string projectName, string projectTypeGuid, string projectGuid)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
            sb.AppendLine(string.Format("Project(\"{0}\") = \"{1}\", \"src\\{1}\\{1}.csproj\", \"{2}\"",
                projectTypeGuid, projectName, projectGuid));
            sb.AppendLine("EndProject");
            sb.AppendLine("Global");
            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            sb.AppendLine(string.Format("\t\t{0}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", projectGuid));
            sb.AppendLine(string.Format("\t\t{0}.Debug|Any CPU.Build.0 = Debug|Any CPU", projectGuid));
            sb.AppendLine(string.Format("\t\t{0}.Release|Any CPU.ActiveCfg = Release|Any CPU", projectGuid));
            sb.AppendLine(string.Format("\t\t{0}.Release|Any CPU.Build.0 = Release|Any CPU", projectGuid));
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
            sb.AppendLine("\t\tHideSolutionNode = FALSE");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("EndGlobal");

            File.WriteAllText(path, sb.ToString());
        }

        // Write an SDK-style .csproj pinned to net47 / C# 7.3 (matching the tool's own
        // language constraints) with implicit usings and nullable disabled.
        private void CreateCsprojFile(string path, string projectName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <OutputType>Exe</OutputType>");
            sb.AppendLine("    <TargetFramework>net47</TargetFramework>");
            sb.AppendLine("    <LangVersion>7.3</LangVersion>");
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>disable</Nullable>");
            sb.AppendLine("    <RootNamespace>" + projectName + "</RootNamespace>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("</Project>");

            File.WriteAllText(path, sb.ToString());
        }

        // Write a conventional "Hello, World!" Program.cs so the generated solution
        // compiles and runs immediately.
        private void CreateProgramCs(string path, string namespaceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace " + namespaceName);
            sb.AppendLine("{");
            sb.AppendLine("    internal class Program");
            sb.AppendLine("    {");
            sb.AppendLine("        static void Main(string[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            Console.WriteLine(\"Hello, World!\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            File.WriteAllText(path, sb.ToString());
        }
    }
}
