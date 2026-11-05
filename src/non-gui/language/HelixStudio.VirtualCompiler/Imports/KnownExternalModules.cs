using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Imports
{
    /// <summary>
    /// Top-level module names the virtual compiler treats as resolvable without finding them on
    /// the <see cref="PythonSearchPath"/>: the CPython 3.12 standard library plus the ubiquitous
    /// third-party ecosystem that lives in <c>site-packages</c>, which the compiler cannot see.
    ///
    /// A module in this set is <b>opaque</b> — resolvable, but with unknown contents. The import
    /// checker therefore never reports "cannot import name" against one; hard-erroring on
    /// <c>numpy</c>/<c>torch</c> imports was previously the single largest false-positive class in
    /// the linker (75 findings on one fixture, 126 on another, almost all of them valid code).
    ///
    /// This is the one authority for the list — <c>HelixPythonNamespace.Linker.ImportResolver</c> defers to
    /// it rather than keeping a second copy that can drift.
    /// </summary>
    public static class KnownExternalModules
    {
        /// <summary>True when <paramref name="dottedName"/>'s top-level package is known-external.</summary>
        public static bool ContainsTopLevelOf(string dottedName)
        {
            if (string.IsNullOrEmpty(dottedName)) return false;
            int dot = dottedName.IndexOf('.');
            string top = dot < 0 ? dottedName : dottedName.Substring(0, dot);
            return Names.Contains(top);
        }

        /// <summary>True when <paramref name="dottedName"/>'s top-level package is CPython's own.</summary>
        public static bool IsStandardLibrary(string dottedName)
        {
            return StandardLibrary.Contains(TopLevelOf(dottedName));
        }

        /// <summary>The top-level package of a dotted module name: <c>os.path</c> -> <c>os</c>.</summary>
        public static string TopLevelOf(string dottedName)
        {
            if (string.IsNullOrEmpty(dottedName)) return string.Empty;
            int dot = dottedName.IndexOf('.');
            return dot < 0 ? dottedName : dottedName.Substring(0, dot);
        }

        /// <summary>
        /// The CPython standard library only. Split out from <see cref="Names"/> so callers that
        /// must tell "ships with the interpreter" apart from "installed from PyPI" — the Solution
        /// Explorer's Dependencies grouping — have one authority to ask rather than a second list
        /// of their own that drifts.
        /// </summary>
        public static readonly HashSet<string> StandardLibrary = new HashSet<string>(StringComparer.Ordinal)
        {
            "__future__", "__main__", "builtins",
            "abc", "argparse", "array", "ast", "asyncio", "atexit", "base64",
            "binascii", "bisect", "bz2", "calendar", "cmath", "cmd",
            "codecs", "collections", "concurrent", "configparser", "contextlib",
            "contextvars", "copy", "copyreg", "csv", "ctypes", "curses", "dataclasses",
            "datetime", "decimal", "difflib", "dis", "doctest", "email",
            "encodings", "enum", "errno", "faulthandler", "fcntl", "filecmp",
            "fileinput", "fnmatch", "fractions", "ftplib", "functools", "gc",
            "getopt", "getpass", "gettext", "glob", "graphlib", "gzip", "hashlib",
            "heapq", "hmac", "html", "http", "imaplib", "importlib", "inspect",
            "io", "ipaddress", "itertools", "json", "keyword", "linecache",
            "locale", "logging", "lzma", "mailbox", "math", "mimetypes", "mmap",
            "multiprocessing", "netrc", "numbers", "operator", "optparse", "os",
            "pathlib", "pickle", "pickletools", "pkgutil", "platform", "plistlib",
            "poplib", "posix", "posixpath", "pprint", "profile", "pstats", "pty",
            "pwd", "queue", "quopri", "random", "re", "readline", "reprlib",
            "resource", "rlcompleter", "runpy", "sched", "secrets", "select",
            "selectors", "shelve", "shlex", "shutil", "signal", "site", "smtplib",
            "socket", "socketserver", "sqlite3", "ssl", "stat", "statistics",
            "string", "stringprep", "struct", "subprocess", "symtable", "sys",
            "sysconfig", "syslog", "tarfile", "tempfile", "termios", "textwrap",
            "threading", "time", "timeit", "tkinter", "token", "tokenize",
            "tomllib", "trace", "traceback", "tracemalloc", "tty", "types",
            "typing", "unicodedata", "unittest", "urllib", "uu", "uuid", "venv",
            "warnings", "wave", "weakref", "webbrowser", "winreg", "winsound",
            "wsgiref", "xml", "xmlrpc", "zipapp", "zipfile", "zipimport", "zlib",
            "zoneinfo",
        };

        /// <summary>
        /// Ubiquitous third-party packages that live in <c>site-packages</c>, keyed by IMPORT name
        /// (which can differ from the pip name — cv2/opencv, PIL/Pillow, sklearn/scikit-learn,
        /// yaml/PyYAML, Bio/biopython). Not exhaustive and never can be: anything absent is still
        /// classified as third-party by elimination, this set only makes the common cases certain.
        /// </summary>
        public static readonly HashSet<string> ThirdParty = new HashSet<string>(StringComparer.Ordinal)
        {
            "pytest", "hypothesis", "mock", "pytest_asyncio", "typing_extensions",
            // scientific / numeric
            "numpy", "scipy", "pandas", "sympy", "statsmodels", "xarray", "numexpr",
            "bottleneck", "numba", "llvmlite", "cython", "mpmath", "patsy",
            // plotting / viz
            "matplotlib", "mpl_toolkits", "seaborn", "plotly", "bokeh", "PIL", "cv2",
            "skimage", "imageio", "lxml",
            // ML / DL
            "torch", "torchvision", "torchaudio", "tensorflow", "keras", "jax", "jaxlib",
            "flax", "sklearn", "transformers", "einops", "cupy", "lightning",
            "pytorch_lightning", "wandb", "onnx", "onnxruntime", "safetensors", "timm",
            "accelerate", "datasets", "tokenizers", "sentencepiece",
            // data / io / storage
            "h5py", "tables", "zarr", "netCDF4", "joblib", "dask", "pyarrow", "fastparquet",
            "openpyxl", "xlrd", "lmdb", "msgpack", "orjson", "ujson", "toml", "ruamel",
            "yaml", "protobuf", "google", "flatbuffers",
            // scientific domain (the fixture corpus is molecular dynamics / structural bio)
            "Bio", "mdtraj", "MDAnalysis", "ase", "rdkit", "pymatgen", "openmm", "prody",
            "networkx", "igraph",
            // structural biology / molecular modelling. Import names, not pip names, and the
            // casing is load-bearing because this set is Ordinal: py3Dmol, never py3dmol.
            // simtk is OpenMM's pre-7.6 namespace and still appears in most published scripts.
            "biotite", "gemmi", "pdbfixer", "parmed", "pytraj", "freesasa", "biopandas",
            "openbabel", "pymol", "chimerax", "simtk", "nglview", "py3Dmol",
            // sequence / genomics. Registered even though this product ships no genomics
            // workflow: an unlisted top-level module is a PY5002 *error*, which gates Compile,
            // Run and Debug — so omitting them refuses to run correct code rather than merely
            // failing to help with it.
            "pysam", "pyfaidx", "pyBigWig", "cyvcf2", "HTSeq", "gffutils", "skbio", "ete3",
            "scanpy", "anndata",
            // web / networking / cloud / cli / utils
            "requests", "urllib3", "aiohttp", "httpx", "flask", "django", "fastapi",
            "starlette", "uvicorn", "gunicorn", "boto3", "botocore", "redis", "pymongo",
            "sqlalchemy", "psycopg2", "click", "typer", "rich", "tqdm", "colorama",
            "tabulate", "attr", "attrs", "pydantic", "dateutil", "pytz", "six",
            "setuptools", "pkg_resources", "wheel", "pip", "packaging", "importlib_metadata",
            "importlib_resources", "cffi", "pycparser", "cryptography", "certifi",
            "charset_normalizer", "idna", "markupsafe", "jinja2", "werkzeug", "itsdangerous",
        };

        /// <summary>
        /// Everything the compiler treats as opaque-but-resolvable: the standard library plus the
        /// known third-party ecosystem. Kept as the union of the two sets above so there is still
        /// exactly one list to maintain.
        /// </summary>
        public static readonly HashSet<string> Names = BuildUnion();

        private static HashSet<string> BuildUnion()
        {
            var all = new HashSet<string>(StandardLibrary, StringComparer.Ordinal);
            foreach (string n in ThirdParty) all.Add(n);
            return all;
        }
    }
}
