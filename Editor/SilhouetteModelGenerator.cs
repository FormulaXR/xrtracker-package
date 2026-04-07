using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Editor utilities for generating silhouette model.
	/// Delegates to SilhouetteModelGeneratorCore for runtime-compatible operations.
	/// </summary>
	public static class SilhouetteModelGenerator
	{
		/// <summary>
		/// Generate silhouette model file from mesh data.
		/// </summary>
		public static Task<bool> GenerateToFileAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale,
			string outputPath)
			=> SilhouetteModelGeneratorCore.GenerateToFileAsync(meshFilters, rootTransform, modelSettings, geometryScale, outputPath);

		/// <summary>
		/// Generate silhouette model and return bytes directly.
		/// </summary>
		public static Task<byte[]> GenerateToBytesAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale)
			=> SilhouetteModelGeneratorCore.GenerateToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);

		/// <summary>
		/// Compute a hash of the mesh data for change detection.
		/// </summary>
		public static string ComputeMeshHash(IList<MeshFilter> meshFilters, Transform rootTransform)
			=> SilhouetteModelGeneratorCore.ComputeMeshHash(meshFilters, rootTransform);

		/// <summary>
		/// Check if two ModelSettings match.
		/// </summary>
		public static bool SettingsMatch(ModelSettings a, ModelSettings b)
			=> SilhouetteModelGeneratorCore.SettingsMatch(a, b);

		/// <summary>
		/// Generate depth model and return bytes directly.
		/// </summary>
		public static Task<byte[]> GenerateDepthModelToBytesAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale)
			=> SilhouetteModelGeneratorCore.GenerateDepthModelToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);

		/// <summary>
		/// Generate a TrackingModelAsset and save it as a new .asset file.
		/// When includeDepthModel is true, uses combined generation (shared Vulkan device)
		/// to avoid context churn that causes hangs on large meshes.
		/// </summary>
		public static async Task<TrackingModelAsset> GenerateAndSaveAssetAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale,
			string assetPath,
			bool includeSilhouetteModel = true,
			bool includeDepthModel = false)
		{
			string meshHash = ComputeMeshHash(meshFilters, rootTransform);
			var asset = ScriptableObject.CreateInstance<TrackingModelAsset>();

			if (includeSilhouetteModel && includeDepthModel)
			{
				// Combined generation: one native call, shared Vulkan device
				var (silhouetteData, depthData) = await SilhouetteModelGeneratorCore
					.GenerateTrackingModelsToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);

				if (silhouetteData == null)
					return null;

				asset.SetSilhouetteModelData(silhouetteData, meshHash, modelSettings, geometryScale);

				if (depthData != null)
				{
					asset.SetDepthModelData(depthData);
					Debug.Log($"[SilhouetteModelGenerator] Added depth model: {depthData.Length} bytes");
				}
				else
				{
					Debug.LogWarning("[SilhouetteModelGenerator] Failed to generate depth model");
				}
			}
			else if (includeSilhouetteModel)
			{
				// Silhouette-only generation
				byte[] data = await GenerateToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);
				if (data == null)
					return null;

				asset.SetSilhouetteModelData(data, meshHash, modelSettings, geometryScale);
			}
			else if (includeDepthModel)
			{
				// Depth-only generation (edge + depth mode)
				byte[] depthData = await GenerateDepthModelToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);
				if (depthData == null)
					return null;

				// Store mesh hash and settings even for depth-only
				asset.SetMetadata(meshHash, modelSettings, geometryScale);
				asset.SetDepthModelData(depthData);
				Debug.Log($"[SilhouetteModelGenerator] Generated depth-only model: {depthData.Length} bytes");
			}

			AssetDatabase.CreateAsset(asset, assetPath);
			AssetDatabase.SaveAssets();

			return asset;
		}

		/// <summary>
		/// Update an existing TrackingModelAsset with new model data.
		/// When includeDepthModel is true, uses combined generation (shared Vulkan device).
		/// </summary>
		public static async Task<bool> UpdateExistingAssetAsync(
			TrackingModelAsset existingAsset,
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale,
			bool includeSilhouetteModel = true,
			bool includeDepthModel = false)
		{
			string meshHash = ComputeMeshHash(meshFilters, rootTransform);

			if (includeSilhouetteModel && includeDepthModel)
			{
				// Combined generation: one native call, shared Vulkan device
				var (silhouetteData, depthData) = await SilhouetteModelGeneratorCore
					.GenerateTrackingModelsToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);

				if (silhouetteData == null)
					return false;

				existingAsset.SetSilhouetteModelData(silhouetteData, meshHash, modelSettings, geometryScale);

				if (depthData != null)
				{
					existingAsset.SetDepthModelData(depthData);
					Debug.Log($"[SilhouetteModelGenerator] Updated depth model: {depthData.Length} bytes");
				}
				else
				{
					Debug.LogWarning("[SilhouetteModelGenerator] Failed to generate depth model");
				}
			}
			else if (includeSilhouetteModel)
			{
				// Silhouette-only generation
				byte[] data = await GenerateToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);
				if (data == null)
					return false;

				existingAsset.SetSilhouetteModelData(data, meshHash, modelSettings, geometryScale);
				existingAsset.ClearDepthModel();
			}
			else if (includeDepthModel)
			{
				// Depth-only generation (edge + depth mode)
				byte[] depthData = await GenerateDepthModelToBytesAsync(meshFilters, rootTransform, modelSettings, geometryScale);
				if (depthData == null)
					return false;

				existingAsset.SetMetadata(meshHash, modelSettings, geometryScale);
				existingAsset.ClearSilhouetteModel();
				existingAsset.SetDepthModelData(depthData);
			}

			EditorUtility.SetDirty(existingAsset);
			AssetDatabase.SaveAssets();

			return true;
		}

		/// <summary>
		/// Generate or update tracking model for a TrackedBody.
		/// If asset already assigned, updates it in place. Otherwise asks for save location.
		/// </summary>
		public static async void GenerateForTrackedBody(TrackedBody trackedBody)
		{
			if (trackedBody == null) return;

			var meshFilters = trackedBody.MeshFilters;
			var modelSettings = trackedBody.ModelSettings;
			var existingAsset = trackedBody.TrackingModelAsset;
			var enableDepthTracking = trackedBody.EnableDepthTracking;
			var includeSilhouette = trackedBody.EnableSilhouetteTracking;

			if (meshFilters == null || meshFilters.Count == 0)
			{
				Debug.LogError("[TrackedBody] No MeshFilters assigned");
				return;
			}

			if (modelSettings == null)
			{
				Debug.LogError("[TrackedBody] ModelSettings is null");
				return;
			}

			float geometryScale = trackedBody.transform.lossyScale.x;

			// If asset already exists, check if update is needed
			if (existingAsset != null)
			{
				string currentHash = ComputeMeshHash(meshFilters, trackedBody.transform);
				bool hashMatch = existingAsset.SourceMeshHash == currentHash;
				bool settingsMatch = SettingsMatch(existingAsset.ModelSettings, modelSettings);
				bool scaleMatch = Mathf.Approximately(existingAsset.GeneratedAtScale, geometryScale);
				bool depthMatch = enableDepthTracking == existingAsset.HasValidDepthModel;

				if (hashMatch && settingsMatch && scaleMatch && depthMatch)
				{
					Debug.Log("[TrackedBody] Tracking model is up to date, no regeneration needed.");
					EditorGUIUtility.PingObject(existingAsset);
					return;
				}

				string reason = !hashMatch ? "mesh changed" : (!settingsMatch ? "settings changed" : (!scaleMatch ? "scale changed" : "depth setting changed"));
				Debug.Log($"[TrackedBody] Regenerating tracking model ({reason})...");

				string progressTitle1 = enableDepthTracking ? "Updating Tracking Model (with depth)" : "Updating Tracking Model";
				EditorUtility.DisplayProgressBar(progressTitle1, "Please wait...", 0.5f);

				try
				{
					bool success = await UpdateExistingAssetAsync(existingAsset, meshFilters, trackedBody.transform, modelSettings, geometryScale, includeSilhouette, enableDepthTracking);
					if (success)
					{
						Debug.Log($"[TrackedBody] Updated tracking model: {AssetDatabase.GetAssetPath(existingAsset)}");
						EditorGUIUtility.PingObject(existingAsset);
					}
				}
				finally
				{
					EditorUtility.ClearProgressBar();
				}
				return;
			}

			// No existing asset - ask for save location
			string defaultName = trackedBody.BodyId + "_TrackingModel.asset";
			string path = EditorUtility.SaveFilePanelInProject(
				"Save Tracking Model Asset",
				defaultName,
				"asset",
				"Choose location for the tracking model asset");

			if (string.IsNullOrEmpty(path))
				return;

			string progressTitle = enableDepthTracking ? "Generating Tracking Model (with depth)" : "Generating Tracking Model";
			EditorUtility.DisplayProgressBar(progressTitle, "Please wait...", 0.5f);

			try
			{
				var asset = await GenerateAndSaveAssetAsync(meshFilters, trackedBody.transform, modelSettings, geometryScale, path, includeSilhouette, enableDepthTracking);
				if (asset != null)
				{
					// Assign to TrackedBody
					trackedBody.TrackingModelAsset = asset;
					EditorUtility.SetDirty(trackedBody);

					AssetDatabase.Refresh();
					Selection.activeObject = asset;
					EditorGUIUtility.PingObject(asset);

					Debug.Log($"[TrackedBody] Generated tracking model: {path}");
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}
	}
}
