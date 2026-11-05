using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>
    /// The template registry (SRS §1, §10): the set of available <see cref="ProjectTemplate"/>s. Ships a
    /// built-in catalog and is open to user/org/plugin templates (added via <see cref="Add"/> — the wizard
    /// never needs code changes for a new template). Provides the corpus the search/filter layer runs over.
    /// </summary>
    public sealed class TemplateCatalog
    {
        private readonly List<ProjectTemplate> _templates = new List<ProjectTemplate>();

        public IReadOnlyList<ProjectTemplate> Templates { get { return _templates; } }
        public int Count { get { return _templates.Count; } }

        public void Add(ProjectTemplate t) { if (t != null) _templates.Add(t); }

        /// <summary>
        /// The languages this Helix product can actually create, open and build. New Project must
        /// never offer a scaffold the IDE would then refuse to open — in the combined build the
        /// catalog covered all seven languages, and after the product split the other five would
        /// have generated dead projects here. The built-in catalog no longer DEFINES those five,
        /// so this filter now exists for discovered (user/org/plugin) templates, which can declare
        /// any language they like.
        /// </summary>
        /// <remarks>These must match the catalog's own <c>Language</c> strings exactly: the GPU
        /// entries are labelled "CUDA C", not "CUDA", and getting that wrong silently drops every
        /// CUDA template — which a Python solution is explicitly allowed to add (TemplateEligibility).
        /// </remarks>
        private static readonly string[] ShippedLanguages = { "Python", "C++", "C", "CUDA C", "CUDA" };

        /// <summary>Drops every template whose language this product does not ship.</summary>
        public void RetainShippedLanguagesOnly()
        {
            _templates.RemoveAll(t =>
            {
                if (t == null || string.IsNullOrEmpty(t.Language)) return false;
                foreach (string lang in ShippedLanguages)
                    if (string.Equals(t.Language, lang, System.StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            });
        }

        /// <summary>Discover and add on-disk templates under <paramref name="root"/> (SRS §10). Returns any
        /// per-manifest load errors (never throws) so a bad template folder can't break the catalog.</summary>
        public List<string> DiscoverFrom(string root, string installedBy = "user")
        {
            var loader = new TemplateLoader();
            foreach (var t in loader.LoadFrom(root, installedBy)) Add(t);
            return loader.Errors;
        }

        /// <summary>The catalog the wizard uses: built-in templates plus any discovered under the standard
        /// user (%APPDATA%\HelixStudio\templates) and org (%PROGRAMDATA%\HelixStudio\templates) roots.</summary>
        public static TemplateCatalog Default()
        {
            var c = BuiltIn();
            try { c.DiscoverFrom(UserTemplatesDir(), "user"); } catch { }
            try { c.DiscoverFrom(OrgTemplatesDir(), "org"); } catch { }
            c.RetainShippedLanguagesOnly();
            return c;
        }

        public static string UserTemplatesDir()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HelixStudio", "templates"); } catch { return null; }
        }
        public static string OrgTemplatesDir()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HelixStudio", "templates"); } catch { return null; }
        }
        public ProjectTemplate ById(string id)
        {
            foreach (var t in _templates) if (t.Id == id) return t;
            return null;
        }

        /// <summary>Distinct values for a facet (Language/Platform/ProjectType/Framework/Category) for the filter UI.</summary>
        public List<string> Facet(string name)
        {
            var seen = new List<string>();
            foreach (var t in _templates)
            {
                string v = FacetValue(t, name);
                if (!string.IsNullOrEmpty(v) && !seen.Contains(v)) seen.Add(v);
            }
            seen.Sort(System.StringComparer.OrdinalIgnoreCase);
            return seen;
        }

        internal static string FacetValue(ProjectTemplate t, string facet)
        {
            switch (facet)
            {
                case "Language": return t.Language;
                case "Platform": return t.Platform;
                case "ProjectType": return t.ProjectType;
                case "Framework": return t.Framework;
                case "Category": return t.Category;
                default: return null;
            }
        }

        /// <summary>The default catalog: one console + one library template per shipped language.</summary>
        public static TemplateCatalog BuiltIn()
        {
            var c = new TemplateCatalog();

            c.Add(Console("py.console", "Python Console App", "Python", "CPython", "🐍",
                "A minimal Python console application with a main entry point.",
                new TemplateFile { RelativePath = "{{ProjectName}}/main.py",
                    Content = "def main():\n    print(\"Hello from {{ProjectName}}\")\n\n\nif __name__ == \"__main__\":\n    main()\n" },
                // [tool.helixstudio] kind is REQUIRED: ProjectKinds.FromToken silently defaults a
                // missing kind to ClassLibrary, so without it the created app LOADS AS A LIBRARY
                // (not runnable). startup = true makes the sole project the startup immediately.
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\n\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n" }));

            var notebook = PyKind("py.notebook", "Jupyter Notebook Solution", "📓", "Notebook",
                "A Python solution containing a new Jupyter notebook with one empty code cell.",
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\n\n[tool.helixstudio]\nkind = \"library\"\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/{{ProjectName}}.ipynb",
                    Content = "{\n  \"cells\": [\n    {\n      \"cell_type\": \"code\",\n      \"execution_count\": null,\n      \"id\": \"new-cell\",\n      \"metadata\": {},\n      \"outputs\": [],\n      \"source\": []\n    }\n  ],\n  \"metadata\": {\n    \"kernelspec\": {\n      \"display_name\": \"Python 3\",\n      \"language\": \"python\",\n      \"name\": \"python3\"\n    },\n    \"language_info\": {\n      \"name\": \"python\",\n      \"version\": \"{{PythonVersion}}\"\n    }\n  },\n  \"nbformat\": 4,\n  \"nbformat_minor\": 5\n}\n" });
            notebook.Tags.AddRange(new[] { "jupyter", "notebook", "ipynb", "data science" });
            c.Add(notebook);
            c.ById("py.notebook").ConfigPages.Add(PythonPage());

            // ---- the worked example -------------------------------------------------------
            //
            // A one-click path to something real: it downloads a Protein Data Bank entry, measures
            // it, and writes both a figure and the CSV behind that figure. The CSV is not a nicety
            // — a figure a reader cannot check is a picture, and this is the template a reviewer
            // opens first.
            //
            // The generated code is object-oriented because PY8002 requires it: module-level `def`
            // is refused everywhere except the PSM-mandated `def main()`. A template that generated
            // free functions would produce a project the IDE then refuses to run, which is the
            // worst possible first impression.
            var ensemble = PyKind("py.structure-ensemble", "Analyse a Structure Ensemble", "\U0001F9EC",
                "Console",
                "Downloads a PDB entry, measures its geometry, and writes a figure with the CSV behind it.",
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\ndependencies = [\"matplotlib\"]\n\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/main.py",
                    Content = "\"\"\"Analyse a structure ensemble - press Run.\n\nDownloads a Protein Data Bank entry, measures it, writes a figure and the CSV\nbehind that figure. Everything it produces lands beside this file.\n\nThe same structure can be opened in the viewer (Analysis > Protein Structure >\nOpen from PDB or UniProt ID) and queried there; REPRODUCE.md lists the command\nlines that regenerate these outputs without the IDE.\n\"\"\"\n\nimport os\n\nfrom {{ProjectName}}.analysis import EnsembleAnalysis\nfrom {{ProjectName}}.plot import DistancePlot\nfrom {{ProjectName}}.structure import Structure, StructureSource\n\nACCESSION = \"1UBQ\"\n\n\ndef main():\n    here = os.path.dirname(os.path.abspath(__file__))\n    source = StructureSource(ACCESSION, here)\n    path = source.ensure()\n\n    structure = Structure.read(path)\n    analysis = EnsembleAnalysis(structure)\n\n    print(\"%s: %d atoms, %d chains\" % (ACCESSION, len(structure.atoms), len(structure.chains())))\n    for line in analysis.summary():\n        print(\"  \" + line)\n\n    distances = analysis.residue_distances()\n    if not distances:\n        print(\"no alpha-carbons: nothing to plot\")\n        return 0\n\n    plot = DistancePlot(distances, \"%s - residue distance from the chain centre\" % ACCESSION)\n    print(\"  data:   \" + plot.write_csv(os.path.join(here, \"residue_distance.csv\")))\n    figure = plot.write(os.path.join(here, \"residue_distance.png\"))\n    if figure:\n        print(\"  figure: \" + os.path.abspath(figure))\n    return 0\n\n\nif __name__ == \"__main__\":\n    main()\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/{{ProjectName}}/__init__.py",
                    Content = "from .analysis import EnsembleAnalysis, ResidueGeometry\nfrom .plot import DistancePlot\nfrom .structure import Atom, Structure, StructureSource\n\n__all__ = [\n    \"Atom\",\n    \"DistancePlot\",\n    \"EnsembleAnalysis\",\n    \"ResidueGeometry\",\n    \"Structure\",\n    \"StructureSource\",\n]\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/{{ProjectName}}/structure.py",
                    Content = "\"\"\"Fetching and reading the structure this example analyses.\"\"\"\n\nimport os\nimport urllib.request\n\n\nclass StructureSource:\n    \"\"\"Where the structure comes from.\n\n    Downloads it once, from the same place the IDE's \"Open by ID...\" does, and\n    keeps it beside this file. Re-running is then offline: an example that needs\n    the network every time is an example that fails during a talk.\n    \"\"\"\n\n    RCSB = \"https://files.rcsb.org/download/\"\n\n    def __init__(self, accession, directory=None):\n        self.accession = accession.upper()\n        self.directory = directory or os.path.dirname(os.path.abspath(__file__))\n\n    @property\n    def path(self):\n        return os.path.join(self.directory, self.accession + \".pdb\")\n\n    def ensure(self):\n        \"\"\"The local path, downloading the entry if it is not already here.\"\"\"\n        if os.path.exists(self.path):\n            return self.path\n\n        url = self.RCSB + self.accession + \".pdb\"\n        print(\"downloading \" + url)\n        with urllib.request.urlopen(url, timeout=30) as response:\n            body = response.read()\n\n        # Written only after it is known to be a structure. A truncated or\n        # error-page download cached under this name would fail later, somewhere\n        # that looks unrelated.\n        if b\"ATOM  \" not in body and b\"HETATM\" not in body:\n            raise ValueError(url + \" did not return a PDB structure\")\n\n        with open(self.path, \"wb\") as handle:\n            handle.write(body)\n        return self.path\n\n\nclass Atom:\n    \"\"\"One ATOM/HETATM record, only the fields this example uses.\"\"\"\n\n    def __init__(self, name, res_name, res_seq, chain, element, x, y, z):\n        self.name = name\n        self.res_name = res_name\n        self.res_seq = res_seq\n        self.chain = chain\n        self.element = element\n        self.x = x\n        self.y = y\n        self.z = z\n\n\nclass Structure:\n    \"\"\"A parsed PDB file, one model.\n\n    Deliberately a small reader rather than a dependency: the point of this\n    example is the analysis, and a reader that fits on a page is one a reviewer\n    can check. For real work, open the file in HelixPython and query it there.\n    \"\"\"\n\n    def __init__(self, atoms):\n        self.atoms = atoms\n\n    @classmethod\n    def read(cls, path):\n        atoms = []\n        with open(path, \"r\", encoding=\"ascii\", errors=\"replace\") as handle:\n            for line in handle:\n                if line.startswith(\"ENDMDL\"):\n                    break\n                if not line.startswith((\"ATOM  \", \"HETATM\")):\n                    continue\n                atoms.append(Atom(\n                    line[12:16].strip(),\n                    line[17:20].strip(),\n                    int(line[22:26]),\n                    line[21].strip(),\n                    line[76:78].strip(),\n                    float(line[30:38]),\n                    float(line[38:46]),\n                    float(line[46:54]),\n                ))\n        return cls(atoms)\n\n    def backbone(self):\n        return [a for a in self.atoms if a.name == \"CA\"]\n\n    def chains(self):\n        seen = []\n        for atom in self.atoms:\n            if atom.chain not in seen:\n                seen.append(atom.chain)\n        return seen\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/{{ProjectName}}/analysis.py",
                    Content = "\"\"\"The analysis: per-residue geometry, summarised per chain.\"\"\"\n\nimport math\n\n\nclass ResidueGeometry:\n    \"\"\"One residue's distance from its chain's centre of mass.\n\n    The simplest quantity that is still a real measurement: it needs every\n    alpha-carbon, it changes with conformation, and a plot of it against residue\n    number is recognisably a protein rather than a curve.\n    \"\"\"\n\n    def __init__(self, chain, res_seq, res_name, distance):\n        self.chain = chain\n        self.res_seq = res_seq\n        self.res_name = res_name\n        self.distance = distance\n\n\nclass EnsembleAnalysis:\n    \"\"\"Reads a structure and reports what it found.\n\n    Every number here is derived from the coordinates in the file. Nothing is\n    defaulted: a chain with no alpha-carbons is reported as having none, rather\n    than silently contributing a zero to an average.\n    \"\"\"\n\n    def __init__(self, structure):\n        self.structure = structure\n\n    def radius_of_gyration(self, chain=None):\n        \"\"\"Mass-free radius of gyration over alpha-carbons, in angstrom.\"\"\"\n        points = self._alpha_carbons(chain)\n        if not points:\n            return None\n\n        cx, cy, cz = self._centre(points)\n        total = sum((a.x - cx) ** 2 + (a.y - cy) ** 2 + (a.z - cz) ** 2 for a in points)\n        return math.sqrt(total / len(points))\n\n    def residue_distances(self, chain=None):\n        \"\"\"Each residue's distance from the centre, in file order.\"\"\"\n        points = self._alpha_carbons(chain)\n        if not points:\n            return []\n\n        cx, cy, cz = self._centre(points)\n        return [\n            ResidueGeometry(a.chain, a.res_seq, a.res_name,\n                            math.sqrt((a.x - cx) ** 2 + (a.y - cy) ** 2 + (a.z - cz) ** 2))\n            for a in points\n        ]\n\n    def summary(self):\n        \"\"\"One line per chain: residues, and the radius of gyration over them.\"\"\"\n        lines = []\n        for chain in self.structure.chains():\n            residues = self._alpha_carbons(chain)\n            if not residues:\n                lines.append(\"chain %s: no alpha-carbons - not a polymer\" % (chain or \"-\"))\n                continue\n            lines.append(\"chain %s: %d residues, Rg %.2f A\"\n                         % (chain or \"-\", len(residues), self.radius_of_gyration(chain)))\n        return lines\n\n    def _alpha_carbons(self, chain):\n        points = self.structure.backbone()\n        if chain is None:\n            return points\n        return [a for a in points if a.chain == chain]\n\n    @staticmethod\n    def _centre(points):\n        n = float(len(points))\n        return (sum(a.x for a in points) / n,\n                sum(a.y for a in points) / n,\n                sum(a.z for a in points) / n)\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/{{ProjectName}}/plot.py",
                    Content = "\"\"\"Writing the figure.\"\"\"\n\nimport os\n\n\nclass DistancePlot:\n    \"\"\"Residue number against distance from the chain centre.\n\n    matplotlib is a declared dependency, but its absence is reported rather than\n    raised: the analysis above is the result, and a missing plotting library\n    should not make it look as though the analysis failed.\n    \"\"\"\n\n    def __init__(self, distances, title):\n        self.distances = distances\n        self.title = title\n\n    def write(self, path):\n        \"\"\"Write the figure, returning the path, or None with a reason printed.\"\"\"\n        try:\n            import matplotlib\n            matplotlib.use(\"Agg\")\n            import matplotlib.pyplot as plt\n        except ImportError:\n            print(\"matplotlib is not installed - skipping the figure \"\n                  \"(pip install matplotlib), the CSV beside it is the data\")\n            return None\n\n        figure, axes = plt.subplots(figsize=(7, 3.5))\n        axes.plot([d.res_seq for d in self.distances],\n                  [d.distance for d in self.distances], linewidth=1.4)\n        axes.set_xlabel(\"residue\")\n        axes.set_ylabel(\"distance from centre (A)\")\n        axes.set_title(self.title)\n        figure.tight_layout()\n        figure.savefig(path, dpi=150)\n        plt.close(figure)\n        return path\n\n    def write_csv(self, path):\n        \"\"\"The same numbers as CSV, so the figure is checkable rather than trusted.\"\"\"\n        with open(path, \"w\", encoding=\"utf-8\", newline=\"\") as handle:\n            handle.write(\"chain,resi,resn,distance_angstrom\\n\")\n            for d in self.distances:\n                handle.write(\"%s,%d,%s,%.4f\\n\" % (d.chain, d.res_seq, d.res_name, d.distance))\n        return os.path.abspath(path)\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/REPRODUCE.md",
                    Content = "# Reproducing these outputs\n\nTwo ways, and they should agree.\n\n## From the IDE\n\nPress Run. It downloads {{ProjectName}}'s structure, prints a per-chain summary,\nand writes `residue_distance.csv` and `residue_distance.png` beside `main.py`.\nEvery run also writes `environment.lock.toml` next to `pyproject.toml`: the\ninterpreter, the resolved package versions and the machine's toolchains, so a\nresult can be attributed to the environment that produced it.\n\n## From a command line\n\nThe same structure, measured by the IDE's own engines rather than by this\nproject's Python:\n\n    helix_cli dssp 1UBQ.pdb\n    helix_cli query 1UBQ.pdb --sql=\"select count(*) from atoms group by ss\"\n    helix_cli query 1UBQ.pdb --sql=\"select avg(beta) from atoms where ss='H'\"\n\nOver a trajectory, one answer per frame:\n\n    helix_cli query traj.dcd --topology=1UBQ.pdb --sql=\"select avg(rg) from atoms\" --frames\n\nEach command prints CSV carrying the query that produced it, so a figure built\nfrom one can be traced back to the question it answers.\n" });
            ensemble.Tags.AddRange(new[] { "structural biology", "pdb", "analysis", "matplotlib", "example" });
            c.Add(ensemble);
            c.ById("py.structure-ensemble").ConfigPages.Add(PythonPage());

            c.Add(Console("cuda.console", "CUDA Console App", "CUDA C", "CUDA Runtime", "⚡",
                "A CUDA program with a vector-add kernel and a host launcher.",
                new TemplateFile { RelativePath = "{{ProjectName}}/main.cu",
                    Content = "#include <cstdio>\n\n__global__ void addVec(const float* a, const float* b, float* c, int n) {\n    int i = blockIdx.x * blockDim.x + threadIdx.x;\n    if (i < n) c[i] = a[i] + b[i];\n}\n\nint main() {\n    printf(\"{{ProjectName}}: CUDA kernel ready\\n\");\n    return 0;\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CUDA CXX)\nadd_executable({{ProjectName}} main.cu)\nset_target_properties({{ProjectName}} PROPERTIES CUDA_ARCHITECTURES {{CudaArch}})\n" }));
            c.ById("cuda.console").Platform = "GPU";


            // ---- native C++/CUDA app + library templates (CMake-driven). These also back the
            // per-language "Add New Project" eligibility sets (TemplateEligibility): a Python
            // solution may add a CUDA library, a CUDA/C++ solution may add either language's
            // app or library. ----

            c.Add(Console("cpp.console", "C++ Console App", "C++", "CMake", "🟪",
                "A C++ console application built with CMake.",
                new TemplateFile { RelativePath = "{{ProjectName}}/main.cpp",
                    Content = "#include <iostream>\n\nint main() {\n    std::cout << \"Hello from {{ProjectName}}\\n\";\n    return 0;\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD {{CxxStandard}})\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\nadd_executable({{ProjectName}} main.cpp)\n" }));

            var cppLib = Console("cpp.classlib", "C++ Library (Static)", "C++", "CMake", "📦",
                "A C++ static library built with CMake.",
                new TemplateFile { RelativePath = "{{ProjectName}}/lib.h",
                    Content = "#pragma once\n\nclass Greeter {\npublic:\n    const char* greet() const;\n};\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/lib.cpp",
                    Content = "#include \"lib.h\"\n\nconst char* Greeter::greet() const {\n    return \"Hello from {{ProjectName}}\";\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD {{CxxStandard}})\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\nadd_library({{ProjectName}} STATIC lib.cpp lib.h)\ntarget_include_directories({{ProjectName}} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})\n" });
            cppLib.ProjectType = "Library";
            cppLib.Tags.Add("library");
            c.Add(cppLib);

            var cudaLibT = Console("cuda.classlib", "CUDA Library (Static)", "CUDA C", "CUDA Runtime", "⚙",
                "A CUDA static library (kernels + C-linkage launchers) built with CMake.",
                new TemplateFile { RelativePath = "{{ProjectName}}/kernels.cu",
                    Content = "#include <cuda_runtime.h>\n\nextern \"C\" __global__ void scale_kernel(float* data, float factor, int n) {\n    int i = blockIdx.x * blockDim.x + threadIdx.x;\n    if (i < n) data[i] *= factor;\n}\n\nextern \"C\" cudaError_t launch_scale(float* data, float factor, int n, cudaStream_t stream) {\n    dim3 block(256);\n    dim3 grid((n + block.x - 1) / block.x);\n    scale_kernel<<<grid, block, 0, stream>>>(data, factor, n);\n    return cudaGetLastError();\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CUDA CXX)\nadd_library({{ProjectName}} STATIC kernels.cu)\nset_target_properties({{ProjectName}} PROPERTIES CUDA_ARCHITECTURES {{CudaArch}})\n" });
            cudaLibT.ProjectType = "Library";
            cudaLibT.Platform = "GPU";
            cudaLibT.Tags.Add("library");
            c.Add(cudaLibT);

            // A scientific-simulation flavour to exercise the ProjectType/Category facets and search.
            var sim = Console("cuda.mdsim", "CUDA Molecular Dynamics", "CUDA C", "CUDA Runtime", "🧬",
                "A GPU molecular-dynamics simulation scaffold (integrator + kernel stubs).",
                new TemplateFile { RelativePath = "{{ProjectName}}/simulate.cu",
                    Content = "// {{ProjectName}} -- GPU MD simulation scaffold\n__global__ void integrate(float* pos, float* vel, float dt, int n) {\n    int i = blockIdx.x * blockDim.x + threadIdx.x;\n    if (i < n) pos[i] += vel[i] * dt;\n}\n\nint main() {\n    return 0;  // host driver stub -- launch integrate() from here\n}\n" },
                // Without a CMakeLists the created project could not be opened as a native
                // workspace at all (the wizard finished and the IDE showed nothing).
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CUDA CXX)\nadd_executable({{ProjectName}} simulate.cu)\nset_target_properties({{ProjectName}} PROPERTIES CUDA_ARCHITECTURES {{CudaArch}})\n" });
            sim.ProjectType = "Molecular Simulation"; sim.Platform = "GPU"; sim.Category = "Samples";
            sim.Tags.AddRange(new[] { "science", "md", "dynamics", "gpu" });
            c.Add(sim);

            // ---- Python project kinds beyond Console, bringing the wizard to parity with the legacy
            // "New Solution…" dialog it will replace (blank / class library / GUI app / Flask web app).
            // Each writes pyproject.toml at {{ProjectName}}/pyproject.toml so the wizard opens the
            // generated project on the canvas, and gets the same Python config page as py.console. ----

            var blank = PyKind("py.blank", "Blank Python Solution", "📄", "Empty",
                "An empty Python project — just a pyproject.toml, ready to design classes on the canvas.",
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\n\n[tool.helixstudio]\nkind = \"library\"\n" });
            blank.Tags.AddRange(new[] { "blank", "empty", "solution" });
            c.Add(blank);
            c.ById("py.blank").ConfigPages.Add(PythonPage());

            var lib = PyKind("py.classlib", "Python Class Library", "📚", "Library",
                "A reusable Python package (importable module) with no entry point.",
                // Keep the package directly under the project directory. TypeService still
                // recognises legacy src-layout projects, but new projects must not acquire an
                // extra organisational folder that the user did not request.
                new TemplateFile { RelativePath = "{{ProjectName}}/{{ProjectName}}/__init__.py",
                    Content = "\"\"\"{{ProjectName}} package.\"\"\"\n\n__version__ = \"0.1.0\"\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\n\n[build-system]\nrequires = [\"setuptools>=61\"]\nbuild-backend = \"setuptools.build_meta\"\n\n[tool.helixstudio]\nkind = \"library\"\n" });
            lib.Tags.AddRange(new[] { "library", "package", "module" });
            c.Add(lib);
            c.ById("py.classlib").ConfigPages.Add(PythonPage());

            var gui = PyKind("py.gui.tk", "Python GUI App (Tkinter)", "🖼", "Desktop",
                "A Tkinter desktop application with a main window.",
                // GuiApp is subject to the main-module rules (ProjectKinds.RequiresMainModule):
                // the entry must be the canonical def main() + __main__ guard shape.
                new TemplateFile { RelativePath = "{{ProjectName}}/main.py",
                    Content = "import tkinter as tk\n\n\nclass App(tk.Tk):\n    def __init__(self):\n        super().__init__()\n        self.title(\"{{ProjectName}}\")\n        self.geometry(\"480x320\")\n        tk.Label(self, text=\"Hello from {{ProjectName}}\").pack(expand=True)\n\n\ndef main():\n    App().mainloop()\n\n\nif __name__ == \"__main__\":\n    main()\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\n\n[tool.helixstudio]\nkind = \"gui\"\nstartup = true\n" });
            gui.Tags.AddRange(new[] { "gui", "desktop", "tkinter" });
            c.Add(gui);
            c.ById("py.gui.tk").ConfigPages.Add(PythonPage());

            var flask = PyKind("py.flask", "Python Flask Web App", "🌐", "Web",
                "A minimal Flask web application with a single route.",
                new TemplateFile { RelativePath = "{{ProjectName}}/app.py",
                    Content = "from flask import Flask\n\napp = Flask(__name__)\n\n\n@app.route(\"/\")\ndef index():\n    return \"Hello from {{ProjectName}}\"\n\n\nif __name__ == \"__main__\":\n    app.run(debug=True)\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\ndependencies = [\"flask>=3.0\"]\n\n[tool.helixstudio]\nkind = \"flask\"\nstartup = true\n" });
            flask.Tags.AddRange(new[] { "flask", "web", "server" });
            c.Add(flask);
            c.ById("py.flask").ConfigPages.Add(PythonPage());

            // ---- Dynamic (shared) native libraries — the DLL siblings of the (Static) ones. ----

            var cppDll = Console("cpp.dll", "C++ Dynamic Link Library", "C++", "CMake", "🔗",
                "A C++ shared library (DLL/.so) built with CMake, with an export macro.",
                new TemplateFile { RelativePath = "{{ProjectName}}/lib.h",
                    Content = "#pragma once\n\n#if defined(_WIN32)\n#  define {{ProjectName}}_API __declspec(dllexport)\n#else\n#  define {{ProjectName}}_API\n#endif\n\nextern \"C\" {{ProjectName}}_API int answer();\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/lib.cpp",
                    Content = "#include \"lib.h\"\n\nextern \"C\" int answer() {\n    return 42;\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD {{CxxStandard}})\nadd_library({{ProjectName}} SHARED lib.cpp lib.h)\ntarget_include_directories({{ProjectName}} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})\n" });
            cppDll.ProjectType = "DLL";
            cppDll.Tags.AddRange(new[] { "dll", "shared", "library" });
            c.Add(cppDll);

            var cudaDll = Console("cuda.dll", "CUDA Dynamic Link Library", "CUDA C", "CUDA Runtime", "🔗",
                "A CUDA shared library (kernels + exported C-linkage launchers) built with CMake.",
                new TemplateFile { RelativePath = "{{ProjectName}}/kernels.cu",
                    Content = "#include <cuda_runtime.h>\n\n#if defined(_WIN32)\n#  define {{ProjectName}}_API __declspec(dllexport)\n#else\n#  define {{ProjectName}}_API\n#endif\n\nextern \"C\" __global__ void scale_kernel(float* data, float factor, int n) {\n    int i = blockIdx.x * blockDim.x + threadIdx.x;\n    if (i < n) data[i] *= factor;\n}\n\nextern \"C\" {{ProjectName}}_API cudaError_t launch_scale(float* data, float factor, int n, cudaStream_t stream) {\n    dim3 block(256);\n    dim3 grid((n + block.x - 1) / block.x);\n    scale_kernel<<<grid, block, 0, stream>>>(data, factor, n);\n    return cudaGetLastError();\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CUDA CXX)\nadd_library({{ProjectName}} SHARED kernels.cu)\nset_target_properties({{ProjectName}} PROPERTIES CUDA_ARCHITECTURES {{CudaArch}})\n" });
            cudaDll.ProjectType = "DLL";
            cudaDll.Platform = "GPU";
            cudaDll.Tags.AddRange(new[] { "dll", "shared", "library" });
            c.Add(cudaDll);

            // ---- C++ / Python test libraries ----

            var cppTest = Console("cpp.test", "C++ Unit Test Library (GTest)", "C++", "CMake", "🧪",
                "A GoogleTest unit-test project (GTest fetched via CMake FetchContent).",
                // test_main.cpp (not tests.cpp): the language descriptor's test-file convention
                // is test_*/ *_test — the name is what makes the tree label it "[Unit Test]".
                new TemplateFile { RelativePath = "{{ProjectName}}/test_main.cpp",
                    Content = "#include <gtest/gtest.h>\n\nTEST({{ProjectName}}, Addition) {\n    EXPECT_EQ(1 + 1, 2);\n}\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/CMakeLists.txt",
                    Content = "cmake_minimum_required(VERSION 3.18)\nproject({{ProjectName}} LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD {{CxxStandard}})\ninclude(FetchContent)\nFetchContent_Declare(googletest\n  URL https://github.com/google/googletest/archive/refs/tags/v1.14.0.zip\n  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)\nset(gtest_force_shared_crt ON CACHE BOOL \"\" FORCE)\nFetchContent_MakeAvailable(googletest)\nenable_testing()\nadd_executable({{ProjectName}} test_main.cpp)\ntarget_link_libraries({{ProjectName}} GTest::gtest_main)\ninclude(GoogleTest)\ngtest_discover_tests({{ProjectName}})\n" });
            cppTest.ProjectType = "Test";
            cppTest.Tags.AddRange(new[] { "test", "gtest" });
            c.Add(cppTest);

            // kind = "test", NOT "library": solution policy rule 3 (PY8003) allows test files in a
            // test project and nowhere else, so a test template that still declared itself a
            // library would generate a project blocked from compiling the instant it was created.
            var pyTest = PyKind("py.test", "Python Unit Test Library (pytest)", "🧪", "Test",
                "A pytest test project — a sibling test package that tests other members (PSM test-package rule).",
                new TemplateFile { RelativePath = "{{ProjectName}}/test_basic.py",
                    Content = "class TestBasic:\n    def test_addition(self):\n        assert 1 + 1 == 2\n" },
                new TemplateFile { RelativePath = "{{ProjectName}}/pyproject.toml",
                    Content = "[project]\nname = \"{{ProjectName}}\"\nversion = \"0.1.0\"\ndescription = \"{{Description}}\"\nrequires-python = \">={{PythonVersion}}\"\n\n[tool.helixstudio]\nkind = \"test\"\n" });
            pyTest.Tags.AddRange(new[] { "test", "pytest", "unittest" });
            c.Add(pyTest);
            c.ById("py.test").ConfigPages.Add(PythonPage());

            // ---- Per-template configuration pages (SRS §6). Field values become {{Name}} variables. ----
            var pyPage = new TemplateConfigPage { Title = "Python" };
            pyPage.Fields.Add(Choice("PythonVersion", "Python version", "3.11", "3.11", "3.12", "3.13"));
            pyPage.Fields.Add(Boolean("UseVenv", "Create virtual environment", true));
            c.ById("py.console").ConfigPages.Add(pyPage);

            foreach (string cppId in new[] { "cpp.console", "cpp.classlib", "cpp.dll", "cpp.test" })
            {
                var cxx = new TemplateConfigPage { Title = "C++" };
                cxx.Fields.Add(Choice("CxxStandard", "C++ standard", "20", "14", "17", "20", "23"));
                c.ById(cppId).ConfigPages.Add(cxx);
            }

            foreach (string cudaId in new[] { "cuda.console", "cuda.mdsim", "cuda.classlib", "cuda.dll" })
            {
                var cu = new TemplateConfigPage { Title = "CUDA" };
                cu.Fields.Add(Choice("CudaArch", "GPU architecture (compute capability)", "86", "70", "75", "80", "86", "89", "90"));
                cu.Fields.Add(Boolean("UseCublas", "Use cuBLAS", false));
                cu.Fields.Add(Boolean("UseCufft", "Use cuFFT", false));
                cu.Fields.Add(Boolean("UseCurand", "Use cuRAND", false));
                c.ById(cudaId).ConfigPages.Add(cu);
            }

            c.RetainShippedLanguagesOnly();
            return c;
        }

        private static ProjectTemplate Console(string id, string name, string lang, string framework, string icon, string desc, params TemplateFile[] files)
        {
            var t = new ProjectTemplate
            {
                Id = id, Name = name, Description = desc, Language = lang, Framework = framework, Icon = icon,
                Platform = "Cross-platform", ProjectType = "Console", Category = "Official", Author = "HelixStudio", Version = "1.0.0",
            };
            t.Tags.Add(lang.ToLowerInvariant());
            t.Tags.Add("console");
            t.Variables.Add(new TemplateVariable { Name = "ProjectName", Prompt = "Project name", Default = "MyApp", Required = true });
            t.Variables.Add(new TemplateVariable { Name = "Description", Prompt = "Description", Default = "A " + lang + " project." });
            t.Files.AddRange(files);
            return t;
        }

        // A non-console Python template (customizable ProjectType), otherwise mirroring Console().
        private static ProjectTemplate PyKind(string id, string name, string icon, string projectType, string desc, params TemplateFile[] files)
        {
            var t = new ProjectTemplate
            {
                Id = id, Name = name, Description = desc, Language = "Python", Framework = "CPython", Icon = icon,
                Platform = "Cross-platform", ProjectType = projectType, Category = "Official", Author = "HelixStudio", Version = "1.0.0",
            };
            t.Tags.Add("python");
            t.Variables.Add(new TemplateVariable { Name = "ProjectName", Prompt = "Project name", Default = "MyApp", Required = true });
            t.Variables.Add(new TemplateVariable { Name = "Description", Prompt = "Description", Default = "A Python project." });
            t.Files.AddRange(files);
            return t;
        }

        // A fresh Python config page (Python version + venv), matching py.console's. A new instance
        // per template avoids sharing mutable state across catalog entries.
        private static TemplateConfigPage PythonPage()
        {
            var p = new TemplateConfigPage { Title = "Python" };
            p.Fields.Add(Choice("PythonVersion", "Python version", "3.11", "3.11", "3.12", "3.13"));
            p.Fields.Add(Boolean("UseVenv", "Create virtual environment", true));
            return p;
        }

        private static TemplateConfigField Choice(string name, string label, string def, params string[] choices)
        {
            var f = new TemplateConfigField { Name = name, Label = label, Type = ConfigFieldType.Choice, Default = def };
            f.Choices.AddRange(choices);
            return f;
        }

        private static TemplateConfigField Boolean(string name, string label, bool def)
        {
            return new TemplateConfigField { Name = name, Label = label, Type = ConfigFieldType.Bool, Default = def ? "true" : "false" };
        }
    }
}
