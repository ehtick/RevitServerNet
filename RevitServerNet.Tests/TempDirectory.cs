using System;
using System.IO;

namespace RevitServerNet.Tests
{
    /// <summary>
    /// Unique folder under %TEMP% for one test, deleted on dispose.
    /// </summary>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RsnTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        /// <summary>
        /// Path under the root (not created).
        /// </summary>
        public string Path(string relative) => System.IO.Path.Combine(Root, relative);

        /// <summary>
        /// Creates a folder under the root and returns its path.
        /// </summary>
        public string Create(string relative)
        {
            var path = Path(relative);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Deletes the folder. A file that was just written can stay locked for a moment (for example while an antivirus scans it):
        /// the delete is retried for about 1 s, and the last failure is thrown (a file still held after that is a handle leak).
        /// </summary>
        public void Dispose()
        {
            for (var attempt = 1; Directory.Exists(Root); attempt++)
            {
                try
                {
                    Directory.Delete(Root, true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
                {
                    System.Threading.Thread.Sleep(100 * attempt);
                }
            }
        }
    }
}
