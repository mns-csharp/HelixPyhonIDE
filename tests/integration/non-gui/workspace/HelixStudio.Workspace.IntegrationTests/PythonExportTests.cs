using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Export;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonExportTests
    {
        [TestMethod]
        public void EmitsConcreteClassWithMethod()
        {
            Diagram d = new Diagram { Name = "x" };
            TypeNode node = new TypeNode("Car", TypeKind.ConcreteClass);
            MemberDefinition m = new MemberDefinition("drive", MemberKind.Method);
            m.ReturnType = "None";
            m.Parameters.Add(new MethodParameter("speed", "int"));
            node.Members.Add(m);
            d.Nodes.Add(node);

            string outDir = Path.Combine(Path.GetTempPath(), "pyex_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            new PythonExportService().ExportCSharp(d, new ExportOptions { OutputPath = outDir });
            string result = File.ReadAllText(Path.Combine(outDir, "Car.py"));
            StringAssert.Contains(result, "class Car:");
            StringAssert.Contains(result, "def drive(self, speed: int) -> None:");
        }

        [TestMethod]
        public void EmitsAbstractClass()
        {
            Diagram d = new Diagram { Name = "x" };
            TypeNode node = new TypeNode("Animal", TypeKind.AbstractClass);
            MemberDefinition m = new MemberDefinition("speak", MemberKind.Method) { IsAbstract = true, ReturnType = "str" };
            node.Members.Add(m);
            d.Nodes.Add(node);

            string outDir = Path.Combine(Path.GetTempPath(), "pyex_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            new PythonExportService().ExportCSharp(d, new ExportOptions { OutputPath = outDir });
            string result = File.ReadAllText(Path.Combine(outDir, "Animal.py"));
            StringAssert.Contains(result, "from abc import ABC, abstractmethod");
            StringAssert.Contains(result, "class Animal(ABC):");
            StringAssert.Contains(result, "@abstractmethod");
            StringAssert.Contains(result, "def speak(self) -> str:");
        }

        [TestMethod]
        public void EmitsProtocolInterface()
        {
            Diagram d = new Diagram { Name = "x" };
            TypeNode node = new TypeNode("Shape", TypeKind.Interface);
            node.Members.Add(new MemberDefinition("area", MemberKind.Method) { ReturnType = "float" });
            d.Nodes.Add(node);

            string outDir = Path.Combine(Path.GetTempPath(), "pyex_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            new PythonExportService().ExportCSharp(d, new ExportOptions { OutputPath = outDir });
            string result = File.ReadAllText(Path.Combine(outDir, "Shape.py"));
            StringAssert.Contains(result, "from typing import Protocol");
            StringAssert.Contains(result, "class Shape(Protocol):");
        }

        [TestMethod]
        public void EmitsDataclass()
        {
            Diagram d = new Diagram { Name = "x" };
            TypeNode node = new TypeNode("Point", TypeKind.Record);
            d.Nodes.Add(node);

            string outDir = Path.Combine(Path.GetTempPath(), "pyex_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            new PythonExportService().ExportCSharp(d, new ExportOptions { OutputPath = outDir });
            string result = File.ReadAllText(Path.Combine(outDir, "Point.py"));
            StringAssert.Contains(result, "from dataclasses import dataclass");
            StringAssert.Contains(result, "@dataclass");
            StringAssert.Contains(result, "class Point:");
        }

        [TestMethod]
        public void EmitsInheritanceViaRelationship()
        {
            Diagram d = new Diagram { Name = "x" };
            TypeNode animal = new TypeNode("Animal", TypeKind.ConcreteClass);
            TypeNode dog = new TypeNode("Dog", TypeKind.ConcreteClass);
            d.Nodes.Add(animal); d.Nodes.Add(dog);
            d.Relationships.Add(new Relationship(RelationshipKind.Inherits, dog, animal));

            string outDir = Path.Combine(Path.GetTempPath(), "pyex_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            new PythonExportService().ExportCSharp(d, new ExportOptions { OutputPath = outDir });
            string dogPy = File.ReadAllText(Path.Combine(outDir, "Dog.py"));
            StringAssert.Contains(dogPy, "class Dog(Animal):");
        }
    }
}
