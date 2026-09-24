using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitServerNet.Enterprise
{
	/// <summary>
	/// Autodesk Revit Server client assemblies loaded from one directory.
	/// </summary>
	internal sealed class RsAssemblies
	{
		private readonly Assembly[] _assemblies;

		public RsAssemblies(string directory, IEnumerable<Assembly> assemblies)
		{
			Directory = directory;
			_assemblies = assemblies.ToArray();
		}

		/// <summary>
		/// Full path of the directory the assemblies were loaded from.
		/// </summary>
		public string Directory { get; }

		/// <summary>
		/// Candidates the search skipped before choosing <see cref="Directory"/>, each with the reason; empty when it was the first candidate.
		/// </summary>
		public IReadOnlyList<string> SkippedCandidates { get; internal set; } = new string[0];

		/// <summary>
		/// Returns the type with the given full name from the loaded assemblies.
		/// </summary>
		/// <exception cref="TypeLoadException">No loaded assembly defines the type.</exception>
		public Type GetRequiredType(string fullName)
		{
			foreach (var asm in _assemblies)
			{
				var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
				if (t != null) return t;
			}
			throw new TypeLoadException($"Type '{fullName}' was not found in the Revit Server client assemblies loaded from '{Directory}'.");
		}
	}

	/// <summary>
	/// Directory chosen by the search, and the candidates skipped before it (each with the reason).
	/// </summary>
	internal sealed class RsAssemblyLocation
	{
		public RsAssemblyLocation(string directory, IReadOnlyList<string> skipped)
		{
			Directory = directory;
			Skipped = skipped ?? new string[0];
		}

		public string Directory { get; }

		public IReadOnlyList<string> Skipped { get; }
	}

	/// <summary>
	/// Finds and loads the Autodesk Revit Server client assemblies used by the direct export.
	/// </summary>
	/// <remarks>
	/// Search order (the first directory that directly contains all <see cref="RequiredFiles"/> wins, subfolders are never scanned):
	/// 1. <c>AssembliesPath</c>, when that directory exists (a path that does not exist is ignored);
	/// 2. <c>&lt;AppContext.BaseDirectory&gt;\RSAssemblies</c>;
	/// 3. <c>&lt;directory of RevitServerNet.dll&gt;\RSAssemblies</c>;
	/// 4. <c>&lt;directory of the process executable&gt;\RSAssemblies</c> (.NET 6 and later);
	/// 5. <c>&lt;Program Files&gt;\Autodesk\Revit {year}\RevitServerToolCommand</c>.
	/// The assemblies are loaded once per process. A set found by the automatic search (no existing AssembliesPath) is reused by every later
	/// export without an existing AssembliesPath, whatever its year. A later export that resolves to another directory fails with
	/// <see cref="InvalidOperationException"/>: two sets of these assemblies (same simple names) cannot be used in one process.
	/// </remarks>
	internal static class RsAssemblyLoader
	{
		/// <summary>
		/// Name of the bundled assemblies folder (copied next to the application by the NuGet package).
		/// </summary>
		internal const string BundledFolderName = "RSAssemblies";

		/// <summary>
		/// Assemblies loaded explicitly, in this order (not strong-named: one copy per process).
		/// </summary>
		internal static readonly string[] LoadedFiles =
		{
			"Autodesk.RevitServer.Social.dll",
			"RS.Enterprise.Common.ClientServer.DataContract.dll",
			"RS.Enterprise.Common.ClientServer.ServiceContract.Model.dll",
			"RS.Enterprise.Common.ClientServer.Helper.dll",
		};

		/// <summary>
		/// Files a directory must contain (directly) to be used: <see cref="LoadedFiles"/> and their Castle dependencies
		/// (strong-named, loaded on demand through the resolve handler; the export fails without them).
		/// </summary>
		internal static readonly string[] RequiredFiles = LoadedFiles.Concat(new[] { "Castle.Core.dll", "Castle.Windsor.dll" }).ToArray();

		private static readonly RsAssemblyBinding Binding = new RsAssemblyBinding(LoadDirectory);
		private static readonly object ResolveGate = new object();
		private static string _resolveDirectory;
		private static bool _resolveHandlerRegistered;

		/// <summary>
		/// Finds the assemblies directory for an export and loads the assemblies (once per process).
		/// </summary>
		/// <exception cref="InvalidOperationException">No directory qualifies, or the process already uses another directory.</exception>
		public static RsAssemblies Load(string revitVersion, string assembliesPath)
		{
			return Load(revitVersion, assembliesPath, Binding, AppContext.BaseDirectory, GetLibraryDirectory(), GetProcessDirectory(), GetProgramFilesDirectory());
		}

		/// <summary>
		/// <see cref="Load(string, string)"/> with the binding and the searched directories given.
		/// </summary>
		internal static RsAssemblies Load(string revitVersion, string assembliesPath, RsAssemblyBinding binding,
			string appBaseDirectory, string libraryDirectory, string processDirectory, string programFilesDirectory)
		{
			// Without an existing AssembliesPath the directory comes from the automatic search, where only the last candidate depends on the year:
			// such a call reuses a set that the automatic search found earlier, so that exports of different years share it.
			var automatic = string.IsNullOrWhiteSpace(assembliesPath) || !System.IO.Directory.Exists(assembliesPath);
			return binding.GetOrLoad(() =>
			{
				var candidates = GetCandidateDirectories(revitVersion, assembliesPath, appBaseDirectory, libraryDirectory, processDirectory, programFilesDirectory);
				var location = Locate(candidates, assembliesPath);
				if (location.Skipped.Count > 0)
					Trace.TraceWarning($"RevitServerNet: using the Revit Server client assemblies in '{location.Directory}'{DescribeSkipped(location.Skipped)}.");
				return location;
			}, automatic);
		}

		/// <summary>
		/// Returns the first qualifying candidate and the candidates skipped before it.
		/// </summary>
		/// <exception cref="InvalidOperationException">No candidate qualifies; the message lists every searched path.</exception>
		internal static RsAssemblyLocation Locate(IReadOnlyList<string> candidates, string assembliesPath)
		{
			var searched = new List<string>();
			var directory = FindDirectory(candidates, searched);
			if (directory != null) return new RsAssemblyLocation(directory, searched);
			var ignored = !string.IsNullOrWhiteSpace(assembliesPath) && !System.IO.Directory.Exists(assembliesPath)
				? $" AssembliesPath '{assembliesPath}' does not exist and was ignored."
				: string.Empty;
			throw new InvalidOperationException(
				"Revit Server client assemblies not found. A directory is used only when it directly contains "
				+ string.Join(", ", RequiredFiles) + " (subfolders are not searched)." + ignored
				+ " Searched: " + (searched.Count == 0 ? "nothing" : string.Join("; ", searched)) + ".");
		}

		/// <summary>
		/// " (skipped before it: ...)" for messages, or an empty string.
		/// </summary>
		internal static string DescribeSkipped(IReadOnlyList<string> skipped)
		{
			return skipped == null || skipped.Count == 0 ? string.Empty : " (skipped before it: " + string.Join("; ", skipped) + ")";
		}

		/// <summary>
		/// Candidate directories in search order, without duplicates.
		/// </summary>
		internal static IReadOnlyList<string> GetCandidateDirectories(string revitVersion, string assembliesPath, string appBaseDirectory, string libraryDirectory,
			string processDirectory, string programFilesDirectory)
		{
			var result = new List<string>();
			void Add(string dir)
			{
				var normalized = NormalizeDirectory(dir);
				if (!result.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
					result.Add(normalized);
			}

			if (!string.IsNullOrWhiteSpace(assembliesPath) && System.IO.Directory.Exists(assembliesPath))
				Add(assembliesPath);
			if (!string.IsNullOrWhiteSpace(appBaseDirectory))
				Add(Path.Combine(appBaseDirectory, BundledFolderName));
			if (!string.IsNullOrWhiteSpace(libraryDirectory))
				Add(Path.Combine(libraryDirectory, BundledFolderName));
			if (!string.IsNullOrWhiteSpace(processDirectory))
				Add(Path.Combine(processDirectory, BundledFolderName));
			if (!string.IsNullOrWhiteSpace(programFilesDirectory) && !string.IsNullOrWhiteSpace(revitVersion))
				Add(Path.Combine(programFilesDirectory, "Autodesk", "Revit " + revitVersion.Trim(), "RevitServerToolCommand"));
			return result;
		}

		/// <summary>
		/// Longest file path from which .NET Framework loads an assembly (MAX_PATH minus the terminating null).
		/// </summary>
		internal const int NetFrameworkMaxPath = 259;

		/// <summary>
		/// Returns the first candidate that directly contains all <see cref="RequiredFiles"/>, or null.
		/// Adds a line per checked candidate to <paramref name="searched"/>.
		/// </summary>
		internal static string FindDirectory(IEnumerable<string> candidates, List<string> searched)
		{
			foreach (var dir in candidates)
			{
				if (!System.IO.Directory.Exists(dir))
				{
					searched.Add($"'{dir}' (does not exist)");
					continue;
				}
				var missing = new List<string>();
				var tooLong = new List<string>();
				foreach (var file in RequiredFiles)
				{
					var path = Path.Combine(dir, file);
					if (IsTooLongToLoad(path)) tooLong.Add($"{file} ({path.Length} characters)");
					else if (!File.Exists(path)) missing.Add(file);
				}
				if (missing.Count == 0 && tooLong.Count == 0) return dir;
				var reasons = new List<string>();
				if (missing.Count > 0) reasons.Add("missing " + string.Join(", ", missing));
				if (tooLong.Count > 0)
					reasons.Add($"path longer than {NetFrameworkMaxPath} characters, from which .NET Framework cannot load an assembly: " + string.Join(", ", tooLong));
				searched.Add($"'{dir}' ({string.Join("; ", reasons)})");
			}
			return null;
		}

		/// <summary>
		/// True on .NET Framework when <paramref name="path"/> is longer than <see cref="NetFrameworkMaxPath"/> characters:
		/// Assembly.LoadFrom fails for such a file even in a long-path-aware process (where File.Exists does find it), and in other
		/// processes File.Exists returns false for it although it exists. Always false on .NET (Core), which loads from long paths.
		/// </summary>
		internal static bool IsTooLongToLoad(string path)
		{
#if NETFRAMEWORK
			return path.Length > NetFrameworkMaxPath;
#else
			return false;
#endif
		}

		/// <summary>
		/// Full path without a trailing separator (except for a drive root).
		/// </summary>
		internal static string NormalizeDirectory(string directory)
		{
			var full = Path.GetFullPath(directory);
			var root = Path.GetPathRoot(full) ?? string.Empty;
			if (full.Length > root.Length)
				full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return full;
		}

		private static string GetLibraryDirectory()
		{
			// Location is empty when the library is loaded from memory (for example a single-file app).
			var location = typeof(RsAssemblyLoader).Assembly.Location;
			return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
		}

		/// <summary>
		/// Directory of the process executable (.NET 6 and later). In a single-file app that extracts its content to a temp folder
		/// (IncludeAllContentForSelfExtract), AppContext.BaseDirectory and the library's folder are that temp folder, while RSAssemblies
		/// (excluded from the bundle) stays next to the executable.
		/// </summary>
		private static string GetProcessDirectory()
		{
#if NET
			var processPath = Environment.ProcessPath;
			return string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
#else
			return null;
#endif
		}

		private static string GetProgramFilesDirectory()
		{
			// Revit is 64-bit: in a 32-bit process SpecialFolder.ProgramFiles is "Program Files (x86)".
			var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
			return !string.IsNullOrWhiteSpace(programW6432)
				? programW6432
				: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		}

		private static RsAssemblies LoadDirectory(string directory)
		{
			RegisterResolveHandler(directory);
			var loaded = new List<Assembly>();
			foreach (var file in LoadedFiles)
			{
				var path = Path.Combine(directory, file);
				var name = AssemblyName.GetAssemblyName(path);
				var existing = AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
				{
					var existingLocation = existing.IsDynamic ? string.Empty : existing.Location;
					if (string.IsNullOrEmpty(existingLocation) || !string.Equals(Path.GetFullPath(existingLocation), path, StringComparison.OrdinalIgnoreCase))
						throw new InvalidOperationException(
							$"Assembly '{existing.FullName}' is already loaded in this process from '{(string.IsNullOrEmpty(existingLocation) ? "<no file>" : existingLocation)}'; "
							+ $"the direct export cannot load another copy from '{path}'. Only one set of Revit Server client assemblies can be used per process.");
					loaded.Add(existing);
					continue;
				}
				loaded.Add(Assembly.LoadFrom(path));
			}
			return new RsAssemblies(directory, loaded);
		}

		private static void RegisterResolveHandler(string directory)
		{
			lock (ResolveGate)
			{
				_resolveDirectory = directory;
				if (_resolveHandlerRegistered) return;
				AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
				_resolveHandlerRegistered = true;
			}
		}

		/// <summary>
		/// Resolves dependencies of the Autodesk assemblies (for example Castle.Core) by simple name from the loaded directory.
		/// Only a file whose identity matches the request (name, and version/public key token when requested) is returned.
		/// </summary>
		private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
		{
			string directory;
			lock (ResolveGate) directory = _resolveDirectory;
			if (directory == null) return null;

			var requested = new AssemblyName(args.Name);
			if (string.IsNullOrEmpty(requested.Name)) return null;
			var path = Path.Combine(directory, requested.Name + ".dll");
			if (!File.Exists(path)) return null;

			var candidate = AssemblyName.GetAssemblyName(path);
			if (!string.Equals(candidate.Name, requested.Name, StringComparison.OrdinalIgnoreCase)) return null;
			if (requested.Version != null && requested.Version != candidate.Version) return null;
			var requestedToken = requested.GetPublicKeyToken();
			if (requestedToken != null && requestedToken.Length > 0)
			{
				var candidateToken = candidate.GetPublicKeyToken() ?? new byte[0];
				if (!requestedToken.SequenceEqual(candidateToken)) return null;
			}
			return Assembly.LoadFrom(path);
		}
	}

	/// <summary>
	/// Binds the process to one assemblies directory and loads it once (thread-safe).
	/// </summary>
	internal sealed class RsAssemblyBinding
	{
		private readonly object _gate = new object();
		private readonly Func<string, RsAssemblies> _load;
		private string _directory;
		private bool _automatic;
		private IReadOnlyList<string> _skipped = new string[0];
		private RsAssemblies _assemblies;

		public RsAssemblyBinding(Func<string, RsAssemblies> load)
		{
			_load = load ?? throw new ArgumentNullException(nameof(load));
		}

		/// <summary>
		/// Directory this binding is bound to, or null before the first call to <see cref="GetOrLoad(string)"/>.
		/// </summary>
		public string BoundDirectory
		{
			get { lock (_gate) return _directory; }
		}

		/// <summary>
		/// Loads the assemblies from <paramref name="directory"/> on the first call and returns the same instance afterwards
		/// (as a call with an explicit AssembliesPath).
		/// </summary>
		/// <exception cref="InvalidOperationException">The binding is already bound to another directory.</exception>
		public RsAssemblies GetOrLoad(string directory)
		{
			return GetOrLoad(() => new RsAssemblyLocation(directory, null), automatic: false);
		}

		/// <summary>
		/// Loads the assemblies from the directory <paramref name="locate"/> returns on the first call and returns the same instance afterwards.
		/// </summary>
		/// <param name="locate">Searches the directory; not called when an automatic call reuses a set found by the automatic search.</param>
		/// <param name="automatic">True when the call has no existing AssembliesPath (the directory comes from the automatic search).</param>
		/// <exception cref="InvalidOperationException">The binding is already bound to another directory.</exception>
		public RsAssemblies GetOrLoad(Func<RsAssemblyLocation> locate, bool automatic)
		{
			lock (_gate)
			{
				if (automatic && _directory != null && _automatic)
					return LoadBound();

				var location = locate();
				var normalized = RsAssemblyLoader.NormalizeDirectory(location.Directory);
				if (_directory != null && !string.Equals(_directory, normalized, StringComparison.OrdinalIgnoreCase))
				{
					var skipped = RsAssemblyLoader.DescribeSkipped(location.Skipped);
					throw new InvalidOperationException(automatic
						? $"This process already uses the Revit Server client assemblies from '{_directory}', set by the AssembliesPath of an earlier export, "
							+ $"and cannot switch to '{normalized}', which the automatic search found for this export{skipped}: "
							+ "only one set of these assemblies can be loaded per process. Run exports that need another set in a separate process."
						: $"This process already uses the Revit Server client assemblies from '{_directory}'"
							+ (_automatic ? ", found by the automatic search of an earlier export," : string.Empty)
							+ $" and cannot switch to '{normalized}'{skipped}: only one set of these assemblies can be loaded per process. "
							+ "Use the same AssembliesPath for every export (or none), or run exports that need another set in a separate process.");
				}
				// Bound before loading: a failed load may leave some assemblies of this directory in the process.
				if (_directory == null)
				{
					_directory = normalized;
					_automatic = automatic;
					_skipped = location.Skipped;
				}
				return LoadBound();
			}
		}

		private RsAssemblies LoadBound()
		{
			if (_assemblies == null)
			{
				var assemblies = _load(_directory);
				assemblies.SkippedCandidates = _skipped;
				_assemblies = assemblies;
			}
			return _assemblies;
		}
	}
}
