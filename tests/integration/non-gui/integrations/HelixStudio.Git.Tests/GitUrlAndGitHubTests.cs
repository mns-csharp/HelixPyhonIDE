namespace HelixPythonNamespace.Git.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Git;

    [TestClass]
    public class GitUrlUtilTests
    {
        [TestMethod]
        public void IsHttpsUrlAndStatus404Detection()
        {
            Assert.IsTrue(GitUrlUtil.IsHttpsUrl("https://github.com/o/r.git"));
            Assert.IsFalse(GitUrlUtil.IsHttpsUrl("git@github.com:o/r.git"));
            Assert.IsTrue(GitUrlUtil.LooksLikeStatus404("request failed with status code: 404"));
            Assert.IsTrue(GitUrlUtil.LooksLikeStatus404("status 404"));
            Assert.IsFalse(GitUrlUtil.LooksLikeStatus404("status 200"));
        }

        [TestMethod]
        public void PercentEncodeReservedChars()
        {
            Assert.AreEqual("abc-_.~", GitUrlUtil.UrlPercentEncode("abc-_.~"));
            Assert.AreEqual("a%2Fb%40c", GitUrlUtil.UrlPercentEncode("a/b@c"));
        }

        [TestMethod]
        public void EmbedCredentialsUsesPlaceholderForEmptyUser()
        {
            string url = GitUrlUtil.EmbedCredentialsInHttpsUrl("https://github.com/o/r.git", "", "ghp_token");
            Assert.AreEqual("https://x-access-token:ghp_token@github.com/o/r.git", url);
        }

        [TestMethod]
        public void EmbedCredentialsStripsExistingUserInfo()
        {
            string url = GitUrlUtil.EmbedCredentialsInHttpsUrl("https://old:pass@github.com/o/r.git", "me", "tok");
            Assert.AreEqual("https://me:tok@github.com/o/r.git", url);
        }

        [TestMethod]
        public void EmbedCredentialsLeavesNonHttpsUntouched()
        {
            Assert.AreEqual("git@github.com:o/r.git",
                GitUrlUtil.EmbedCredentialsInHttpsUrl("git@github.com:o/r.git", "me", "tok"));
        }

        [TestMethod]
        public void ParsesGitHubOwnerRepo()
        {
            OwnerRepo r = GitUrlUtil.ParseGitHubOwnerRepo("https://github.com/octocat/Hello-World.git");
            Assert.IsNotNull(r);
            Assert.AreEqual("octocat", r.Owner);
            Assert.AreEqual("Hello-World", r.Repo);
        }

        [TestMethod]
        public void ParsesGitHubWithTrailingSlashAndWww()
        {
            OwnerRepo r = GitUrlUtil.ParseGitHubOwnerRepo("https://www.github.com/o/r/");
            Assert.IsNotNull(r);
            Assert.AreEqual("o", r.Owner);
            Assert.AreEqual("r", r.Repo);
        }

        [TestMethod]
        public void ParsesGitHubStrippingEmbeddedCreds()
        {
            OwnerRepo r = GitUrlUtil.ParseGitHubOwnerRepo("https://tok@github.com/o/r");
            Assert.IsNotNull(r);
            Assert.AreEqual("o", r.Owner);
        }

        [TestMethod]
        public void RejectsNonGitHubAndExtraPath()
        {
            Assert.IsNull(GitUrlUtil.ParseGitHubOwnerRepo("https://gitlab.com/o/r.git"));
            Assert.IsNull(GitUrlUtil.ParseGitHubOwnerRepo("https://github.com/o/r/pulls"));
            Assert.IsNull(GitUrlUtil.ParseGitHubOwnerRepo("https://github.com/onlyowner"));
            Assert.IsNull(GitUrlUtil.ParseGitHubOwnerRepo("git@github.com:o/r.git"));
        }
    }

    [TestClass]
    public class GitHubClientTests
    {
        [TestMethod]
        public void ParseCreateResponseSuccess()
        {
            string json = "{\"full_name\":\"u/r\",\"html_url\":\"https://github.com/u/r\"," +
                          "\"clone_url\":\"https://github.com/u/r.git\",\"ssh_url\":\"git@github.com:u/r.git\"}";
            GitHubCreateRepoResult r = GitHubClient.ParseCreateResponse(201, json);
            Assert.IsTrue(r.Ok);
            Assert.AreEqual("u/r", r.FullName);
            Assert.AreEqual("https://github.com/u/r.git", r.CloneUrlHttps);
            Assert.AreEqual("git@github.com:u/r.git", r.SshUrl);
        }

        [TestMethod]
        public void ParseCreateResponseErrorUsesMessage()
        {
            GitHubCreateRepoResult r = GitHubClient.ParseCreateResponse(422, "{\"message\":\"name already exists\"}");
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("name already exists", r.ErrorMessage);
        }

        [TestMethod]
        public void ParseCreateResponseInvalidJson()
        {
            GitHubCreateRepoResult r = GitHubClient.ParseCreateResponse(500, "<html>oops</html>");
            Assert.IsFalse(r.Ok);
            Assert.IsTrue(r.ErrorMessage.Contains("Invalid JSON"));
        }

        [TestMethod]
        public void StatusToDiagnosisMapping()
        {
            Assert.AreEqual(GitHubRepoDiagnosis.DiagResult.Accessible, GitHubClient.StatusToDiagnosis(200));
            Assert.AreEqual(GitHubRepoDiagnosis.DiagResult.InvalidToken, GitHubClient.StatusToDiagnosis(401));
            Assert.AreEqual(GitHubRepoDiagnosis.DiagResult.InsufficientScope, GitHubClient.StatusToDiagnosis(403));
            Assert.AreEqual(GitHubRepoDiagnosis.DiagResult.NotFoundOrNoAccess, GitHubClient.StatusToDiagnosis(404));
            Assert.AreEqual(GitHubRepoDiagnosis.DiagResult.Other, GitHubClient.StatusToDiagnosis(500));
        }

        [TestMethod]
        public void ExtractMessageReadsField()
        {
            Assert.AreEqual("Not Found", GitHubClient.ExtractMessage("{\"message\":\"Not Found\"}"));
            Assert.AreEqual("", GitHubClient.ExtractMessage("not json"));
        }
    }
}
