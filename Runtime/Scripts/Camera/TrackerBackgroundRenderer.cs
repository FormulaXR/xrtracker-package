using UnityEngine;
using UnityEngine.Rendering;

namespace IV.FormulaTracker
{
	public class TrackerBackgroundRenderer : MonoBehaviour
	{
		#region Serialized Fields

		[Header("Background Settings")]
		[SerializeField, Tooltip("Layer for background plane (ensure this layer exists)")]
		private int _backgroundLayer = 8;

		[SerializeField, Tooltip("Distance of background plane from camera")]
		private float _planeDistance = 0.1f;

		#endregion

		#region Public Properties

		public int BackgroundLayer
		{
			get => _backgroundLayer;
			set => _backgroundLayer = value;
		}

		public float PlaneDistance
		{
			get => _planeDistance;
			set => _planeDistance = value;
		}

		/// <summary>
		/// The created background camera. Read-only.
		/// </summary>
		public Camera BackgroundCamera => _backgroundCamera;

		/// <summary>
		/// The main camera used for rendering. Read-only.
		/// </summary>
		public Camera MainCamera => _mainCamera;

		#endregion

		#region Private Fields

		private Camera _mainCamera;
		private Camera _backgroundCamera;
		private Material _backgroundMaterial;
		private GameObject _backgroundPlane;
		private MeshRenderer _planeRenderer;
		private bool _isSetUp;
		private bool _isUsingOverlay;

		#endregion

		#region Private Properties

		private XRTrackerManager Manager => XRTrackerManager.Instance;

		#endregion

		#region Unity Lifecycle

		private void Start()
		{
			if (!_isSetUp)
				SetUp();

			if (Manager != null)
			{
				Manager.OnImage += UpdateTexture;
				Manager.OnCropFactorsChanged += UpdatePlanePosition;
			}
		}

		private void OnValidate()
		{
			if (Application.isPlaying && _isSetUp)
				UpdatePlanePosition();
		}

		private void LateUpdate()
		{
			// Sync targetTexture if main camera's RT changed (e.g., window resize)
			if (_isSetUp && _mainCamera != null && _backgroundCamera != null &&
			    _mainCamera.targetTexture != null &&
			    _backgroundCamera.targetTexture != _mainCamera.targetTexture)
			{
				_backgroundCamera.targetTexture = _mainCamera.targetTexture;
			}
		}

		private void OnDestroy()
		{
			if (Manager != null)
			{
				Manager.OnImage -= UpdateTexture;
				Manager.OnCropFactorsChanged -= UpdatePlanePosition;
			}

			// Restore main camera if we changed it to overlay
			if (_isUsingOverlay && _mainCamera != null)
			{
				RestoreMainCameraFromOverlay();
			}

			if (_backgroundMaterial != null)
			{
				Destroy(_backgroundMaterial);
				_backgroundMaterial = null;
			}

			if (_backgroundCamera != null)
			{
				Destroy(_backgroundCamera.gameObject);
				_backgroundCamera = null;
			}

			if (_backgroundPlane != null)
			{
				Destroy(_backgroundPlane);
				_backgroundPlane = null;
			}

			_isSetUp = false;
		}

		#endregion

		#region Public Methods

		public void UpdateTexture(Texture texture)
		{
			if (!_isSetUp)
				return;

			if (_backgroundMaterial != null)
				_backgroundMaterial.mainTexture = texture;
		}

		#endregion

		#region Private Methods

		private void SetUp()
		{
			if (_isSetUp)
				return;

			// Get main camera from TrackingManager
			_mainCamera = GetMainCameraFromManager();
			if (_mainCamera == null)
			{
				Debug.LogError("[TrackerBackgroundRenderer] No main camera found! Ensure XRTrackerManager has a camera assigned.", this);
				return;
			}

			CreateBackgroundObjects();
			ConfigureRenderPipeline();
			_mainCamera.cullingMask &= ~(1 << _backgroundLayer);

			_isSetUp = true;
		}

		private Camera GetMainCameraFromManager()
		{
			if (Manager != null && Manager.CameraTransform != null)
			{
				var cam = Manager.CameraTransform.GetComponent<Camera>();
				if (cam != null)
					return cam;
			}

			// Fallback to Camera.main
			return Camera.main;
		}

		private bool IsURP()
		{
			if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null)
				return false;
			return System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime") != null;
		}

		private void ConfigureRenderPipeline()
		{
			// Common background camera setup (all pipelines)
			_backgroundCamera.depth = _mainCamera.depth - 1;
			_backgroundCamera.clearFlags = CameraClearFlags.SolidColor;
			_backgroundCamera.backgroundColor = Color.black;

			// Copy targetTexture if main camera renders to RenderTexture
			if (_mainCamera.targetTexture != null)
				_backgroundCamera.targetTexture = _mainCamera.targetTexture;

			// URP overlay mode only when not rendering to RenderTexture
			if (IsURP() && _mainCamera.targetTexture == null && TryConfigureURPOverlay())
			{
				_isUsingOverlay = true;
				Debug.Log("[XRTracker] Configured for URP (Base + Overlay)");
				return;
			}

			// Depth-based for Built-in, HDRP, or URP with RenderTexture
			_mainCamera.clearFlags = CameraClearFlags.Depth;
			Debug.Log("[XRTracker] Configured with depth-based rendering");
		}

		private bool TryConfigureURPOverlay()
		{
			// URP uses UniversalAdditionalCameraData component
			// We need to use reflection to avoid hard dependency
			var urpDataType = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
			if (urpDataType == null)
				return false;

			var renderTypeProperty = urpDataType.GetProperty("renderType");
			if (renderTypeProperty == null)
				return false;

			// Get or add URP camera data to background camera, set as Base
			var bgCameraData = _backgroundCamera.GetComponent(urpDataType);
			if (bgCameraData == null)
				bgCameraData = _backgroundCamera.gameObject.AddComponent(urpDataType);
			renderTypeProperty.SetValue(bgCameraData, 0); // CameraRenderType.Base = 0

			// Get or add URP camera data to main camera, set as Overlay
			var mainCameraData = _mainCamera.GetComponent(urpDataType);
			if (mainCameraData == null)
				mainCameraData = _mainCamera.gameObject.AddComponent(urpDataType);
			renderTypeProperty.SetValue(mainCameraData, 1); // CameraRenderType.Overlay = 1

			// Add main camera to background camera's stack
			var cameraStackProperty = urpDataType.GetProperty("cameraStack");
			if (cameraStackProperty != null)
			{
				var stack = cameraStackProperty.GetValue(bgCameraData) as System.Collections.Generic.List<Camera>;
				if (stack != null && !stack.Contains(_mainCamera))
					stack.Add(_mainCamera);
			}

			return true;
		}

		private void RestoreMainCameraFromOverlay()
		{
			// Restore main camera from overlay mode (for URP)
			var urpDataType = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
			if (urpDataType == null)
				return;

			var mainCameraData = _mainCamera.GetComponent(urpDataType);
			if (mainCameraData == null)
				return;

			// Set back to Base
			var renderTypeProperty = urpDataType.GetProperty("renderType");
			if (renderTypeProperty != null)
			{
				renderTypeProperty.SetValue(mainCameraData, 0); // Base
			}

			// Restore clear flags
			_mainCamera.clearFlags = CameraClearFlags.Skybox;
		}

		private void CreateBackgroundCamera()
		{
			var cameraObject = new GameObject("FT_BackgroundCamera");
			cameraObject.transform.SetParent(transform);
			cameraObject.transform.localPosition = Vector3.zero;
			cameraObject.transform.localRotation = Quaternion.identity;

			_backgroundCamera = cameraObject.AddComponent<Camera>();
			_backgroundCamera.orthographic = true;
			_backgroundCamera.orthographicSize = 0.5f;
			_backgroundCamera.cullingMask = 1 << _backgroundLayer;
			_backgroundCamera.nearClipPlane = 0.01f;
			_backgroundCamera.farClipPlane = _planeDistance + 0.01f;
		}

		private void CreateBackgroundPlane()
		{
			_backgroundPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
			_backgroundPlane.name = "FT_BackgroundPlane";
			_backgroundPlane.transform.SetParent(transform);

			if (_backgroundPlane.TryGetComponent(out Collider cld))
				Destroy(cld);

			_backgroundMaterial = new Material(Shader.Find("Unlit/Texture"))
			{
				name = "FT_BackgroundMaterial"
			};

			_planeRenderer = _backgroundPlane.GetComponent<MeshRenderer>();
			_planeRenderer.material = _backgroundMaterial;

			_backgroundPlane.layer = _backgroundLayer;
			_planeRenderer.shadowCastingMode = ShadowCastingMode.Off;
			_planeRenderer.receiveShadows = false;
		}

		private void CreateBackgroundObjects()
		{
			CreateBackgroundCamera();
			CreateBackgroundPlane();
			UpdatePlanePosition();
		}

		private void UpdatePlanePosition()
		{
			if (_backgroundPlane == null || _backgroundCamera == null) return;

			_backgroundPlane.transform.position = _backgroundCamera.transform.position + _backgroundCamera.transform.forward * _planeDistance;
			_backgroundPlane.transform.rotation = _backgroundCamera.transform.rotation;

			float screenHeight = _backgroundCamera.orthographicSize * 2.0f;
			float screenWidth = screenHeight * _backgroundCamera.aspect;

			float cropX = Manager != null ? Manager.CropFactorX : 0f;
			float cropY = Manager != null ? Manager.CropFactorY : 0f;

			float planeWidth = cropX > 0 ? screenWidth / (1f - cropX) : screenWidth;
			float planeHeight = cropY > 0 ? screenHeight / (1f - cropY) : screenHeight;

			_backgroundPlane.transform.localScale = new Vector3(planeWidth, planeHeight, 1f);
		}

		#endregion
	}
}
