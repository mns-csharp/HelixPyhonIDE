namespace HelixPythonNamespace.Git
{
    using System;
    using System.Text;

    /// <summary>An owner/repo pair parsed from a GitHub URL.</summary>
    public sealed class OwnerRepo
    {
        public string Owner = "";
        public string Repo = "";
    }

    /// <summary>
    /// Pure URL helpers for HTTPS credential embedding and GitHub owner/repo parsing.
    /// Port of the C++ <c>git_credential_url_util</c>.
    /// </summary>
    public static class GitUrlUtil
    {
        public static bool IsHttpsUrl(string url)
        {
            return url != null && url.StartsWith("https://", StringComparison.Ordinal);
        }

        /// <summary>True if libgit2/WinHTTP error text indicates an HTTP 404 (any phrasing).</summary>
        public static bool LooksLikeStatus404(string errorText)
        {
            if (string.IsNullOrEmpty(errorText)) return false;
            return errorText.IndexOf("status 404", StringComparison.Ordinal) >= 0
                || errorText.IndexOf("status: 404", StringComparison.Ordinal) >= 0
                || errorText.IndexOf("status code: 404", StringComparison.Ordinal) >= 0
                || errorText.IndexOf("status code 404", StringComparison.Ordinal) >= 0;
        }

        public static string UrlPercentEncode(string input)
        {
            const string hex = "0123456789ABCDEF";
            StringBuilder outSb = new StringBuilder(input.Length);
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            foreach (byte bb in bytes)
            {
                char c = (char)bb;
                bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                                  (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
                if (unreserved) outSb.Append(c);
                else
                {
                    outSb.Append('%');
                    outSb.Append(hex[(bb >> 4) & 0xF]);
                    outSb.Append(hex[bb & 0xF]);
                }
            }
            return outSb.ToString();
        }

        /// <summary>
        /// Embed <paramref name="user"/>/<paramref name="pass"/> into an HTTPS URL's userinfo,
        /// stripping any pre-existing credentials. Empty user becomes GitHub's documented
        /// <c>x-access-token</c> placeholder so PAT-only Basic-auth isn't treated as anonymous.
        /// </summary>
        public static string EmbedCredentialsInHttpsUrl(string url, string user, string pass)
        {
            if (!IsHttpsUrl(url)) return url;
            const string scheme = "https://";
            string rest = url.Substring(scheme.Length);

            int at = rest.IndexOf('@');
            int slash = rest.IndexOf('/');
            if (at >= 0 && (slash < 0 || at < slash)) rest = rest.Substring(at + 1);

            string effectiveUser = string.IsNullOrEmpty(user) ? "x-access-token" : user;
            string authority = UrlPercentEncode(effectiveUser);
            if (!string.IsNullOrEmpty(pass)) authority += ":" + UrlPercentEncode(pass);
            authority += "@";
            return scheme + authority + rest;
        }

        /// <summary>Parse a github.com HTTPS URL into owner/repo, or null if not a plain GitHub repo URL.</summary>
        public static OwnerRepo ParseGitHubOwnerRepo(string url)
        {
            if (!IsHttpsUrl(url)) return null;
            const string scheme = "https://";
            string rest = url.Substring(scheme.Length);

            int at = rest.IndexOf('@');
            int slash = rest.IndexOf('/');
            if (at >= 0 && (slash < 0 || at < slash)) { rest = rest.Substring(at + 1); slash = rest.IndexOf('/'); }
            if (slash < 0) return null;

            string host = rest.Substring(0, slash);
            if (host != "github.com" && host != "www.github.com") return null;

            string path = rest.Substring(slash + 1);
            while (path.Length > 0 && path[path.Length - 1] == '/') path = path.Substring(0, path.Length - 1);
            const string dotGit = ".git";
            if (path.Length > dotGit.Length && path.EndsWith(dotGit, StringComparison.Ordinal))
                path = path.Substring(0, path.Length - dotGit.Length);

            int sep = path.IndexOf('/');
            if (sep < 0) return null;
            OwnerRepo r = new OwnerRepo { Owner = path.Substring(0, sep), Repo = path.Substring(sep + 1) };
            if (r.Owner.Length == 0 || r.Repo.Length == 0) return null;
            if (r.Repo.IndexOf('/') >= 0) return null;
            return r;
        }
    }
}
