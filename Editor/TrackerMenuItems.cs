using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
#if HAS_AR_FOUNDATION
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
#endif

namespace IV.FormulaTracker.Editor
{
	static class TrackerMenuItems
	{
#if HAS_AR_FOUNDATION
		[MenuItem("GameObject/XRTracker/AR Tracker", false, 10)]
		static void CreateARTracker(MenuCommand menuCommand)
		{
			// Root object
			var root = new GameObject("AR_Tracker");
			var manager = root.AddComponent<XRTrackerManager>();
			manager.CurrentImageSource = ImageSource.Injected;
			manager.UseARPoseFusion = true;

			// Reuse existing AR Session or create one under root (via Unity's internal util)
			var arSession = UnityEngine.Object.FindAnyObjectByType<ARSession>();
			if (arSession == null)
				InvokeARFoundationUtil("SceneUtils", "CreateARSessionWithParent", root.transform);

			// Reuse existing XR Origin or create one under root (via Unity's internal util)
			var xrOrigin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
			if (xrOrigin == null)
				xrOrigin = InvokeARFoundationUtil("XROriginCreateUtil", "CreateXROriginWithParent", root.transform) as XROrigin;

			// Ensure Device tracking origin (no floor offset interfering with pose)
			if (xrOrigin != null)
				xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;

			var mainCam = xrOrigin != null ? xrOrigin.Camera : null;

			// Ensure camera has ARFoundationCameraFeeder
			if (mainCam != null && mainCam.GetComponent<ARFoundationCameraFeeder>() == null)
			{
				var feeder = mainCam.gameObject.AddComponent<ARFoundationCameraFeeder>();
				var camMgr = mainCam.GetComponent<ARCameraManager>();
				if (camMgr != null) feeder.CameraManager = camMgr;
			}

			manager.MainCamera = mainCam;

			GameObjectUtility.SetParentAndAlign(root, menuCommand.context as GameObject);
			Undo.RegisterCreatedObjectUndo(root, "Create AR_Tracker");
			Selection.activeGameObject = root;
		}

		static object InvokeARFoundationUtil(string className, string methodName, Transform parent)
		{
			var assembly = Assembly.Load("Unity.XR.ARFoundation.Editor");
			var type = assembly.GetType($"UnityEditor.XR.ARFoundation.{className}");
			var method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			return method?.Invoke(null, new object[] { parent });
		}
#endif

		[MenuItem("GameObject/XRTracker/PC Tracker", false, 11)]
		static void CreatePCTracker(MenuCommand menuCommand)
		{
			// Parent object
			var root = new GameObject("PC_Tracker");
			var manager = root.AddComponent<XRTrackerManager>();
			root.AddComponent<TrackerBackgroundRenderer>();
			root.AddComponent<CameraSelectorUI>();

			// Use existing scene camera if available, otherwise create one as child
			var existingCam = Camera.main;
			if (existingCam != null)
			{
				manager.MainCamera = existingCam;
			}
			else
			{
				var camGo = new GameObject("Camera");
				camGo.tag = "MainCamera";
				camGo.transform.SetParent(root.transform, false);

				var cam = camGo.AddComponent<Camera>();
				cam.nearClipPlane = 0.01f;
				cam.farClipPlane = 10f;
				camGo.AddComponent<AudioListener>();

				manager.MainCamera = cam;
			}

			GameObjectUtility.SetParentAndAlign(root, menuCommand.context as GameObject);
			Undo.RegisterCreatedObjectUndo(root, "Create PC_Tracker");
			Selection.activeGameObject = root;
		}
	}
}
