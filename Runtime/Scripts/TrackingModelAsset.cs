using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace IV.FormulaTracker
{
	/// <summary>
	/// ScriptableObject that stores pre-generated tracking models (silhouette + depth).
	/// This allows models to be embedded in Unity builds without requiring
	/// file system access at runtime.
	/// </summary>
	[CreateAssetMenu(fileName = "TrackingModel", menuName = "FormulaTracker/Tracking Model Asset")]
	[PreferBinarySerialization]
	public class TrackingModelAsset : ScriptableObject
	{
		#region Silhouette Model Data

		[Tooltip("The pre-generated silhouette model binary")]
		[FormerlySerializedAs("_modelData")]
		[FormerlySerializedAs("_regionModelData")]
		[SerializeField, HideInInspector]
		private byte[] _silhouetteModelData;

		[Tooltip("Hash of the source mesh used to generate this model")]
		[SerializeField, HideInInspector]
		private string _sourceMeshHash;

		[Tooltip("Model configuration used during generation")]
		[SerializeField]
		private ModelSettings _modelSettings = new ModelSettings();

		[Tooltip("Scale (lossyScale.x) used during generation")]
		[SerializeField, HideInInspector]
		private float _generatedAtScale = 1f;

		[Header("Silhouette Model Info")]
		[Tooltip("Size of the silhouette model data in bytes")]
		[FormerlySerializedAs("_dataSize")]
		[FormerlySerializedAs("_regionDataSize")]
		[SerializeField]
		private int _silhouetteDataSize;

		[Tooltip("When the silhouette model was generated")]
		[FormerlySerializedAs("_generatedDate")]
		[FormerlySerializedAs("_regionGeneratedDate")]
		[SerializeField]
		private string _silhouetteGeneratedDate;

		[Tooltip("Scale used during silhouette model generation")]
		[SerializeField]
		private float _displayScale = 1f;

		#endregion

		#region Depth Model Data

		[Tooltip("The pre-generated depth model binary")]
		[SerializeField, HideInInspector]
		private byte[] _depthModelData;

		[Header("Depth Model Info")]
		[Tooltip("Size of the depth model data in bytes")]
		[SerializeField]
		private int _depthDataSize;

		[Tooltip("When the depth model was generated")]
		[SerializeField]
		private string _depthGeneratedDate;

		[Tooltip("Whether depth model has been generated")]
		[SerializeField]
		private bool _hasDepthModel;

		#endregion

		#region Backward Compatibility (Legacy Property Names)

		// These properties maintain backward compatibility with code that used SilhouetteModelAsset

		/// <summary>
		/// The raw silhouette model data bytes (legacy: ModelData)
		/// </summary>
		public byte[] ModelData => _silhouetteModelData;

		/// <summary>
		/// Size of the silhouette model data in bytes (legacy: DataSize)
		/// </summary>
		public int DataSize => _silhouetteDataSize;

		#endregion

		#region Silhouette Model Properties

		/// <summary>
		/// The raw silhouette model data bytes
		/// </summary>
		public byte[] SilhouetteModelData => _silhouetteModelData;

		/// <summary>
		/// Size of the silhouette model data in bytes
		/// </summary>
		public int SilhouetteDataSize => _silhouetteDataSize;

		/// <summary>
		/// Hash of the source mesh
		/// </summary>
		public string SourceMeshHash
		{
			get => _sourceMeshHash;
			set => _sourceMeshHash = value;
		}

		/// <summary>
		/// Model settings used during generation
		/// </summary>
		public ModelSettings ModelSettings
		{
			get => _modelSettings;
			set => _modelSettings = value;
		}

		/// <summary>
		/// Scale (lossyScale.x) used when this model was generated
		/// </summary>
		public float GeneratedAtScale => _generatedAtScale;

		/// <summary>
		/// Check if the asset has any valid model data (silhouette or depth)
		/// </summary>
		public bool HasValidData => (_silhouetteModelData != null && _silhouetteModelData.Length > 0) || _hasDepthModel;

		/// <summary>
		/// Check if the asset has valid silhouette model data
		/// </summary>
		public bool HasValidSilhouetteModel => _silhouetteModelData != null && _silhouetteModelData.Length > 0;

		/// <summary>
		/// When the silhouette model was generated
		/// </summary>
		public string SilhouetteGeneratedDate => _silhouetteGeneratedDate;

		/// <summary>
		/// Display scale used during generation
		/// </summary>
		public float DisplayScale => _displayScale;

		#endregion

		#region Depth Model Properties

		/// <summary>
		/// The raw depth model data bytes
		/// </summary>
		public byte[] DepthModelData => _depthModelData;

		/// <summary>
		/// Size of the depth model data in bytes
		/// </summary>
		public int DepthDataSize => _depthDataSize;

		/// <summary>
		/// Check if the asset has valid depth model data
		/// </summary>
		public bool HasValidDepthModel => _hasDepthModel && _depthModelData != null && _depthModelData.Length > 0;

		/// <summary>
		/// When the depth model was generated
		/// </summary>
		public string DepthGeneratedDate => _depthGeneratedDate;

		#endregion

		#region Silhouette Model Methods

		/// <summary>
		/// Set silhouette model data (called by editor tooling)
		/// </summary>
		/// <param name="data">Binary model data</param>
		/// <param name="meshHash">Hash of source mesh</param>
		/// <param name="settings">Model settings used during generation</param>
		/// <param name="scale">Scale (lossyScale.x) used during generation</param>
		public void SetSilhouetteModelData(byte[] data, string meshHash, ModelSettings settings, float scale)
		{
			_silhouetteModelData = data;
			_sourceMeshHash = meshHash;
			_generatedAtScale = scale;
			// Copy values to avoid reference issues
			_modelSettings = new ModelSettings
			{
				sphereRadius = settings.sphereRadius,
				nDivides = settings.nDivides,
				nPoints = settings.nPoints,
				maxRadiusDepthOffset = settings.maxRadiusDepthOffset,
				strideDepthOffset = settings.strideDepthOffset,
				imageSize = settings.imageSize,
				viewpointPreset = settings.viewpointPreset,
				minElevation = settings.minElevation,
				maxElevation = settings.maxElevation,
				enableHorizontalFilter = settings.enableHorizontalFilter,
				minHorizontal = settings.minHorizontal,
				maxHorizontal = settings.maxHorizontal,
				forwardAxis = settings.forwardAxis
			};
			_silhouetteDataSize = data?.Length ?? 0;
			_silhouetteGeneratedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			_displayScale = scale;
		}

		/// <summary>
		/// Set silhouette model data (legacy method name for backward compatibility)
		/// </summary>
		public void SetModelData(byte[] data, string meshHash, ModelSettings settings, float scale)
		{
			SetSilhouetteModelData(data, meshHash, settings, scale);
		}

		/// <summary>
		/// Set just the silhouette model data bytes (for import scenarios)
		/// </summary>
		public void SetSilhouetteModelData(byte[] data)
		{
			_silhouetteModelData = data;
			_silhouetteDataSize = data?.Length ?? 0;
			_silhouetteGeneratedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
		}

		/// <summary>
		/// Set just the model data bytes (legacy method name)
		/// </summary>
		public void SetModelData(byte[] data)
		{
			SetSilhouetteModelData(data);
		}

		#endregion

			/// <summary>
		/// Set metadata without silhouette data (for depth-only assets).
		/// </summary>
		public void SetMetadata(string meshHash, ModelSettings settings, float scale)
		{
			_sourceMeshHash = meshHash;
			_generatedAtScale = scale;
			_displayScale = scale;
			_modelSettings = new ModelSettings
			{
				sphereRadius = settings.sphereRadius,
				nDivides = settings.nDivides,
				nPoints = settings.nPoints,
				maxRadiusDepthOffset = settings.maxRadiusDepthOffset,
				strideDepthOffset = settings.strideDepthOffset,
				imageSize = settings.imageSize,
				viewpointPreset = settings.viewpointPreset,
				minElevation = settings.minElevation,
				maxElevation = settings.maxElevation,
				enableHorizontalFilter = settings.enableHorizontalFilter,
				minHorizontal = settings.minHorizontal,
				maxHorizontal = settings.maxHorizontal,
				forwardAxis = settings.forwardAxis
			};
		}

		/// <summary>
		/// Clear silhouette model data only (keeps depth).
		/// </summary>
		public void ClearSilhouetteModel()
		{
			_silhouetteModelData = null;
			_silhouetteDataSize = 0;
			_silhouetteGeneratedDate = null;
		}

		#region Depth Model Methods

		/// <summary>
		/// Set depth model data (called by editor tooling)
		/// </summary>
		/// <param name="data">Binary depth model data</param>
		public void SetDepthModelData(byte[] data)
		{
			_depthModelData = data;
			_depthDataSize = data?.Length ?? 0;
			_depthGeneratedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			_hasDepthModel = data != null && data.Length > 0;
		}

		/// <summary>
		/// Clear depth model data
		/// </summary>
		public void ClearDepthModel()
		{
			_depthModelData = null;
			_depthDataSize = 0;
			_depthGeneratedDate = null;
			_hasDepthModel = false;
		}

		#endregion

		#region Clear All

		/// <summary>
		/// Clear all model data (silhouette + depth)
		/// </summary>
		public void ClearData()
		{
			// Clear silhouette model
			_silhouetteModelData = null;
			_sourceMeshHash = null;
			_generatedAtScale = 1f;
			_silhouetteDataSize = 0;
			_silhouetteGeneratedDate = null;
			_displayScale = 1f;

			// Clear depth model
			ClearDepthModel();
		}

		#endregion
	}
}
