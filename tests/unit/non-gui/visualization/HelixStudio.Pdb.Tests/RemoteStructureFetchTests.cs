using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using HelixPythonNamespace.Pdb.Io;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Fetching a structure by accession — the classification, the URLs, the cache, and every
    /// failure the user can actually hit.
    ///
    /// The transport is replaced throughout. That is not to avoid testing the network: the real
    /// endpoints were verified by hand during testing and what they said is recorded in
    /// <see cref="RemoteStructureFetch"/>'s remarks. It is because the branches that matter here
    /// are the ones a live run does NOT take — the entry that exists only as mmCIF, the proxy that
    /// answers 200 with a login page, the second call that must not hit the network at all — and a
    /// test that needs a working internet connection to say anything is a test that goes quiet
    /// exactly when someone is on a conference wifi.
    /// </summary>
    [TestClass]
    public class RemoteStructureFetchTests
    {
        private string _cache;

        [TestInitialize]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "helix-fetch-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void TearDown()
        {
            try { if (Directory.Exists(_cache)) Directory.Delete(_cache, true); } catch (IOException) { }
        }

        // A minimal but genuine PDB body: PdbFileProbe requires a real coordinate record, which is
        // the whole point of committing through it.
        private const string CrambinHead =
            "HEADER    PLANT PROTEIN                           30-APR-81   1CRN\n" +
            "ATOM      1  N   THR A   1      17.047  14.099   3.625  1.00 13.79           N\n" +
            "ATOM      2  CA  THR A   1      16.967  12.784   4.338  1.00 10.80           C\n" +
            "END\n";

        private static byte[] Ascii(string text) { return Encoding.ASCII.GetBytes(text); }

        /// <summary>Records every URL asked for, and answers from a table.</summary>
        private sealed class FakeTransport
        {
            private readonly Dictionary<string, byte[]> _answers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public readonly List<string> Requested = new List<string>();
            public Exception ThrowInstead;

            public void Add(string url, string body) { _answers[url] = Ascii(body); }

            public byte[] Get(string url, CancellationToken cancel)
            {
                Requested.Add(url);
                if (ThrowInstead != null) throw ThrowInstead;
                byte[] body;
                if (!_answers.TryGetValue(url, out body))
                    throw new StructureNotFoundException("404 from " + url);
                return body;
            }
        }

        private RemoteStructureFetch With(FakeTransport transport)
        {
            return new RemoteStructureFetch(_cache) { Transport = transport.Get };
        }

        // ---- classification --------------------------------------------------------------------

        [TestMethod]
        public void AFourCharacterEntryId_IsAPdbEntry_AndIsUpperCased()
        {
            StructureAccession a = StructureAccession.Parse(" 1crn ");
            Assert.AreEqual(StructureAccessionKind.PdbEntry, a.Kind);
            Assert.AreEqual("1CRN", a.Id);
            Assert.AreEqual("1crn", a.Typed, "the user's own text is kept for error messages");
        }

        [TestMethod]
        public void TheExtendedPdbIdForm_ReducesToTheLegacyId()
        {
            StructureAccession a = StructureAccession.Parse("pdb_00001crn");
            Assert.AreEqual(StructureAccessionKind.PdbEntry, a.Kind);
            Assert.AreEqual("1CRN", a.Id);
        }

        [TestMethod]
        public void UniProtAccessions_AreRecognisedInBothLengths()
        {
            Assert.AreEqual(StructureAccessionKind.UniProt, StructureAccession.Parse("P69905").Kind);
            Assert.AreEqual(StructureAccessionKind.UniProt, StructureAccession.Parse("A0A023GPI8").Kind);
            Assert.AreEqual("P69905", StructureAccession.Parse("p69905").Id);
        }

        [TestMethod]
        public void TheTwoNamespacesDoNotCollide()
        {
            // A PDB id starts with a digit and is four long; a UniProt accession starts with a
            // letter and is six or ten. Nothing is classified as both, which is what lets one text
            // box serve both databases without asking the user which they meant.
            foreach (string pdb in new[] { "1CRN", "4HHB", "2QWE", "9ZZZ" })
                Assert.AreEqual(StructureAccessionKind.PdbEntry, StructureAccession.Parse(pdb).Kind, pdb);
            foreach (string up in new[] { "P69905", "Q9Y6K9", "A0A023GPI8", "O00255" })
                Assert.AreEqual(StructureAccessionKind.UniProt, StructureAccession.Parse(up).Kind, up);
        }

        [TestMethod]
        public void AnythingElse_IsUnrecognisedAndSaysWhatIsExpected()
        {
            foreach (string junk in new[] { "", "   ", "hemoglobin", "HBA_HUMAN", "1CR", "1CRN.pdb", "12345" })
            {
                StructureAccession a = StructureAccession.Parse(junk);
                Assert.AreEqual(StructureAccessionKind.Unrecognised, a.Kind, junk);
                Assert.IsFalse(a.IsRecognised, junk);
            }

            StringAssert.Contains(StructureAccession.Parse("hemoglobin").Describe(), "1CRN");
            StringAssert.Contains(StructureAccession.Parse("hemoglobin").Describe(), "P69905");
        }

        [TestMethod]
        public void DescribeNamesTheDatabaseBeforeTheRequestIsMade()
        {
            StringAssert.Contains(StructureAccession.Parse("1CRN").Describe(), "RCSB");
            StringAssert.Contains(StructureAccession.Parse("P69905").Describe(), "AlphaFold");
        }

        // ---- urls ------------------------------------------------------------------------------

        [TestMethod]
        public void UrlsAreBuiltAgainstTheDocumentedEndpoints()
        {
            Assert.AreEqual("https://files.rcsb.org/download/1CRN.pdb", RemoteStructureFetch.RcsbUrlFor("1crn"));
            Assert.AreEqual("https://alphafold.ebi.ac.uk/api/prediction/P69905",
                RemoteStructureFetch.AlphaFoldApiUrlFor("p69905"));
        }

        // ---- the PDB ---------------------------------------------------------------------------

        [TestMethod]
        public void APdbEntry_IsDownloadedAndCachedUnderItsEntryId()
        {
            var transport = new FakeTransport();
            transport.Add("https://files.rcsb.org/download/1CRN.pdb", CrambinHead);

            string path = With(transport).Fetch("1crn", CancellationToken.None);

            Assert.AreEqual(Path.Combine(_cache, "1CRN.pdb"), path);
            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(PdbFileProbe.IsMolecularStructure(path), "the cached file must open as a structure");
        }

        [TestMethod]
        public void ASecondFetchOfTheSameEntry_DoesNotTouchTheNetwork()
        {
            var transport = new FakeTransport();
            transport.Add("https://files.rcsb.org/download/1CRN.pdb", CrambinHead);
            RemoteStructureFetch fetch = With(transport);

            fetch.Fetch("1CRN", CancellationToken.None);
            fetch.Fetch("1CRN", CancellationToken.None);

            Assert.AreEqual(1, transport.Requested.Count,
                "a structure already on disk is the answer; re-downloading it is what makes a demo "
                + "depend on the venue's wifi");
        }

        [TestMethod]
        public void AnEntryWithNoPdbFormatFile_FallsBackToMmcif()
        {
            // Measured: 6EK0 is a real entry and files.rcsb.org 404s the .pdb form,
            // because 219,596 atoms in 521 segments with two-character chain ids do not fit the
            // 80-column format. Reporting that as "no such entry" sent the user hunting for a typo
            // that was not there; the fix is to ask for the format the entry actually has.
            var transport = new FakeTransport();
            transport.Add("https://files.rcsb.org/download/6EK0.cif",
                "data_6EK0\n_entry.id 6EK0\n" + CrambinHead);

            string path = With(transport).Fetch("6EK0", CancellationToken.None);

            Assert.AreEqual("https://files.rcsb.org/download/6EK0.pdb", transport.Requested[0],
                "the smaller fixed-width file is asked for first");
            Assert.AreEqual("https://files.rcsb.org/download/6EK0.cif", transport.Requested[1],
                "and mmCIF only when that one is not served");
            Assert.AreEqual(Path.Combine(_cache, "6EK0.cif"), path);
        }

        [TestMethod]
        public void AnEntryServedInNeitherFormat_IsReportedAsMissing()
        {
            var transport = new FakeTransport();   // 404s everything

            try
            {
                With(transport).Fetch("9ZZZ", CancellationToken.None);
                Assert.Fail("expected a not-found");
            }
            catch (StructureNotFoundException ex)
            {
                StringAssert.Contains(ex.Message, "9ZZZ");
                StringAssert.Contains(ex.Message, "neither PDB nor mmCIF");
            }

            Assert.AreEqual(2, transport.Requested.Count, "both formats are tried before giving up");
        }

        [TestMethod]
        public void AFailedDownload_LeavesNothingInTheCache()
        {
            var transport = new FakeTransport();
            try { With(transport).Fetch("1CRN", CancellationToken.None); }
            catch (StructureNotFoundException) { }

            Assert.IsFalse(File.Exists(Path.Combine(_cache, "1CRN.pdb")));
            Assert.IsFalse(File.Exists(Path.Combine(_cache, "1CRN.pdb.part")));
        }

        [TestMethod]
        public void AnHtmlLoginPageAnsweredWith200_IsRefusedRatherThanCached()
        {
            // A captive portal is the realistic version of this, and the damage is delayed: cached
            // under 1CRN.pdb, it opens later as an empty molecule and reads as a broken viewer.
            var transport = new FakeTransport();
            transport.Add("https://files.rcsb.org/download/1CRN.pdb",
                "<html><body>Sign in to the network</body></html>");

            try
            {
                With(transport).Fetch("1CRN", CancellationToken.None);
                Assert.Fail("expected the body to be rejected");
            }
            catch (InvalidDataException ex)
            {
                StringAssert.Contains(ex.Message, "not a PDB structure");
            }

            Assert.IsFalse(File.Exists(Path.Combine(_cache, "1CRN.pdb")),
                "a body that is not a structure must never land under a name the next run trusts");
        }

        // ---- the AlphaFold database --------------------------------------------------------------

        private const string PredictionJson =
            "[{\"entryId\":\"AF-P69905-F1\",\"latestVersion\":6," +
            "\"pdbUrl\":\"https://alphafold.ebi.ac.uk/files/AF-P69905-F1-model_v6.pdb\"," +
            "\"cifUrl\":\"https://alphafold.ebi.ac.uk/files/AF-P69905-F1-model_v6.cif\"}]";

        private FakeTransport AlphaFoldTransport()
        {
            var transport = new FakeTransport();
            transport.Add("https://alphafold.ebi.ac.uk/api/prediction/P69905", PredictionJson);
            transport.Add("https://alphafold.ebi.ac.uk/files/AF-P69905-F1-model_v6.pdb", CrambinHead);
            return transport;
        }

        [TestMethod]
        public void AUniProtAccession_IsResolvedThroughTheApi_NotByGuessingAVersion()
        {
            FakeTransport transport = AlphaFoldTransport();

            string path = With(transport).Fetch("P69905", CancellationToken.None);

            Assert.AreEqual("https://alphafold.ebi.ac.uk/api/prediction/P69905", transport.Requested[0],
                "the version in the file name is not guessable — v4 URLs 404 now that the database is on v6");
            Assert.AreEqual("https://alphafold.ebi.ac.uk/files/AF-P69905-F1-model_v6.pdb", transport.Requested[1]);
            Assert.AreEqual(Path.Combine(_cache, "AF-P69905-F1-model_v6.pdb"), path,
                "the model version belongs in the cached name, so a superseded download cannot pass for the current one");
        }

        [TestMethod]
        public void AnAccessionAlphaFoldHasNoModelFor_IsReportedAsThat()
        {
            var transport = new FakeTransport();   // answers 404 to everything

            try
            {
                With(transport).Fetch("P69905", CancellationToken.None);
                Assert.Fail("expected a not-found");
            }
            catch (StructureNotFoundException ex)
            {
                StringAssert.Contains(ex.Message, "AlphaFold");
                StringAssert.Contains(ex.Message, "P69905");
            }
        }

        [TestMethod]
        public void AUrlPointingSomewhereElse_IsRefused()
        {
            // The next GET's destination arrives over the network, so it is pinned to one host and
            // one file type rather than followed on trust.
            var transport = new FakeTransport();
            transport.Add("https://alphafold.ebi.ac.uk/api/prediction/P69905",
                "[{\"pdbUrl\":\"https://example.invalid/evil.pdb\"}]");

            try
            {
                With(transport).Fetch("P69905", CancellationToken.None);
                Assert.Fail("expected the url to be refused");
            }
            catch (InvalidDataException ex)
            {
                StringAssert.Contains(ex.Message, "refusing to follow");
            }
        }

        [TestMethod]
        public void WhenTheApiIsUnreachable_AnAlreadyDownloadedModelIsStillOpened()
        {
            FakeTransport online = AlphaFoldTransport();
            var fetch = new RemoteStructureFetch(_cache) { Transport = online.Get };
            string first = fetch.Fetch("P69905", CancellationToken.None);

            var offline = new FakeTransport { ThrowInstead = new IOException("Could not reach alphafold.ebi.ac.uk") };
            string second = new RemoteStructureFetch(_cache) { Transport = offline.Get }
                .Fetch("P69905", CancellationToken.None);

            Assert.AreEqual(first, second,
                "re-opening a model already on disk must not need the network");
        }

        [TestMethod]
        public void WhenTheApiIsUnreachableAndNothingIsCached_TheTransportFailureIsWhatSurfaces()
        {
            var offline = new FakeTransport { ThrowInstead = new IOException("Could not reach alphafold.ebi.ac.uk") };

            try
            {
                new RemoteStructureFetch(_cache) { Transport = offline.Get }.Fetch("P69905", CancellationToken.None);
                Assert.Fail("expected the transport failure");
            }
            catch (IOException ex)
            {
                StringAssert.Contains(ex.Message, "alphafold.ebi.ac.uk");
            }
        }

        [TestMethod]
        public void TheNewestCachedModelWins_NotTheFirstFound()
        {
            Directory.CreateDirectory(_cache);
            File.WriteAllText(Path.Combine(_cache, "AF-P69905-F1-model_v4.pdb"), CrambinHead);
            File.WriteAllText(Path.Combine(_cache, "AF-P69905-F1-model_v10.pdb"), CrambinHead);
            File.WriteAllText(Path.Combine(_cache, "AF-P69905-F1-model_v6.pdb"), CrambinHead);

            var offline = new FakeTransport { ThrowInstead = new IOException("offline") };
            string path = new RemoteStructureFetch(_cache) { Transport = offline.Get }
                .Fetch("P69905", CancellationToken.None);

            Assert.AreEqual(Path.Combine(_cache, "AF-P69905-F1-model_v10.pdb"), path,
                "v10 sorts before v4 as text; the version is a number");
        }

        // ---- refusals ---------------------------------------------------------------------------

        [TestMethod]
        public void AnUnrecognisedAccession_IsRefusedWithoutAnyRequest()
        {
            var transport = new FakeTransport();

            try
            {
                With(transport).Fetch("hemoglobin", CancellationToken.None);
                Assert.Fail("expected a refusal");
            }
            catch (StructureNotFoundException ex)
            {
                StringAssert.Contains(ex.Message, "hemoglobin");
            }

            Assert.AreEqual(0, transport.Requested.Count, "nothing typed-wrong should reach a server");
        }

        [TestMethod]
        public void ACancelledFetch_DoesNotStart()
        {
            var transport = new FakeTransport();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try
                {
                    With(transport).Fetch("1CRN", cts.Token);
                    Assert.Fail("expected cancellation");
                }
                catch (OperationCanceledException) { }
            }

            Assert.AreEqual(0, transport.Requested.Count);
        }

        [TestMethod]
        public void TheDefaultCacheDirectory_FollowsTheIsolatedStateDirectory()
        {
            string previous = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            try
            {
                Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _cache);
                Assert.AreEqual(Path.Combine(_cache, RemoteStructureFetch.CacheFolderName),
                    RemoteStructureFetch.DefaultCacheDirectory(),
                    "a test run must never write into the developer's own structure cache");
            }
            finally
            {
                Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", previous);
            }
        }
    }
}
