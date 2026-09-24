using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitServerNet.Enterprise;
using Xunit;

namespace RevitServerNet.Tests
{
    /// <summary>
    /// Search order, the non-recursive directory rule and the one-set-per-process binding of the RS assemblies loader.
    /// </summary>
    public class RsAssemblyLoaderTests
    {
        private static void CreateFakeSet(string directory, params string[] except)
        {
            Directory.CreateDirectory(directory);
            foreach (var file in RsAssemblyLoader.RequiredFiles.Except(except))
                File.WriteAllBytes(Path.Combine(directory, file), new byte[0]);
        }

        [Fact]
        public void RequiredFiles_AreTheExportPathAssemblies()
        {
            Assert.Equal(
                new[]
                {
                    "Autodesk.RevitServer.Social.dll",
                    "RS.Enterprise.Common.ClientServer.DataContract.dll",
                    "RS.Enterprise.Common.ClientServer.ServiceContract.Model.dll",
                    "RS.Enterprise.Common.ClientServer.Helper.dll",
                    "Castle.Core.dll",
                    "Castle.Windsor.dll",
                },
                RsAssemblyLoader.RequiredFiles);
        }

        [Fact]
        public void SearchOrder_IsAssembliesPathAppBaseLibraryProcessThenProgramFiles_WithoutYearSubfolders()
        {
            using (var temp = new TempDirectory())
            {
                var custom = temp.Create("custom");
                // Full sets in year subfolders (1.2.x left bin\RSAssemblies\{year} behind) must never become candidates.
                CreateFakeSet(Path.Combine(custom, "2024"));
                CreateFakeSet(temp.Path(@"app\RSAssemblies"));
                CreateFakeSet(temp.Path(@"app\RSAssemblies\2024"));
                CreateFakeSet(temp.Path(@"lib\RSAssemblies\2024"));
                CreateFakeSet(temp.Path(@"proc\RSAssemblies\2024"));

                var candidates = RsAssemblyLoader.GetCandidateDirectories("2024", custom, temp.Path("app"), temp.Path("lib"), temp.Path("proc"), temp.Path("pf"));

                Assert.Equal(
                    new[]
                    {
                        custom,
                        temp.Path(@"app\RSAssemblies"),
                        temp.Path(@"lib\RSAssemblies"),
                        temp.Path(@"proc\RSAssemblies"),
                        temp.Path(@"pf\Autodesk\Revit 2024\RevitServerToolCommand"),
                    },
                    candidates);
            }
        }

        [Fact]
        public void SearchOrder_MissingAssembliesPathIsIgnored_DuplicatesAreListedOnce()
        {
            using (var temp = new TempDirectory())
            {
                var candidates = RsAssemblyLoader.GetCandidateDirectories("2022", temp.Path("does-not-exist"), temp.Path("app"), temp.Path(@"app\"), temp.Path("app"), null);

                Assert.Equal(new[] { temp.Path(@"app\RSAssemblies") }, candidates);
            }
        }

        [Fact]
        public void Load_SetFoundByTheAutomaticSearch_IsReusedForOtherYears()
        {
            using (var temp = new TempDirectory())
            {
                var pf = temp.Path("pf");
                var set2024 = Path.Combine(pf, "Autodesk", "Revit 2024", "RevitServerToolCommand");
                CreateFakeSet(set2024);
                CreateFakeSet(Path.Combine(pf, "Autodesk", "Revit 2022", "RevitServerToolCommand"));
                var loads = 0;
                var binding = new RsAssemblyBinding(d => { loads++; return new RsAssemblies(d, new System.Reflection.Assembly[0]); });

                var first = RsAssemblyLoader.Load("2024", null, binding, temp.Path("app"), temp.Path("lib"), null, pf);
                var second = RsAssemblyLoader.Load("2022", null, binding, temp.Path("app"), temp.Path("lib"), null, pf);
                // No set at all for this year, and an AssembliesPath that does not exist: still the set found first.
                var third = RsAssemblyLoader.Load("2031", temp.Path("not-there"), binding, temp.Path("app"), temp.Path("lib"), null, pf);

                Assert.Equal(set2024, first.Directory);
                Assert.Same(first, second);
                Assert.Same(first, third);
                Assert.Equal(1, loads);
                // Kept for error messages: why the bundled folders were not used.
                Assert.Contains(first.SkippedCandidates, line => line.Contains(temp.Path(@"app\RSAssemblies")) && line.Contains("does not exist"));
            }
        }

        [Fact]
        public void Load_AutomaticSearchAfterAnExplicitAssembliesPath_MismatchNamesTheSkippedCandidates()
        {
            using (var temp = new TempDirectory())
            {
                var custom = temp.Path("custom");
                CreateFakeSet(custom);
                var app = temp.Path("app");
                CreateFakeSet(Path.Combine(app, "RSAssemblies"), "Castle.Windsor.dll");
                var pf = temp.Path("pf");
                var set2024 = Path.Combine(pf, "Autodesk", "Revit 2024", "RevitServerToolCommand");
                CreateFakeSet(set2024);
                var binding = new RsAssemblyBinding(d => new RsAssemblies(d, new System.Reflection.Assembly[0]));
                RsAssemblyLoader.Load("2024", custom, binding, app, null, null, pf);

                var ex = Assert.Throws<InvalidOperationException>(() => RsAssemblyLoader.Load("2024", null, binding, app, null, null, pf));

                Assert.Contains(custom, ex.Message);
                Assert.Contains(set2024, ex.Message);
                Assert.Contains("automatic search", ex.Message);
                Assert.Contains("missing Castle.Windsor.dll", ex.Message);
                // This export gave no AssembliesPath: the message does not tell it to use the same one.
                Assert.DoesNotContain("Use the same AssembliesPath", ex.Message);
            }
        }

        [Fact]
        public void Load_ExplicitAssembliesPathAfterTheAutomaticSearch_IsRejected()
        {
            using (var temp = new TempDirectory())
            {
                var app = temp.Path("app");
                CreateFakeSet(Path.Combine(app, "RSAssemblies"));
                var custom = temp.Path("custom");
                CreateFakeSet(custom);
                var binding = new RsAssemblyBinding(d => new RsAssemblies(d, new System.Reflection.Assembly[0]));
                RsAssemblyLoader.Load("2024", null, binding, app, null, null, null);

                var ex = Assert.Throws<InvalidOperationException>(() => RsAssemblyLoader.Load("2024", custom, binding, app, null, null, null));

                Assert.Contains("found by the automatic search of an earlier export", ex.Message);
                Assert.Contains("Use the same AssembliesPath for every export (or none)", ex.Message);
            }
        }

        [Fact]
        public void FindDirectory_UsesOnlyFilesDirectlyInTheDirectory()
        {
            using (var temp = new TempDirectory())
            {
                var parent = temp.Create("parent");
                CreateFakeSet(Path.Combine(parent, "2024"));
                var flat = temp.Path("flat");
                CreateFakeSet(flat);
                var searched = new List<string>();

                var found = RsAssemblyLoader.FindDirectory(new[] { parent, flat }, searched);

                Assert.Equal(flat, found);
                var line = Assert.Single(searched);
                Assert.Contains(parent, line);
                Assert.Contains("missing", line);
            }
        }

        [Fact]
        public void FindDirectory_DirectoryWithoutOneRequiredFile_IsSkipped()
        {
            using (var temp = new TempDirectory())
            {
                var incomplete = temp.Path("incomplete");
                CreateFakeSet(incomplete, "Castle.Windsor.dll");
                var complete = temp.Path("complete");
                CreateFakeSet(complete);
                var searched = new List<string>();

                var found = RsAssemblyLoader.FindDirectory(new[] { incomplete, complete }, searched);

                Assert.Equal(complete, found);
                Assert.Contains("Castle.Windsor.dll", Assert.Single(searched));
            }
        }

        /// <summary>
        /// .NET Framework cannot load an assembly from a path longer than 259 characters (in a long-path-aware process File.Exists finds
        /// the file, but Assembly.LoadFrom still fails): such a directory is skipped with that reason, not reported as "missing".
        /// .NET (Core) loads from long paths.
        /// </summary>
        [Theory]
        [InlineData(259)]
        [InlineData(260)]
        public void FindDirectory_FilePathLongerThanNetFrameworkCanLoad_IsSkippedWithThatReason(int longestPathLength)
        {
            // The longest required file name gets a path of longestPathLength characters; the other files have shorter paths.
            const string longest = "RS.Enterprise.Common.ClientServer.ServiceContract.Model.dll";
            Assert.Equal(longest.Length, RsAssemblyLoader.RequiredFiles.Max(f => f.Length));
            using (var temp = new TempDirectory())
            {
                var dirLength = longestPathLength - longest.Length - 1;
                Assert.True(temp.Root.Length + 2 <= dirLength, "the temp folder is too deep for this test");
                var dir = temp.Root + @"\" + new string('d', dirLength - temp.Root.Length - 1);
                Directory.CreateDirectory(dir);
                var longPath = Path.Combine(dir, longest);
                Assert.Equal(longestPathLength, longPath.Length);
                // Written through the extended-length form: .NET Framework cannot create a 260-character path otherwise.
                var longPathExtended = @"\\?\" + longPath;
                foreach (var file in RsAssemblyLoader.RequiredFiles)
                    File.WriteAllBytes(file == longest ? longPathExtended : Path.Combine(dir, file), new byte[0]);
                try
                {
                    var searched = new List<string>();

                    var found = RsAssemblyLoader.FindDirectory(new[] { dir }, searched);

#if NETFRAMEWORK
                    if (longestPathLength > RsAssemblyLoader.NetFrameworkMaxPath)
                    {
                        Assert.Null(found);
                        var line = Assert.Single(searched);
                        Assert.Contains($"path longer than {RsAssemblyLoader.NetFrameworkMaxPath} characters", line);
                        Assert.Contains($"{longest} ({longestPathLength} characters)", line);
                        Assert.DoesNotContain("missing", line);
                        return;
                    }
#endif
                    Assert.Equal(dir, found);
                    Assert.Empty(searched);
                }
                finally
                {
                    File.Delete(longPathExtended);
                }
            }
        }

        [Fact]
        public void FindDirectory_FirstQualifyingCandidateWins()
        {
            using (var temp = new TempDirectory())
            {
                var first = temp.Path("first");
                var second = temp.Path("second");
                CreateFakeSet(first);
                CreateFakeSet(second);

                Assert.Equal(first, RsAssemblyLoader.FindDirectory(new[] { first, second }, new List<string>()));
            }
        }

        [Fact]
        public void Locate_NothingQualifies_ListsEverySearchedPath()
        {
            using (var temp = new TempDirectory())
            {
                var missing = temp.Path("missing");
                var nested = temp.Create("nested");
                CreateFakeSet(Path.Combine(nested, "2022"));
                var assembliesPath = temp.Path("not-there");

                var ex = Assert.Throws<InvalidOperationException>(() => RsAssemblyLoader.Locate(new[] { missing, nested }, assembliesPath));

                Assert.Contains($"'{missing}' (does not exist)", ex.Message);
                Assert.Contains($"'{nested}' (missing ", ex.Message);
                Assert.Contains($"AssembliesPath '{assembliesPath}' does not exist and was ignored", ex.Message);
                Assert.Contains("subfolders are not searched", ex.Message);
            }
        }

        [Fact]
        public void Binding_LoadsOncePerDirectory()
        {
            using (var temp = new TempDirectory())
            {
                var loads = 0;
                var binding = new RsAssemblyBinding(d => { loads++; return new RsAssemblies(d, new System.Reflection.Assembly[0]); });
                var dir = temp.Create("set");

                var first = binding.GetOrLoad(dir);
                var second = binding.GetOrLoad(dir + @"\");
                var third = binding.GetOrLoad(dir.ToUpperInvariant());

                Assert.Equal(1, loads);
                Assert.Same(first, second);
                Assert.Same(first, third);
                Assert.Equal(dir, binding.BoundDirectory);
            }
        }

        [Fact]
        public void Binding_AnotherDirectory_IsRejected()
        {
            using (var temp = new TempDirectory())
            {
                var loads = 0;
                var binding = new RsAssemblyBinding(d => { loads++; return new RsAssemblies(d, new System.Reflection.Assembly[0]); });
                var a = temp.Create("a");
                var b = temp.Create("b");
                binding.GetOrLoad(a);

                var ex = Assert.Throws<InvalidOperationException>(() => binding.GetOrLoad(b));

                Assert.Contains(a, ex.Message);
                Assert.Contains(b, ex.Message);
                Assert.Equal(1, loads);
                Assert.Equal(a, binding.BoundDirectory);
            }
        }

        [Fact]
        public void Binding_ConcurrentCalls_LoadOnce()
        {
            using (var temp = new TempDirectory())
            {
                var loads = 0;
                var binding = new RsAssemblyBinding(d =>
                {
                    Interlocked.Increment(ref loads);
                    Thread.Sleep(50);
                    return new RsAssemblies(d, new System.Reflection.Assembly[0]);
                });
                var dir = temp.Create("set");

                var results = new RsAssemblies[16];
                Parallel.For(0, results.Length, i => results[i] = binding.GetOrLoad(dir));

                Assert.Equal(1, loads);
                Assert.All(results, r => Assert.Same(results[0], r));
            }
        }

        [Fact]
        public void Binding_FailedLoad_StaysBoundToItsDirectoryAndRetries()
        {
            using (var temp = new TempDirectory())
            {
                var loads = 0;
                var binding = new RsAssemblyBinding(d =>
                {
                    if (++loads == 1) throw new FileLoadException("first load fails");
                    return new RsAssemblies(d, new System.Reflection.Assembly[0]);
                });
                var a = temp.Create("a");
                var b = temp.Create("b");

                Assert.Throws<FileLoadException>(() => binding.GetOrLoad(a));
                Assert.Throws<InvalidOperationException>(() => binding.GetOrLoad(b));
                Assert.NotNull(binding.GetOrLoad(a));
                Assert.Equal(2, loads);
            }
        }

        [Fact]
        public void BundledSet_IsFoundNextToTheApplication_AndMatchesTheExportCode()
        {
            var set = RsAssemblyLoader.Load("2022", null);

            Assert.Equal(RsAssemblyLoader.NormalizeDirectory(Path.Combine(AppContext.BaseDirectory, "RSAssemblies")), set.Directory);
            // Resolves every Autodesk type and member the export uses; throws if the set does not match.
            var api = RsModelServiceApi.For(set);
            Assert.Equal("IModelService", api.ModelServiceType.Name);
            Assert.Same(set, RsAssemblyLoader.Load("2031", null));
        }

        [Fact]
        public void AnotherSetInTheSameProcess_IsRejected()
        {
            // The test process uses the bundled set (as every export in these tests does).
            var bundled = RsAssemblyLoader.Load("2022", null);
            using (var temp = new TempDirectory())
            {
                var copy = temp.Create("copy");
                foreach (var file in RsAssemblyLoader.RequiredFiles)
                    File.Copy(Path.Combine(bundled.Directory, file), Path.Combine(copy, file));

                var ex = Assert.Throws<InvalidOperationException>(() => RsAssemblyLoader.Load("2022", copy));

                Assert.Contains(bundled.Directory, ex.Message);
                Assert.Contains(copy, ex.Message);
                Assert.Contains("only one set", ex.Message);
            }
        }
    }
}
