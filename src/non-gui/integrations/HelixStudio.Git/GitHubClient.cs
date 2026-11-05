namespace HelixPythonNamespace.Git
{
    using System;
    using System.Net.Http;
    using System.Text;
    using Newtonsoft.Json.Linq;

    public sealed class GitHubCreateRepoResult
    {
        public bool Ok;
        public string ErrorMessage = "";
        public string FullName = "";
        public string HtmlUrl = "";
        public string CloneUrlHttps = "";
        public string SshUrl = "";
    }

    public sealed class GitHubRepoDiagnosis
    {
        public enum DiagResult
        {
            Accessible,           // HTTP 200
            InvalidToken,         // HTTP 401
            InsufficientScope,    // HTTP 403
            NotFoundOrNoAccess,   // HTTP 404
            NetworkError,
            Other,
        }

        public DiagResult Result = DiagResult.Other;
        public int HttpStatus;
        public string GithubMessage = "";
        public string OauthScopes = "";
        public string AcceptedScopes = "";
        public bool FineGrained;
    }

    /// <summary>
    /// Thin synchronous GitHub REST client (create repo + diagnose access). Port of the
    /// C++ <c>GitHubClient</c> (QNetworkAccessManager → <see cref="HttpClient"/>,
    /// QJsonDocument → Newtonsoft). Blocks on the round-trip; callers gate the UI.
    /// </summary>
    public static class GitHubClient
    {
        private static readonly HttpClient Http = new HttpClient();

        public static GitHubCreateRepoResult CreateRepository(string name, string description, bool isPrivate, string personalAccessToken)
        {
            GitHubCreateRepoResult r = new GitHubCreateRepoResult();
            try
            {
                JObject body = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["private"] = isPrivate,
                    ["auto_init"] = false,
                };
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/user/repos");
                ApplyHeaders(req, personalAccessToken);
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

                HttpResponseMessage resp = Http.SendAsync(req).GetAwaiter().GetResult();
                int status = (int)resp.StatusCode;
                string bodyText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return ParseCreateResponse(status, bodyText);
            }
            catch (Exception ex)
            {
                r.ErrorMessage = "Network error: " + ex.Message;
                return r;
            }
        }

        public static GitHubRepoDiagnosis DiagnoseRepoAccess(string owner, string repo, string personalAccessToken)
        {
            GitHubRepoDiagnosis d = new GitHubRepoDiagnosis();
            d.FineGrained = personalAccessToken != null && personalAccessToken.StartsWith("github_pat_", StringComparison.Ordinal);
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/" + owner + "/" + repo);
                ApplyHeaders(req, personalAccessToken);

                HttpResponseMessage resp = Http.SendAsync(req).GetAwaiter().GetResult();
                d.HttpStatus = (int)resp.StatusCode;
                d.OauthScopes = FirstHeader(resp, "X-OAuth-Scopes");
                d.AcceptedScopes = FirstHeader(resp, "X-Accepted-OAuth-Scopes");
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                d.GithubMessage = ExtractMessage(body);
                d.Result = StatusToDiagnosis(d.HttpStatus);
                return d;
            }
            catch (Exception ex)
            {
                d.Result = GitHubRepoDiagnosis.DiagResult.NetworkError;
                d.GithubMessage = ex.Message;
                return d;
            }
        }

        // ---- testable pure helpers ----

        internal static GitHubCreateRepoResult ParseCreateResponse(int status, string bodyText)
        {
            GitHubCreateRepoResult r = new GitHubCreateRepoResult();
            JObject obj;
            try { obj = JObject.Parse(bodyText); }
            catch { r.ErrorMessage = "Invalid JSON response (status " + status + ")"; return r; }

            if (status != 201)
            {
                string msg = (string)obj["message"];
                r.ErrorMessage = string.IsNullOrEmpty(msg) ? "HTTP " + status : msg;
                return r;
            }
            r.Ok = true;
            r.FullName = (string)obj["full_name"] ?? "";
            r.HtmlUrl = (string)obj["html_url"] ?? "";
            r.CloneUrlHttps = (string)obj["clone_url"] ?? "";
            r.SshUrl = (string)obj["ssh_url"] ?? "";
            return r;
        }

        internal static GitHubRepoDiagnosis.DiagResult StatusToDiagnosis(int status)
        {
            switch (status)
            {
                case 200: return GitHubRepoDiagnosis.DiagResult.Accessible;
                case 401: return GitHubRepoDiagnosis.DiagResult.InvalidToken;
                case 403: return GitHubRepoDiagnosis.DiagResult.InsufficientScope;
                case 404: return GitHubRepoDiagnosis.DiagResult.NotFoundOrNoAccess;
                default: return GitHubRepoDiagnosis.DiagResult.Other;
            }
        }

        internal static string ExtractMessage(string bodyText)
        {
            try
            {
                JObject obj = JObject.Parse(bodyText);
                return (string)obj["message"] ?? "";
            }
            catch { return ""; }
        }

        private static void ApplyHeaders(HttpRequestMessage req, string pat)
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            req.Headers.TryAddWithoutValidation("User-Agent", "HelixStudio");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + pat);
        }

        private static string FirstHeader(HttpResponseMessage resp, string name)
        {
            System.Collections.Generic.IEnumerable<string> vals;
            if (resp.Headers.TryGetValues(name, out vals))
                foreach (string v in vals) return v;
            return "";
        }
    }
}
