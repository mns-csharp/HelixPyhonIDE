using System.IO;
using System.Text;
using HelixPythonNamespace.Pdb.Io;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// The extension <c>.pdb</c> is claimed by two unrelated formats and this IDE meets both, so
    /// the probe that tells them apart is load-bearing: it is what stands between a double-click on
    /// a build directory's debug symbols and a molecular viewer reporting an empty structure.
    /// </summary>
    [TestClass]
    public class PdbFileProbeTests
    {
        [TestMethod]
        public void AStructureWithALongHeaderIsRecognised()
        {
            // 4HHB — haemoglobin — puts its first ATOM record at byte 71,684, behind 708 REMARK
            // lines and 46 SEQRES lines. Measured against files.rcsb.org during testing, and it is
            // not an outlier: 1AON starts at 186,218, 1HTQ at 277,100, 5XNL at 832,841. The probe
            // read only the first 64 KB, so all four were reported as "not a molecular structure"
            // — from the Open dialog as much as from a download.
            //
            // Synthesised rather than checked in, because pinning this needs the header LENGTH,
            // not haemoglobin: a 474 KB fixture would carry the same one bit of information.
            var text = new StringBuilder();
            text.Append("HEADER    OXYGEN TRANSPORT                        07-MAR-84   4HHB\n");
            while (text.Length < 72 * 1024)
                text.Append("REMARK   2 RESOLUTION.    1.74 ANGSTROMS.                             \n");
            text.Append("ATOM      1  N   VAL A   1       6.204  16.869   4.854  1.00 49.05           N\n");
            text.Append("END\n");

            string path = Path.Combine(Path.GetTempPath(),
                "helixpython-longheader-" + System.Guid.NewGuid().ToString("N") + ".pdb");
            File.WriteAllText(path, text.ToString(), Encoding.ASCII);

            try
            {
                string reason;
                Assert.IsTrue(PdbFileProbe.IsMolecularStructure(path, out reason),
                    "a structure whose coordinates start past 64 KB must still be recognised, got: " + reason);
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        [TestMethod]
        public void AMicrosoftProgramDatabaseIsRejected_AndSaysWhat_ItIs()
        {
            // Written rather than referenced: the repository's real .pdb symbol files live under
            // build directories that a clean checkout may not have, and the discriminator is the
            // magic, which is stable.
            string path = Path.Combine(Path.GetTempPath(),
                "helixpython-probe-" + System.Guid.NewGuid().ToString("N") + ".pdb");
            var msf = new MemoryStream();
            byte[] magic = Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0");
            msf.Write(magic, 0, magic.Length);
            for (int i = 0; i < 4096; i++) msf.WriteByte((byte)(i & 0xFF));
            File.WriteAllBytes(path, msf.ToArray());

            try
            {
                string reason;
                Assert.IsFalse(PdbFileProbe.IsMolecularStructure(path, out reason),
                    "MSVC debug symbols must not be treated as a molecular structure");
                StringAssert.Contains(reason, "program database",
                    "the reason must name what the file actually is, so the user is not left "
                    + "thinking the viewer failed");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [TestMethod]
        public void APlainTextFileWithNoCoordinatesIsRejected()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "helixpython-probe-" + System.Guid.NewGuid().ToString("N") + ".pdb");
            File.WriteAllText(path,
                "HEADER    NOT REALLY A STRUCTURE\r\nTITLE     header lines only\r\nEND\r\n");
            try
            {
                string reason;
                Assert.IsFalse(PdbFileProbe.IsMolecularStructure(path, out reason),
                    "a header-only file has no coordinates to draw");
                Assert.IsNotNull(reason);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [TestMethod]
        public void AMissingFileIsRejectedWithoutThrowing()
        {
            string reason;
            Assert.IsFalse(PdbFileProbe.IsMolecularStructure(
                Path.Combine(Path.GetTempPath(), "no-such-file-here.pdb"), out reason));
            Assert.IsNotNull(reason);
        }

        [TestMethod]
        public void TheExtensionGateAcceptsTheStructureExtensions()
        {
            Assert.IsTrue(PdbFileProbe.HasStructureExtension(@"C:\x\model.pdb"));
            Assert.IsTrue(PdbFileProbe.HasStructureExtension(@"C:\x\model.PDB"));
            Assert.IsTrue(PdbFileProbe.HasStructureExtension(@"C:\x\model.ent"));
            Assert.IsFalse(PdbFileProbe.HasStructureExtension(@"C:\x\model.py"));
            Assert.IsFalse(PdbFileProbe.HasStructureExtension(null));
        }
    }
}
