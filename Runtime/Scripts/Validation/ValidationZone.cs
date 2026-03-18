using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Events;
using IV.FormulaTracker;  // For FTErrorCode

namespace IV.FormulaTracker.Validation
{
	/// <summary>
	/// Type of validator to use for this zone
	/// </summary>
	public enum ValidatorType
	{
		Template,
		Histogram
	}

	/// <summary>
	/// Defines a 3D region on a tracked body for CV validation.
	/// Attach this component as a child of a TrackedBody to define where validation should occur.
	/// </summary>
	[DefaultExecutionOrder(2)]
	public class ValidationZone : MonoBehaviour
	{
		#region Serialized Fields

		[Tooltip("Unique identifier for this validation zone")]
		[SerializeField]
		private string _zoneId;

		[Tooltip("Shape of the validation zone")]
		[SerializeField]
		private FTZoneShape _shape = FTZoneShape.Cylinder;

		[Header("Dimensions")]
		[Tooltip("For Cylinder: radius. For Box/Quad: extent along local X axis (vertical in sampled output)")]
		[SerializeField]
		private float _dimensionX = 0.02f;

		[Tooltip("For all shapes: extent along local Y axis (horizontal in sampled output). Rotate zone so Y points along desired horizontal direction.")]
		[SerializeField]
		private float _dimensionY = 0.05f;

		[Tooltip("For Box: depth. Unused for Cylinder/Quad")]
		[SerializeField]
		private float _dimensionZ;

		[Header("Validation")]
		[Tooltip("Minimum tracking quality (0-1) required to run validation")]
		[Range(0f, 1f)]
		[SerializeField]
		private float _trackingQualityThreshold = 0.5f;

		[Tooltip("Type of validator to use")]
		[SerializeField]
		private ValidatorType _validatorType = ValidatorType.Template;

		[Tooltip("Template image for validation (RGBA format, alpha channel used as mask)")]
		[SerializeField]
		private Texture2D _templateTexture;

		[Tooltip("Correlation threshold to pass validation (0-1)")]
		[Range(0f, 1f)]
		[SerializeField]
		private float _passThreshold = 0.7f;

		[Header("Template Validator Settings")]
		[Tooltip("Use alpha channel as comparison mask (transparent regions ignored)")]
		[SerializeField]
		private bool _useAlphaMask = true;

		[Header("Histogram Validator Settings")]
		[Tooltip("Histogram comparison method")]
		[SerializeField]
		private FTHistogramCompareMethod _histogramMethod = FTHistogramCompareMethod.Correlation;

		[Tooltip("Use only Hue channel (more robust to lighting changes)")]
		[SerializeField]
		private bool _useHueOnly;

		[Tooltip("Number of histogram bins per channel")]
		[Range(8, 256)]
		[SerializeField]
		private int _numBins = 32;

		[Header("Auto Validation")]
		[Tooltip("Automatically run validation each frame when tracking quality is sufficient")]
		[SerializeField]
		private bool _autoValidate;

		[Tooltip("Minimum interval between auto-validations (seconds)")]
		[SerializeField]
		private float _autoValidateInterval = 0.1f;

		[Header("Events")]
		[SerializeField]
		private UnityEvent<ValidationResult> _onValidationComplete = new();

		[SerializeField]
		private UnityEvent _onValidationPassed = new();

		[SerializeField]
		private UnityEvent _onValidationFailed = new();

		#endregion

		#region Private Fields

		private TrackedBody _parentBody;
		private bool _isRegistered;
		private bool _validatorAdded;
		private string _validatorId;
		private GCHandle _templateHandle;
		private float _lastAutoValidateTime;
		private ValidationResult _lastValidationResult;

		#endregion

		#region Public Properties

		public string ZoneId
		{
			get => _zoneId;
			set => _zoneId = value;
		}

		public FTZoneShape Shape
		{
			get => _shape;
			set => _shape = value;
		}

		public Vector3 Dimensions
		{
			get => new Vector3(_dimensionX, _dimensionY, _dimensionZ);
			set
			{
				_dimensionX = value.x;
				_dimensionY = value.y;
				_dimensionZ = value.z;
			}
		}

		public float TrackingQualityThreshold
		{
			get => _trackingQualityThreshold;
			set => _trackingQualityThreshold = Mathf.Clamp01(value);
		}

		public Texture2D TemplateTexture
		{
			get => _templateTexture;
			set => _templateTexture = value;
		}

		public float PassThreshold
		{
			get => _passThreshold;
			set => _passThreshold = Mathf.Clamp01(value);
		}

		public bool UseAlphaMask
		{
			get => _useAlphaMask;
			set => _useAlphaMask = value;
		}

		public ValidatorType ValidatorType
		{
			get => _validatorType;
			set => _validatorType = value;
		}

		public FTHistogramCompareMethod HistogramMethod
		{
			get => _histogramMethod;
			set => _histogramMethod = value;
		}

		public bool UseHueOnly
		{
			get => _useHueOnly;
			set => _useHueOnly = value;
		}

		public int NumBins
		{
			get => _numBins;
			set => _numBins = Mathf.Clamp(value, 8, 256);
		}

		public bool IsRegistered => _isRegistered;

		public TrackedBody ParentBody => _parentBody;

		public UnityEvent<ValidationResult> OnValidationComplete => _onValidationComplete;
		public UnityEvent OnValidationPassed => _onValidationPassed;
		public UnityEvent OnValidationFailed => _onValidationFailed;

		public bool AutoValidate
		{
			get => _autoValidate;
			set => _autoValidate = value;
		}

		public float AutoValidateInterval
		{
			get => _autoValidateInterval;
			set => _autoValidateInterval = Mathf.Max(0f, value);
		}

		/// <summary>
		/// The most recent validation result (from auto-validation or manual ProcessValidation call)
		/// </summary>
		public ValidationResult LastValidationResult => _lastValidationResult;

		#endregion

		#region Unity Lifecycle

		private void OnEnable()
		{
			if (string.IsNullOrEmpty(_zoneId))
				_zoneId = gameObject.name;

			_validatorId = _zoneId + "_validator";

			// Find parent TrackedBody
			_parentBody = GetComponentInParent<TrackedBody>();
			if (_parentBody == null)
			{
				Debug.LogError($"ValidationZone '{_zoneId}': Must be a child of a TrackedBody", this);
				return;
			}

			// Subscribe to tracking manager events
			if (XRTrackerManager.Instance != null)
			{
				XRTrackerManager.OnTrackerInitialized += OnTrackerInitialized;
				XRTrackerManager.OnTrackerShutdown += OnTrackerShutdown;

				if (XRTrackerManager.Instance.IsInitialized)
					RegisterZone();
			}
		}

		private void OnDisable()
		{
			if (XRTrackerManager.Instance != null)
			{
				XRTrackerManager.OnTrackerInitialized -= OnTrackerInitialized;
				XRTrackerManager.OnTrackerShutdown -= OnTrackerShutdown;
			}

			UnregisterZone();
		}

		private void OnDestroy()
		{
			FreeTemplateHandle();
			if (_sampledImageTexture != null)
				UnityEngine.Object.Destroy(_sampledImageTexture);
		}

		private void Update()
		{
			// Try to register if not yet registered
			if (!_isRegistered && _parentBody != null && _parentBody.IsRegistered)
			{
				RegisterZone();
			}

			if (!_autoValidate || !_isRegistered || _parentBody == null)
				return;

			// Check interval
			if (Time.time - _lastAutoValidateTime < _autoValidateInterval)
				return;

			_lastAutoValidateTime = Time.time;

			// Get tracking quality from parent body
			float trackingQuality = _parentBody.TrackingQuality;
			_lastValidationResult = ProcessValidation(trackingQuality);
		}

		#endregion

		#region Registration

		private void OnTrackerInitialized()
		{
			RegisterZone();
		}

		private void OnTrackerShutdown()
		{
			_isRegistered = false;
			_validatorAdded = false;
			FreeTemplateHandle();
		}

		private void RegisterZone()
		{
			if (_isRegistered) return;
			if (_parentBody == null || !_parentBody.IsRegistered)
			{
				// Parent not ready yet, will be registered when parent is ready
				return;
			}

			var config = FTValidationZoneConfig.Default();
			config.zone_id = _zoneId;
			config.body_name = _parentBody.BodyId;
			config.shape = (int)_shape;

			// Get local pose relative to parent body
			var localPos = _parentBody.transform.InverseTransformPoint(transform.position);
			var localRot = Quaternion.Inverse(_parentBody.transform.rotation) * transform.rotation;

			// Convert to tracker coordinate system (same as TrackedBody)
			config.pos_x = localPos.x;
			config.pos_y = -localPos.y;
			config.pos_z = localPos.z;
			config.rot_x = localRot.x;
			config.rot_y = -localRot.y;
			config.rot_z = localRot.z;
			config.rot_w = -localRot.w;

			config.dim_x = _dimensionX;
			config.dim_y = _dimensionY;
			config.dim_z = _dimensionZ;
			config.tracking_quality_threshold = _trackingQualityThreshold;

			int result = ValidationBridge.FT_CreateValidationZone(ref config);
			if (result != FTErrorCode.OK)
			{
				Debug.LogError($"ValidationZone '{_zoneId}': Failed to create zone, error {result}", this);
				return;
			}

			_isRegistered = true;
			Debug.Log($"ValidationZone '{_zoneId}': Registered on body '{_parentBody.BodyId}'", this);

			// Add validator if texture is assigned
			if (_templateTexture != null)
				AddValidator();
		}

		private void UnregisterZone()
		{
			if (!_isRegistered) return;

			ValidationBridge.FT_RemoveValidationZone(_zoneId);
			_isRegistered = false;
			_validatorAdded = false;
			FreeTemplateHandle();

			Debug.Log($"ValidationZone '{_zoneId}': Unregistered", this);
		}

		private void AddValidator()
		{
			if (!_isRegistered || _validatorAdded || _templateTexture == null) return;

			// Get RGBA pixel data
			Color32[] pixels = _templateTexture.GetPixels32();
			byte[] rgbaData = new byte[pixels.Length * 4];

			for (int i = 0; i < pixels.Length; i++)
			{
				rgbaData[i * 4 + 0] = pixels[i].r;
				rgbaData[i * 4 + 1] = pixels[i].g;
				rgbaData[i * 4 + 2] = pixels[i].b;
				rgbaData[i * 4 + 3] = pixels[i].a;
			}

			// Pin the data
			_templateHandle = GCHandle.Alloc(rgbaData, GCHandleType.Pinned);

			int result;
			string validatorTypeName;

			if (_validatorType == ValidatorType.Template)
			{
				var validatorConfig = FTTemplateValidatorConfig.Default();
				validatorConfig.validator_id = _validatorId;
				validatorConfig.use_alpha_mask = _useAlphaMask ? 1 : 0;

				result = ValidationBridge.FT_AddTemplateValidator(
					_zoneId,
					ref validatorConfig,
					_templateHandle.AddrOfPinnedObject(),
					_templateTexture.width,
					_templateTexture.height);

				validatorTypeName = "template";
			}
			else // Histogram
			{
				var validatorConfig = FTHistogramValidatorConfig.Default();
				validatorConfig.validator_id = _validatorId;
				validatorConfig.method = (int)_histogramMethod;
				validatorConfig.use_hue_only = _useHueOnly ? 1 : 0;
				validatorConfig.num_bins = _numBins;

				result = ValidationBridge.FT_AddHistogramValidator(
					_zoneId,
					ref validatorConfig,
					_templateHandle.AddrOfPinnedObject(),
					_templateTexture.width,
					_templateTexture.height);

				validatorTypeName = "histogram";
			}

			// Can free the handle now - native code copies the data
			FreeTemplateHandle();

			if (result != FTErrorCode.OK)
			{
				Debug.LogError($"ValidationZone '{_zoneId}': Failed to add {validatorTypeName} validator, error {result}", this);
				return;
			}

			_validatorAdded = true;
			Debug.Log($"ValidationZone '{_zoneId}': Added {validatorTypeName} validator", this);
		}

		private void FreeTemplateHandle()
		{
			if (_templateHandle.IsAllocated)
				_templateHandle.Free();
		}

		#endregion

		#region Validation

		/// <summary>
		/// Run validation on this zone.
		/// </summary>
		/// <param name="trackingQuality">Current tracking quality of parent body (0-1)</param>
		/// <returns>Validation result</returns>
		public ValidationResult ProcessValidation(float trackingQuality)
		{
			var result = new ValidationResult { ZoneId = _zoneId };

			if (!_isRegistered)
			{
				result.Readiness = FTValidationReadiness.TrackingLost;
				result.ReadinessMessage = "Zone not registered";
				return result;
			}

			int errorCode = ValidationBridge.FT_ProcessValidation(_zoneId, trackingQuality, out var nativeResult);
			if (errorCode != FTErrorCode.OK)
			{
				result.Readiness = FTValidationReadiness.TrackingLost;
				result.ReadinessMessage = $"Processing error: {errorCode}";
				return result;
			}

			result.Readiness = nativeResult.readiness.Status;
			result.ReadinessMessage = nativeResult.readiness.message;
			result.VisibilityScore = nativeResult.readiness.visibility_score;
			result.ValidationAttempted = nativeResult.ValidationAttempted;

			// Get validator results
			if (nativeResult.ValidationAttempted && nativeResult.num_validators > 0)
			{
				result.ValidatorResults = new ValidatorResult[nativeResult.num_validators];

				for (int i = 0; i < nativeResult.num_validators; i++)
				{
					if (ValidationBridge.FT_GetValidatorResult(_zoneId, i, out var vr) == FTErrorCode.OK)
					{
						// Unity determines pass/fail based on local threshold
						bool passed = vr.confidence >= _passThreshold;
						result.ValidatorResults[i] = new ValidatorResult
						{
							ValidatorId = vr.validator_id,
							Passed = passed,
							Confidence = vr.confidence,
							OffsetX = vr.offset_x,
							OffsetY = vr.offset_y,
							DetectedRotation = vr.detected_rotation
						};
					}
				}

				// Determine overall pass/fail
				result.Passed = true;
				foreach (var vr in result.ValidatorResults)
				{
					if (!vr.Passed)
					{
						result.Passed = false;
						break;
					}
				}
			}

			// Fire events
			_onValidationComplete?.Invoke(result);
			if (result.ValidationAttempted)
			{
				if (result.Passed)
					_onValidationPassed?.Invoke();
				else
					_onValidationFailed?.Invoke();
			}

			return result;
		}

		/// <summary>
		/// Check if zone is ready for validation without running validators.
		/// </summary>
		public FTValidationReadiness CheckReadiness(float trackingQuality)
		{
			if (!_isRegistered)
				return FTValidationReadiness.TrackingLost;

			if (ValidationBridge.FT_CheckZoneReadiness(_zoneId, trackingQuality, out var result) == FTErrorCode.OK)
				return result.Status;

			return FTValidationReadiness.TrackingLost;
		}

		/// <summary>
		/// Update zone parameters at runtime.
		/// </summary>
		public void UpdateZoneParameters()
		{
			if (!_isRegistered) return;

			var localPos = _parentBody.transform.InverseTransformPoint(transform.position);
			var localRot = Quaternion.Inverse(_parentBody.transform.rotation) * transform.rotation;

			ValidationBridge.FT_UpdateValidationZone(
				_zoneId,
				localPos.x, -localPos.y, localPos.z,
				localRot.x, -localRot.y, localRot.z, -localRot.w,
				_dimensionX, _dimensionY, _dimensionZ,
				_trackingQualityThreshold);
		}

		#endregion

		#region Debug Visualization

		// Cached sampled image texture
		private Texture2D _sampledImageTexture;
		private byte[] _sampledImageBuffer;
		private const int MAX_SAMPLED_WIDTH = 256;
		private const int MAX_SAMPLED_HEIGHT = 256;

		/// <summary>
		/// Get the current sampled image from the validation zone.
		/// This is useful for debugging and visualization.
		/// </summary>
		/// <returns>Texture2D with the sampled region, or null if not available</returns>
		public Texture2D GetSampledImage()
		{
			if (!_isRegistered) return null;

			// Allocate buffer if needed
			if (_sampledImageBuffer == null)
				_sampledImageBuffer = new byte[MAX_SAMPLED_WIDTH * MAX_SAMPLED_HEIGHT * 3];

			// Pin the buffer
			var handle = GCHandle.Alloc(_sampledImageBuffer, GCHandleType.Pinned);
			try
			{
				int actualWidth, actualHeight;
				int result = ValidationBridge.FT_GetSampledImage(
					_zoneId,
					handle.AddrOfPinnedObject(),
					MAX_SAMPLED_WIDTH,
					MAX_SAMPLED_HEIGHT,
					out actualWidth,
					out actualHeight);

				if (result != FTErrorCode.OK || actualWidth <= 0 || actualHeight <= 0)
					return null;

				// Create or resize texture if needed
				if (_sampledImageTexture == null ||
				    _sampledImageTexture.width != actualWidth ||
				    _sampledImageTexture.height != actualHeight)
				{
					// Don't destroy old texture here - let GC handle it to avoid
					// crashes when texture is being accessed during rendering
					_sampledImageTexture = new Texture2D(actualWidth, actualHeight, TextureFormat.RGB24, false);
					_sampledImageTexture.filterMode = FilterMode.Bilinear;
					_sampledImageTexture.wrapMode = TextureWrapMode.Clamp;
				}

				// Load raw RGB data into texture
				// Create correctly-sized array for the actual image
				int dataSize = actualWidth * actualHeight * 3;
				if (_sampledImageBuffer.Length >= dataSize)
				{
					var textureData = new byte[dataSize];
					System.Array.Copy(_sampledImageBuffer, textureData, dataSize);
					_sampledImageTexture.LoadRawTextureData(textureData);
					_sampledImageTexture.Apply();
				}

				return _sampledImageTexture;
			}
			finally
			{
				handle.Free();
			}
		}

		private void OnDrawGizmosSelected()
		{
			// Draw zone shape in editor
			// Gizmo is drawn in local coordinates:
			// - Local X extent = dim_x (for Quad/Box: vertical in sampled output)
			// - Local Y extent = dim_y (for all shapes: horizontal in sampled output)
			// The zone should be rotated so its local Y points along the "horizontal" direction
			Gizmos.color = new Color(0f, 1f, 0.5f, 0.3f);
			Gizmos.matrix = transform.localToWorldMatrix;

			switch (_shape)
			{
				case FTZoneShape.Cylinder:
					DrawCylinderGizmo();
					break;
				case FTZoneShape.Box:
					Gizmos.DrawWireCube(Vector3.zero, new Vector3(_dimensionX, _dimensionY, _dimensionZ));
					break;
				case FTZoneShape.Quad:
					Gizmos.DrawWireCube(Vector3.zero, new Vector3(_dimensionX, _dimensionY, 0.001f));
					break;
			}
		}

		private void DrawCylinderGizmo()
		{
			int segments = 24;
			float halfHeight = _dimensionY / 2f;

			// Draw top and bottom circles
			for (int i = 0; i < segments; i++)
			{
				float angle1 = (i / (float)segments) * 2f * Mathf.PI;
				float angle2 = ((i + 1) / (float)segments) * 2f * Mathf.PI;

				Vector3 p1Top = new Vector3(Mathf.Cos(angle1) * _dimensionX, halfHeight, Mathf.Sin(angle1) * _dimensionX);
				Vector3 p2Top = new Vector3(Mathf.Cos(angle2) * _dimensionX, halfHeight, Mathf.Sin(angle2) * _dimensionX);
				Vector3 p1Bot = new Vector3(Mathf.Cos(angle1) * _dimensionX, -halfHeight, Mathf.Sin(angle1) * _dimensionX);
				Vector3 p2Bot = new Vector3(Mathf.Cos(angle2) * _dimensionX, -halfHeight, Mathf.Sin(angle2) * _dimensionX);

				Gizmos.DrawLine(p1Top, p2Top);
				Gizmos.DrawLine(p1Bot, p2Bot);
			}

			// Draw vertical lines
			for (int i = 0; i < 8; i++)
			{
				float angle = (i / 8f) * 2f * Mathf.PI;
				Vector3 top = new Vector3(Mathf.Cos(angle) * _dimensionX, halfHeight, Mathf.Sin(angle) * _dimensionX);
				Vector3 bot = new Vector3(Mathf.Cos(angle) * _dimensionX, -halfHeight, Mathf.Sin(angle) * _dimensionX);
				Gizmos.DrawLine(top, bot);
			}
		}

		#endregion
	}

	#region Result Classes

	/// <summary>
	/// Result of validation for a zone
	/// </summary>
	[Serializable]
	public class ValidationResult
	{
		public string ZoneId;
		public FTValidationReadiness Readiness;
		public string ReadinessMessage;
		public float VisibilityScore;
		public bool ValidationAttempted;
		public bool Passed;
		public ValidatorResult[] ValidatorResults;
	}

	/// <summary>
	/// Result of a single validator
	/// </summary>
	[Serializable]
	public class ValidatorResult
	{
		public string ValidatorId;
		public bool Passed;        // Computed by Unity: confidence >= passThreshold
		public float Confidence;   // 0-1 correlation score from native
		public float OffsetX;
		public float OffsetY;
		public float DetectedRotation;
	}

	#endregion
}
