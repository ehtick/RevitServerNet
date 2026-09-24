using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace RevitServerNet.Enterprise
{
	/// <summary>
	/// Access to the Autodesk types used by the direct export, through reflection over the loaded assemblies set.
	/// Nothing is compiled against a particular year's assemblies, so any set with the same contract can be used.
	/// All members are resolved when the instance is created, before any server call.
	/// </summary>
	internal sealed class RsModelServiceApi
	{
		private const string DataContractNs = "Autodesk.RevitServer.Enterprise.Common.ClientServer.DataContract.";
		private const string HelperNs = "Autodesk.RevitServer.Enterprise.Common.ClientServer.Helper.";

		/// <summary>
		/// Lock options passed to LockData by RevitServerTool (DataStorageToolClient.LockData): a non-exclusive read lock.
		/// </summary>
		internal const uint ReadLockOptions = 129u;

		private static readonly object CacheGate = new object();
		private static RsAssemblies _cachedFor;
		private static RsModelServiceApi _cached;

		private readonly string _directory;

		// Service contract
		private readonly MethodInfo _identifyModel;
		private readonly MethodInfo _lockData;
		private readonly MethodInfo _unlockData;
		private readonly MethodInfo _getListOfModelDataFiles;
		private readonly MethodInfo _modelDataFormatVersion;
		private readonly MethodInfo _downloadFile;
		private readonly FieldInfo _downloadResponseStream;

		// Data contracts
		private readonly ConstructorInfo _serviceSessionTokenCtor;
		private readonly ConstructorInfo _modelSessionTokenCtor;
		private readonly PropertyInfo _modelSessionTokenModelLocation;
		private readonly ConstructorInfo _modelLocationCtor;
		private readonly object _modelLocationTypeServer;
		private readonly ConstructorInfo _modelVersionCtor;
		private readonly ConstructorInfo _versionNumberCtor;
		private readonly ConstructorInfo _historyCheckInfoCtor;
		private readonly PropertyInfo _episodeGuidInvalid;
		private readonly PropertyInfo _modelIdentityGuid;
		private readonly MethodInfo _modelIdentityIsValid;
		private readonly PropertyInfo _modelIdentityNew;
		private readonly PropertyInfo _guidValueGuid;
		private readonly ConstructorInfo _downloadRequestCtor;

		// Helper
		private readonly ConstructorInfo _versionManagerCtor;
		private readonly PropertyInfo _versionManagerModelPathUtils;
		private readonly MethodInfo _getLatestStreamFiles;
		private readonly PropertyInfo _sharedUtilsInstance;
		private readonly PropertyInfo _sharedUtilsModelPathUtils;
		private readonly MethodInfo _generateRvtFile;
		private readonly ConstructorInfo _basicFileInfoStreamCtor;
		private readonly MethodInfo _readBasicFileInfo;
		private readonly MethodInfo _writeBasicFileInfo;
		private readonly PropertyInfo _bfiVersion;
		private readonly PropertyInfo _bfiWorksharingState;
		private readonly PropertyInfo _bfiCentralIdentity;
		private readonly PropertyInfo _bfiCentralPath;
		private readonly PropertyInfo _bfiIdentity;
		private readonly object _worksharingStateCreatedLocal;
		private readonly int _bfiStreamVersionMax;

		private RsModelServiceApi(RsAssemblies assemblies)
		{
			_directory = assemblies.Directory;
			Type T(string fullName) => assemblies.GetRequiredType(fullName);

			ModelServiceType = T("Autodesk.RevitServer.Enterprise.Common.ClientServer.ServiceContract.Model.IModelService");
			var serviceSessionToken = T(DataContractNs + "SessionToken.ServiceSessionToken");
			var modelSessionToken = T(DataContractNs + "SessionToken.ServiceModelSessionToken");
			var modelIdentity = T(DataContractNs + "Model.ModelIdentity");
			var modelLocation = T(DataContractNs + "Model.ModelLocation");
			var modelLocationType = T(DataContractNs + "Model.ModelLocationType");
			var modelVersion = T(DataContractNs + "Model.ModelVersion");
			var versionNumber = T(DataContractNs + "Model.VersionNumber");
			var historyCheckInfo = T(DataContractNs + "Model.ModelHistoryCheckInfo");
			var episodeGuid = T(DataContractNs + "Model.EpisodeGuid");
			var guidValue = T(DataContractNs + "Model.GUIDValue");
			DataFormatVersionType = T(DataContractNs + "Model.DataFormatVersion");
			var downloadRequest = T(DataContractNs + "Message.FileDownloadRequestMessage");

			_identifyModel = Method(ModelServiceType, "IdentifyModel", serviceSessionToken, typeof(string), typeof(bool));
			_lockData = Method(ModelServiceType, "LockData", modelSessionToken, typeof(uint), typeof(bool), modelVersion, episodeGuid.MakeByRefType());
			_unlockData = Method(ModelServiceType, "UnlockData", modelSessionToken);
			_getListOfModelDataFiles = Method(ModelServiceType, "GetListOfModelDataFilesWithoutLocking", modelSessionToken, typeof(ArrayList).MakeByRefType());
			_modelDataFormatVersion = Method(ModelServiceType, "ModelDataFormatVersion", modelSessionToken);
			_downloadFile = Method(ModelServiceType, "DownloadFile", downloadRequest);
			// FileDownloadMessageStream (message contract): the body is the public field Stream.
			_downloadResponseStream = _downloadFile.ReturnType.GetField("Stream", BindingFlags.Public | BindingFlags.Instance);
			if (_downloadResponseStream == null || !typeof(Stream).IsAssignableFrom(_downloadResponseStream.FieldType) || !typeof(IDisposable).IsAssignableFrom(_downloadFile.ReturnType))
				throw Mismatch($"{_downloadFile.ReturnType.FullName}.Stream (DownloadFile response: IDisposable with a public Stream field)");

			_serviceSessionTokenCtor = Ctor(serviceSessionToken, typeof(string), typeof(string), typeof(string), typeof(string));
			_modelSessionTokenCtor = Ctor(modelSessionToken, modelIdentity, typeof(string), typeof(string), typeof(string), typeof(string));
			_modelSessionTokenModelLocation = Property(modelSessionToken, "ModelLocation", BindingFlags.Instance);
			if (!_modelSessionTokenModelLocation.CanWrite)
				throw Mismatch($"{modelSessionToken.FullName}.ModelLocation setter");
			_modelLocationCtor = Ctor(modelLocation, typeof(string), typeof(string), modelLocationType);
			_modelLocationTypeServer = EnumValue(modelLocationType, "Server");
			_modelVersionCtor = Ctor(modelVersion, versionNumber, historyCheckInfo);
			_versionNumberCtor = Ctor(versionNumber, typeof(int));
			_historyCheckInfoCtor = Ctor(historyCheckInfo, episodeGuid);
			_episodeGuidInvalid = Property(episodeGuid, "Invalid", BindingFlags.Static);
			_modelIdentityGuid = Property(modelIdentity, "IdentityGUID", BindingFlags.Instance);
			_modelIdentityIsValid = Method(modelIdentity, "isValid");
			_modelIdentityNew = Property(modelIdentity, "NewModelIdentity", BindingFlags.Static);
			_guidValueGuid = Property(guidValue, "GUID", BindingFlags.Instance);
			_downloadRequestCtor = Ctor(downloadRequest, modelSessionToken, episodeGuid, typeof(string));

			var versionManager = T(HelperNs + "VersionManager.ModelDataVersionManager");
			var versionManagerInterface = T(HelperNs + "VersionManager.IModelDataVersionManager");
			var sharedUtils = T(HelperNs + "Utils.SharedUtils");
			var rvtFile = T(HelperNs + "ModelStorage.RvtFile");
			var basicFileInfoStream = T(HelperNs + "ModelStorage.ModelBasicFileInfoStream");
			var basicFileInfo = T(HelperNs + "ModelStorage.BasicFileInfo");
			var worksharingState = T(HelperNs + "ModelStorage.WorksharingState");
			var basicFileInfoStreamVersion = T(HelperNs + "ModelStorage.BasicFileInfoStreamVersion");
			var dictStringString = typeof(IDictionary<string, string>);
			var dictIntString = typeof(IDictionary<int, string>);

			_versionManagerCtor = Ctor(versionManager, typeof(string), DataFormatVersionType);
			_versionManagerModelPathUtils = Property(versionManagerInterface, "ModelPathUtils", BindingFlags.Instance);
			_getLatestStreamFiles = Method(versionManagerInterface, "GetLatestStreamFiles", dictStringString.MakeByRefType(), dictIntString.MakeByRefType(), dictIntString.MakeByRefType());
			_sharedUtilsInstance = Property(sharedUtils, "Instance", BindingFlags.Static);
			_sharedUtilsModelPathUtils = Property(sharedUtils, "ModelPathUtils", BindingFlags.Instance);
			_generateRvtFile = Method(rvtFile, "GenerateRvtFileFromModelFolder", dictStringString, dictIntString, dictIntString, DataFormatVersionType, typeof(string));
			_basicFileInfoStreamCtor = Ctor(basicFileInfoStream, DataFormatVersionType);
			_readBasicFileInfo = Method(basicFileInfoStream, "ReadFromRVTFile", typeof(string));
			_writeBasicFileInfo = Method(basicFileInfoStream, "WriteToRVTFile", typeof(string), basicFileInfo);
			_bfiVersion = Property(basicFileInfo, "Version", BindingFlags.Instance);
			_bfiWorksharingState = Property(basicFileInfo, "WorksharingState", BindingFlags.Instance);
			_bfiCentralIdentity = Property(basicFileInfo, "CentralIdentity", BindingFlags.Instance);
			_bfiCentralPath = Property(basicFileInfo, "CentralPath", BindingFlags.Instance);
			_bfiIdentity = Property(basicFileInfo, "Identity", BindingFlags.Instance);
			_worksharingStateCreatedLocal = EnumValue(worksharingState, "WS_CreatedLocal");
			_bfiStreamVersionMax = Convert.ToInt32(EnumValue(basicFileInfoStreamVersion, "BFISV_Max"));

			SkippedCandidates = assemblies.SkippedCandidates;
			var castleClassFactory = T("Autodesk.Social.Core.InversionOfControl.CastleClassFactory");
			CheckWindsorContainer(Property(castleClassFactory, "Instance", BindingFlags.Static), Method(castleClassFactory, "Release", typeof(object)));
		}

		/// <summary>
		/// IModelService of the loaded assemblies (service contract of the ChannelFactory).
		/// </summary>
		public Type ModelServiceType { get; }

		/// <summary>
		/// Directory of the loaded assemblies set.
		/// </summary>
		public string Directory => _directory;

		/// <summary>
		/// Candidates the assemblies search skipped before <see cref="Directory"/>, with the reasons (for error messages).
		/// </summary>
		public IReadOnlyList<string> SkippedCandidates { get; }

		/// <summary>
		/// DataFormatVersion enum of the loaded assemblies.
		/// </summary>
		public Type DataFormatVersionType { get; }

		/// <summary>
		/// Returns the instance for the loaded assemblies set (created once).
		/// </summary>
		public static RsModelServiceApi For(RsAssemblies assemblies)
		{
			if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
			lock (CacheGate)
			{
				if (!ReferenceEquals(_cachedFor, assemblies))
				{
					_cached = new RsModelServiceApi(assemblies);
					_cachedFor = assemblies;
				}
				return _cached;
			}
		}

		// ---- data contracts ----

		public object NewServiceSessionToken(string userName, string machineName)
		{
			return Invoke(_serviceSessionTokenCtor, userName, string.Empty, machineName, Guid.NewGuid().ToString());
		}

		/// <summary>
		/// New ServiceModelSessionToken (new operation GUID) with ModelLocation set, as RevitServerTool creates for every call.
		/// </summary>
		public object NewModelSessionToken(object modelIdentity, string userName, string machineName, object modelLocation)
		{
			var token = Invoke(_modelSessionTokenCtor, modelIdentity, userName, string.Empty, machineName, Guid.NewGuid().ToString());
			_modelSessionTokenModelLocation.SetValue(token, modelLocation);
			return token;
		}

		public object NewModelLocation(string serverHost, string relativePath)
		{
			return Invoke(_modelLocationCtor, serverHost, relativePath, _modelLocationTypeServer);
		}

		public Guid GetIdentityGuid(object modelIdentity)
		{
			var guidValue = _modelIdentityGuid.GetValue(modelIdentity);
			return guidValue == null ? Guid.Empty : (Guid)_guidValueGuid.GetValue(guidValue);
		}

		public bool IsValidIdentity(object modelIdentity)
		{
			return modelIdentity != null && (bool)Invoke(_modelIdentityIsValid, modelIdentity);
		}

		public object NewDownloadRequest(object modelSessionToken, object creationDate, string fileId)
		{
			return Invoke(_downloadRequestCtor, modelSessionToken, creationDate, fileId);
		}

		// ---- service calls (channel = IModelService proxy) ----

		public object IdentifyModel(object channel, object serviceSessionToken, string modelPath)
		{
			return Invoke(_identifyModel, channel, serviceSessionToken, modelPath, true);
		}

		/// <summary>
		/// LockData(token, 129, allowNonExclusive: true, ModelVersion(VersionNumber(0), ModelHistoryCheckInfo(EpisodeGuid.Invalid)), out creationDate).
		/// Returns the LockStatus name.
		/// </summary>
		public string LockData(object channel, object modelSessionToken, out object creationDate)
		{
			var episodeInvalid = _episodeGuidInvalid.GetValue(null);
			var localVersion = Invoke(_modelVersionCtor, Invoke(_versionNumberCtor, 0), Invoke(_historyCheckInfoCtor, episodeInvalid));
			var args = new object[] { modelSessionToken, ReadLockOptions, true, localVersion, null };
			var status = InvokeWithArgs(_lockData, channel, args);
			creationDate = args[4];
			return status?.ToString();
		}

		/// <summary>
		/// Returns the LockStatus name.
		/// </summary>
		public string UnlockData(object channel, object modelSessionToken)
		{
			return Invoke(_unlockData, channel, modelSessionToken)?.ToString();
		}

		/// <summary>
		/// Returns the ValidationStatus name; <paramref name="files"/> gets the model data file names.
		/// </summary>
		public string GetListOfModelDataFiles(object channel, object modelSessionToken, out List<string> files)
		{
			var args = new object[] { modelSessionToken, null };
			var status = InvokeWithArgs(_getListOfModelDataFiles, channel, args);
			files = (args[1] as IEnumerable)?.Cast<object>().Where(x => x != null).Select(x => x.ToString()).ToList() ?? new List<string>();
			return status?.ToString();
		}

		/// <summary>
		/// Returns the boxed DataFormatVersion of the model.
		/// </summary>
		public object ModelDataFormatVersion(object channel, object modelSessionToken)
		{
			return Invoke(_modelDataFormatVersion, channel, modelSessionToken);
		}

		/// <summary>
		/// Starts a download; the caller reads <paramref name="stream"/> and disposes the returned response.
		/// </summary>
		public IDisposable DownloadFile(object channel, object request, out Stream stream)
		{
			var response = Invoke(_downloadFile, channel, request);
			if (response == null) throw new InvalidOperationException("DownloadFile returned no response.");
			stream = (Stream)_downloadResponseStream.GetValue(response);
			return (IDisposable)response;
		}

		// ---- Helper: RVT assembly and BasicFileInfo ----

		/// <summary>
		/// Builds the RVT from the downloaded model data with the Helper: ModelDataVersionManager.GetLatestStreamFiles + RvtFile.GenerateRvtFileFromModelFolder.
		/// </summary>
		public void GenerateRvtFile(string modelDataDirectory, object dataFormatVersion, string rvtPath)
		{
			IDictionary<string, string> nonElementStreams = new Dictionary<string, string>();
			IDictionary<int, string> elementStreams = new Dictionary<int, string>();
			IDictionary<int, string> steelIncrementStreams = new Dictionary<int, string>();
			var versionManager = Invoke(_versionManagerCtor, modelDataDirectory, dataFormatVersion);
			try
			{
				var sharedUtils = _sharedUtilsInstance.GetValue(null);
				_versionManagerModelPathUtils.SetValue(versionManager, _sharedUtilsModelPathUtils.GetValue(sharedUtils));
				var args = new object[] { nonElementStreams, elementStreams, steelIncrementStreams };
				if (!(bool)InvokeWithArgs(_getLatestStreamFiles, versionManager, args))
					throw new InvalidOperationException($"ModelDataVersionManager.GetLatestStreamFiles returned false for the downloaded model data in '{modelDataDirectory}'.");
				nonElementStreams = (IDictionary<string, string>)args[0];
				elementStreams = (IDictionary<int, string>)args[1];
				steelIncrementStreams = (IDictionary<int, string>)args[2];
			}
			finally
			{
				(versionManager as IDisposable)?.Dispose();
			}

			var generated = (bool)Invoke(_generateRvtFile, null, nonElementStreams, elementStreams, steelIncrementStreams, dataFormatVersion, rvtPath);
			if (!generated || !File.Exists(rvtPath))
				throw new InvalidOperationException($"RvtFile.GenerateRvtFileFromModelFolder did not create '{rvtPath}'.");
		}

		/// <summary>
		/// Turns the BasicFileInfo of <paramref name="rvtPath"/> into the one RevitServerTool writes for a local copy
		/// (SharedUtils.GenerateRvtFileFromModelDataFolder with bCreateLocal): CentralIdentity = central model identity,
		/// WorksharingState = WS_CreatedLocal, CentralPath = <paramref name="centralPath"/>, Identity = a new model identity.
		/// Uses the Helper's ModelBasicFileInfoStream; every step is checked.
		/// </summary>
		public void MakeCreatedLocal(string rvtPath, object dataFormatVersion, object centralIdentity, string centralPath)
		{
			if (!IsValidIdentity(centralIdentity))
				throw new InvalidOperationException("The central model identity returned by IdentifyModel is not valid.");

			var stream = Invoke(_basicFileInfoStreamCtor, dataFormatVersion);
			var info = Invoke(_readBasicFileInfo, stream, rvtPath);
			if (info == null)
				throw new InvalidOperationException($"The BasicFileInfo stream of '{rvtPath}' could not be read.");
			var version = Convert.ToInt32(_bfiVersion.GetValue(info));
			if (version >= _bfiStreamVersionMax)
				throw new NotSupportedException(
					$"The model's BasicFileInfo stream version {version} is newer than the Revit Server client assemblies in '{_directory}' support "
					+ $"(up to {_bfiStreamVersionMax - 1}); rewriting it would drop fields. Use a newer set of assemblies (AssembliesPath) or export without CreateLocal.");

			_bfiCentralIdentity.SetValue(info, centralIdentity);
			_bfiWorksharingState.SetValue(info, _worksharingStateCreatedLocal);
			_bfiCentralPath.SetValue(info, centralPath);
			_bfiIdentity.SetValue(info, _modelIdentityNew.GetValue(null));
			if (!(bool)Invoke(_writeBasicFileInfo, stream, rvtPath, info))
				throw new InvalidOperationException($"ModelBasicFileInfoStream.WriteToRVTFile failed for '{rvtPath}'.");

			var written = Invoke(_readBasicFileInfo, Invoke(_basicFileInfoStreamCtor, dataFormatVersion), rvtPath);
			if (written == null || !Equals(_bfiWorksharingState.GetValue(written), _worksharingStateCreatedLocal)
				|| !string.Equals((string)_bfiCentralPath.GetValue(written), centralPath, StringComparison.Ordinal))
				throw new InvalidOperationException($"The BasicFileInfo written to '{rvtPath}' could not be read back with the new values.");
		}

		/// <summary>
		/// Creates the Autodesk Windsor container (CastleClassFactory; releasing an object it does not track changes nothing).
		/// The Helper creates the same container when it assembles the RVT, after the whole model has been downloaded; it needs
		/// Castle.Core and Castle.Windsor 3.2.0.0 of the set. A host that binds Castle.Core to another version (a Castle.Core 4.x/5.x
		/// dependency on .NET, or a bindingRedirect on .NET Framework) fails here, before any server call.
		/// </summary>
		private void CheckWindsorContainer(PropertyInfo factoryInstance, MethodInfo release)
		{
			try
			{
				release.Invoke(factoryInstance.GetValue(null), new object[] { new object() });
			}
			catch (Exception ex)
			{
				var cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
				var castle = string.Join("; ", AppDomain.CurrentDomain.GetAssemblies()
					.Where(a => a.GetName().Name.StartsWith("Castle.", StringComparison.OrdinalIgnoreCase))
					.Select(a => a.FullName + " from " + (a.IsDynamic || string.IsNullOrEmpty(a.Location) ? "<no file>" : a.Location)));
				throw new InvalidOperationException(
					$"The Revit Server client assemblies in '{_directory}' need Castle.Core and Castle.Windsor 3.2.0.0, but the Windsor container could not be "
					+ $"created in this process (loaded Castle assemblies: {(castle.Length == 0 ? "none" : castle)}): {cause.GetType().Name}: {cause.Message} "
					+ "The host probably binds Castle.Core to another version (a Castle.Core 4.x/5.x dependency on .NET, or a bindingRedirect on .NET Framework); "
					+ "run the direct export in a process without it.", cause);
			}
		}

		// ---- reflection helpers ----

		private static object Invoke(ConstructorInfo ctor, params object[] args)
		{
			try
			{
				return ctor.Invoke(args);
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
				throw;
			}
		}

		private static object Invoke(MethodInfo method, object target, params object[] args)
		{
			return InvokeWithArgs(method, target, args);
		}

		/// <summary>
		/// Invokes the method with the given array (out/ref values are written back to it) and rethrows the method's own exception.
		/// </summary>
		private static object InvokeWithArgs(MethodInfo method, object target, object[] args)
		{
			try
			{
				return method.Invoke(target, args);
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
				throw;
			}
		}

		private MethodInfo Method(Type type, string name, params Type[] parameters)
		{
			var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, null, parameters, null);
			if (method == null)
				throw Mismatch($"{type.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))})");
			return method;
		}

		private ConstructorInfo Ctor(Type type, params Type[] parameters)
		{
			var ctor = type.GetConstructor(parameters);
			if (ctor == null)
				throw Mismatch($"{type.FullName}..ctor({string.Join(", ", parameters.Select(p => p.Name))})");
			return ctor;
		}

		private PropertyInfo Property(Type type, string name, BindingFlags scope)
		{
			var property = type.GetProperty(name, BindingFlags.Public | scope);
			if (property == null)
				throw Mismatch($"{type.FullName}.{name}");
			return property;
		}

		private object EnumValue(Type enumType, string name)
		{
			if (!enumType.IsEnum || !Enum.GetNames(enumType).Contains(name))
				throw Mismatch($"{enumType.FullName}.{name}");
			return Enum.Parse(enumType, name);
		}

		private InvalidOperationException Mismatch(string member)
		{
			return new InvalidOperationException($"The Revit Server client assemblies in '{_directory}' do not match the direct export: {member} is missing.");
		}
	}
}
