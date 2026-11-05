namespace HelixPythonNamespace.Editor.Doc
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Fetches HTML docs pages with an on-disk cache. Port of the C++ <c>DocFetcher</c>
    /// (QNetworkAccessManager → <see cref="HttpClient"/>, Qt signals → .NET events). A
    /// cache hit serves immediately; a miss fetches over HTTP, caches, then raises Loaded.
    /// </summary>
    public sealed class DocFetcher
    {
        private static readonly HttpClient Http = CreateClient();
        private string _cacheDir;

        public event Action<string, string> Loaded;  // (url, html)
        public event Action<string, string> Failed;   // (url, message)

        public DocFetcher()
        {
            _cacheDir = DefaultCacheDir();
            TryCreateDir(_cacheDir);
        }

        public string DefaultCacheDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "HelixStudio", "doc_cache");
        }

        public void SetCacheDirForTesting(string dir)
        {
            _cacheDir = dir;
            TryCreateDir(_cacheDir);
        }

        internal string CachePathFor(string url)
        {
            string keyed = StripFragment(url);
            using (SHA1 sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(keyed));
                StringBuilder hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest) hex.Append(b.ToString("x2"));
                return Path.Combine(_cacheDir, hex.ToString() + ".html");
            }
        }

        internal bool ServeFromCache(string url)
        {
            string path = CachePathFor(url);
            if (!File.Exists(path)) return false;
            string html;
            try { html = File.ReadAllText(path); }
            catch { return false; }
            RaiseLoaded(url, html);
            return true;
        }

        internal void WriteCache(string url, string html)
        {
            try
            {
                TryCreateDir(_cacheDir);
                File.WriteAllText(CachePathFor(url), html);
            }
            catch { }
        }

        public void Fetch(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                RaiseFailed(url, "invalid URL");
                return;
            }
            if (ServeFromCache(url)) return;
            FetchOverHttp(url);
        }

        private async void FetchOverHttp(string url)
        {
            try
            {
                string html = await Http.GetStringAsync(url).ConfigureAwait(false);
                WriteCache(url, html);
                RaiseLoaded(url, html);
            }
            catch (Exception ex)
            {
                RaiseFailed(url, ex.Message);
            }
        }

        private void RaiseLoaded(string url, string html)
        {
            Action<string, string> h = Loaded;
            if (h != null) h(url, html);
        }

        private void RaiseFailed(string url, string message)
        {
            Action<string, string> h = Failed;
            if (h != null) h(url, message);
        }

        internal static string StripFragment(string url)
        {
            if (string.IsNullOrEmpty(url)) return url ?? "";
            int hash = url.IndexOf('#');
            return hash >= 0 ? url.Substring(0, hash) : url;
        }

        private static void TryCreateDir(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }

        private static HttpClient CreateClient()
        {
            HttpClient c = new HttpClient();
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HelixStudio/1.0 (docs)");
            return c;
        }
    }
}
