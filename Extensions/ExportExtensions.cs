using System;
using System.Threading;
using System.Threading.Tasks;
using RevitServerNet.Tools;

namespace RevitServerNet.Extensions
{
    /// <summary>
    /// Convenience extensions to export models using RS libraries directly.
    /// </summary>
    public static class ExportExtensions
    {
        /// <summary>
        /// Export a model using server host and version inferred from the API client.
        /// </summary>
        public static Task<string> ExportModelAsync(this RevitServerApi api, string modelPath, string destinationFile, string assembliesPath = null, bool overwrite = false, IProgress<long> bytesProgress = null)
        {
            // createLocal: false keeps the behaviour of this signature before 1.3.0 (the file keeps the server's BasicFileInfo).
            return ExportModelAsync(api, modelPath, destinationFile, false, assembliesPath, overwrite, bytesProgress, CancellationToken.None);
        }

        /// <summary>
        /// Export a model using server host and version inferred from the API client.
        /// </summary>
        /// <param name="api">Client whose BaseUrl gives the host (without the HTTP port) and the year.</param>
        /// <param name="modelPath">Model path in pipe format or Windows-style relative path.</param>
        /// <param name="destinationFile">RVT file to create.</param>
        /// <param name="createLocal">Write the BasicFileInfo of a local copy, as RevitServerTool does (see <see cref="ModelExporterOptions.CreateLocal"/>).</param>
        /// <param name="assembliesPath">Optional directory with the Revit Server client assemblies, searched first; skipped when it does not contain
        /// all of them (see <see cref="ModelExporterOptions.AssembliesPath"/>).</param>
        /// <param name="overwrite">Replace an existing destination file.</param>
        /// <param name="bytesProgress">Bytes downloaded so far of the current model data file.</param>
        /// <param name="cancellationToken">Cancels the export.</param>
        public static async Task<string> ExportModelAsync(this RevitServerApi api, string modelPath, string destinationFile, bool createLocal, string assembliesPath = null, bool overwrite = false, IProgress<long> bytesProgress = null, CancellationToken cancellationToken = default)
        {
            var options = CreateOptions(api, modelPath, destinationFile, createLocal, assembliesPath, overwrite);
            return await ModelExporter.ExportAsync(options, bytesProgress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Export options for a client: host from BaseUrl without the HTTP port, year from BaseUrl.
        /// </summary>
        internal static ModelExporterOptions CreateOptions(RevitServerApi api, string modelPath, string destinationFile, bool createLocal, string assembliesPath, bool overwrite)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentException("Model path is required", nameof(modelPath));
            if (string.IsNullOrWhiteSpace(destinationFile)) throw new ArgumentException("Destination file is required", nameof(destinationFile));

            return new ModelExporterOptions
            {
                ServerHost = InferHostFromBaseUrl(api.BaseUrl),
                ModelPipePath = modelPath,
                DestinationFile = destinationFile,
                RevitVersion = InferVersionFromBaseUrl(api.BaseUrl),
                AssembliesPath = assembliesPath,
                Overwrite = overwrite,
                CreateLocal = createLocal,
            };
        }

        /// <summary>
        /// Year from RevitServerAdminRESTService{YEAR}; "2019" when BaseUrl has no year (a client created for 2012).
        /// </summary>
        internal static string InferVersionFromBaseUrl(string baseUrl)
        {
            return VersionUtils.ParseVersionFromBaseUrl(baseUrl) ?? "2019";
        }

        /// <summary>
        /// Host of BaseUrl without the HTTP port: the ModelService is reached over net.tcp on its own port.
        /// </summary>
        internal static string InferHostFromBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                throw new InvalidOperationException("Cannot infer server host from RevitServerApi.BaseUrl");
            return uri.Host;
        }
    }
}
